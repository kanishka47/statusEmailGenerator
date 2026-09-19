using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

LoadLocalSettings();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static void LoadLocalSettings()
{
    var configuredPath = Environment.GetEnvironmentVariable("STATUS_UPDATER_SETTINGS");
    var settingsPath = string.IsNullOrWhiteSpace(configuredPath)
        ? Path.Combine(Environment.CurrentDirectory, "src", "ConsoleApp", "appsettings.local.json")
        : configuredPath;

    if (!File.Exists(settingsPath))
    {
        return;
    }

    var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(settingsPath))
        ?? throw new InvalidOperationException($"{settingsPath} does not contain a valid settings object.");

    foreach (var setting in settings)
    {
        if (!string.IsNullOrWhiteSpace(setting.Value) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(setting.Key)))
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }
    }
}

[McpServerToolType]
internal static class WorkHistoryTools
{
    [McpServerTool(Name = "generate_kanis_work_report", Idempotent = false)]
    [Description("Generate Kanishka's engineering report for an explicit inclusive date range. Optionally publish it to Azure AI Search so it is available to future searches.")]
    public static async Task<string> GenerateWorkReportAsync(
        [Description("Inclusive range start in yyyy-MM-dd format.")] string fromDate,
        [Description("Inclusive range end in yyyy-MM-dd format.")] string toDate,
        [Description("Whether to publish the generated report to Azure AI Search.")] bool publish = false,
        [Description("Azure DevOps author alias. Normally use kanis.")] string author = "kanis")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var fromInclusive = ParseDate(fromDate, nameof(fromDate));
        var toInclusive = ParseDate(toDate, nameof(toDate));
        if (fromInclusive > toInclusive)
        {
            throw new ArgumentException("fromDate must be on or before toDate.");
        }

        var consoleAssemblyPath = typeof(AzureAiSearchClient).Assembly.Location;
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(consoleAssemblyPath)!
        };
        startInfo.ArgumentList.Add(consoleAssemblyPath);
        startInfo.ArgumentList.Add("weekly");
        startInfo.ArgumentList.Add("--author");
        startInfo.ArgumentList.Add(author);
        startInfo.ArgumentList.Add("--start-date");
        startInfo.ArgumentList.Add(fromDate);
        startInfo.ArgumentList.Add("--end-date");
        startInfo.ArgumentList.Add(toDate);
        if (publish)
        {
            startInfo.ArgumentList.Add("--publish");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the report generator.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Report generation failed with exit code {process.ExitCode}: {standardError.Trim()}");
        }

        var report = ExtractGeneratedReport(standardOutput);
        var publicationStatus = publish ? "published to Azure AI Search" : "not published";
        return $"Generated report for {author} from {fromDate} through {toDate} ({publicationStatus}).\n\n{report}";
    }

    [McpServerTool(Name = "search_kanis_work_history", ReadOnly = true, Idempotent = true)]
    [Description("Search Kanishka's indexed weekly engineering reports. Use this for questions about work completed, SDK migration, pull requests, work items, risks, or progress during a date range.")]
    public static async Task<string> SearchWorkHistoryAsync(
        [Description("Natural-language question used for hybrid semantic and keyword retrieval.")] string question,
        [Description("Inclusive range start in yyyy-MM-dd format.")] string fromDate,
        [Description("Inclusive range end in yyyy-MM-dd format.")] string toDate,
        [Description("Indexed Azure DevOps author alias. Normally use kanis.")] string author = "kanis")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var fromInclusive = ParseDate(fromDate, nameof(fromDate));
        var toExclusive = ParseDate(toDate, nameof(toDate)).AddDays(1);
        if (fromInclusive >= toExclusive)
        {
            throw new ArgumentException("fromDate must be on or before toDate.");
        }

        var openAiClient = new AzureOpenAiClient(
            GetRequiredSetting("AZURE_OPENAI_ENDPOINT"),
            GetRequiredSetting("AZURE_OPENAI_API_KEY"),
            GetRequiredSetting("AZURE_OPENAI_DEPLOYMENT"),
            GetSettingOrDefault("AZURE_OPENAI_API_VERSION", "2024-10-21"));
        var embeddingDeployment = GetSettingOrDefault(
            "AZURE_OPENAI_EMBEDDING_DEPLOYMENT",
            "text-embedding-3-small");
        var searchClient = new AzureAiSearchClient(
            GetRequiredSetting("AZURE_SEARCH_ENDPOINT"),
            GetRequiredSetting("AZURE_SEARCH_API_KEY"),
            GetRequiredSetting("AZURE_SEARCH_INDEX_NAME"));

        var queryVector = await openAiClient.GenerateEmbeddingAsync(question, embeddingDeployment);
        var reports = await searchClient.SearchRelevantSummariesAsync(
            author,
            fromInclusive,
            toExclusive,
            question,
            queryVector,
            top: 8);

        if (reports.Count == 0)
        {
            return $"No indexed weekly reports were found for {author} from {fromDate} through {toDate}.";
        }

        var evidence = new StringBuilder();
        evidence.AppendLine($"Retrieved {reports.Count} weekly report(s) for {author} from {fromDate} through {toDate}.");
        evidence.AppendLine("Use only the report evidence below. Cite report IDs, PR numbers, and work-item numbers when present.");

        foreach (var report in reports)
        {
            var summary = string.IsNullOrWhiteSpace(report.GeneratedSummary)
                ? report.Content
                : report.GeneratedSummary;
            evidence.AppendLine();
            evidence.AppendLine($"Report: {report.Id}");
            evidence.AppendLine($"Week: {report.WeekStartUtc:yyyy-MM-dd} through {report.WeekEndUtc:yyyy-MM-dd}");
            evidence.AppendLine(summary);
        }

        return evidence.ToString().Trim();
    }

    private static string ExtractGeneratedReport(string output)
    {
        const string reportMarker = "===== WEEKLY REPORT (NO RAG) =====";
        var reportStart = output.IndexOf(reportMarker, StringComparison.Ordinal);
        if (reportStart < 0)
        {
            return output.Trim();
        }

        reportStart += reportMarker.Length;
        var reportEnd = output.IndexOf("\nReport publication is disabled.", reportStart, StringComparison.Ordinal);
        if (reportEnd < 0)
        {
            reportEnd = output.IndexOf("\nGenerating summary vector", reportStart, StringComparison.Ordinal);
        }

        return (reportEnd < 0 ? output[reportStart..] : output[reportStart..reportEnd]).Trim();
    }

    private static DateTime ParseDate(string value, string parameterName)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var result))
        {
            throw new ArgumentException($"{parameterName} must use yyyy-MM-dd format.");
        }

        return result;
    }

    private static string GetRequiredSetting(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Configuration value '{name}' is required.")
            : value;
    }

    private static string GetSettingOrDefault(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }
}