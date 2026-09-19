using DevOpsClient;
using QuestionAnswering;
using Shared;
using StatusGenerator;
using System.Globalization;
using System.Text.Json;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            LoadLocalSettings();
            var options = ParseCommandLine(args);

            PrintStartupConfigurationStatus();
            var appMode = options.Mode ?? ReadAppMode();
            var reportEndExclusive = GetReportEndExclusive(options, appMode);
            var reportStart = GetReportStart(options, reportEndExclusive);

            var org = GetRequiredEnvironmentVariable("AZDO_ORG");
            var project = GetRequiredEnvironmentVariable("AZDO_PROJECT");
            var repoId = GetRequiredEnvironmentVariable("AZDO_REPO");
            var pat = GetRequiredEnvironmentVariable("AZDO_PAT");

            var devops = new AzureDevOpsClient(pat);
            var builder = new WeeklyReportPromptBuilder();
            var completedDocumentBuilder = new WeeklyCompletedDocumentBuilder();

            var author = options.Author;
            if (string.IsNullOrWhiteSpace(author))
            {
                Console.Write("Enter author name (or alias): ");
                author = Console.ReadLine()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(author))
            {
                throw new InvalidOperationException("Author input is required.");
            }

            var azureOpenAiEndpoint = GetRequiredEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
            var azureOpenAiApiKey = GetRequiredEnvironmentVariable("AZURE_OPENAI_API_KEY");
            var azureOpenAiDeployment = GetRequiredEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
            var azureOpenAiEmbeddingDeployment = GetEnvironmentVariableOrDefault("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", "text-embedding-3-small");
            var azureOpenAiApiVersion = GetEnvironmentVariableOrDefault("AZURE_OPENAI_API_VERSION", "2024-10-21");
            var compareRag = bool.TryParse(Environment.GetEnvironmentVariable("COMPARE_RAG"), out var enabled) && enabled;

            var openAiClient = new AzureOpenAiClient(
                azureOpenAiEndpoint,
                azureOpenAiApiKey,
                azureOpenAiDeployment,
                azureOpenAiApiVersion);
            AzureAiSearchClient? searchClient = CreateSearchClientIfConfigured();

            if (appMode != AppMode.LastWeekSummary)
            {
                var questionAnsweringService = new QuestionAnsweringService(
                    devops,
                    openAiClient,
                    searchClient,
                    org,
                    project,
                    repoId,
                    azureOpenAiEmbeddingDeployment);
                var periodStart = reportEndExclusive.AddMonths(-3);
                QuestionAnsweringResult result;

                if (appMode == AppMode.ThreeMonthSummary)
                {
                    Console.WriteLine($"Generating a three-month summary from {periodStart:yyyy-MM-dd} to {reportEndExclusive.AddTicks(-1):yyyy-MM-dd} UTC...");
                    result = await questionAnsweringService.GeneratePeriodSummaryAsync(author, periodStart, reportEndExclusive);
                    Console.WriteLine("\n===== THREE-MONTH SUMMARY =====\n");
                }
                else
                {
                    Console.Write("Ask a question about your engineering work: ");
                    var question = Console.ReadLine()?.Trim();
                    if (string.IsNullOrWhiteSpace(question))
                    {
                        throw new InvalidOperationException("A question is required.");
                    }

                    Console.WriteLine($"Gathering evidence from {periodStart:yyyy-MM-dd} to {reportEndExclusive.AddTicks(-1):yyyy-MM-dd} UTC...");
                    result = await questionAnsweringService.AnswerAsync(author, question, periodStart, reportEndExclusive);
                    Console.WriteLine("\n===== ANSWER =====\n");
                }

                Console.WriteLine(result.Answer);
                Console.WriteLine($"\nEvidence used: {result.PullRequestCount} pull request(s), {result.SearchDocumentCount} Azure AI Search document(s).");
                return 0;
            }

            var reportEnd = reportEndExclusive.AddTicks(-1);

            Console.WriteLine($"Fetching PRs completed by '{author}' from {reportStart:yyyy-MM-dd} to {reportEnd:yyyy-MM-dd} UTC...");
            var completedPrs = await devops.GetCompletedPullRequestsByAuthorAsync(
                org,
                project,
                repoId,
                author,
                reportStart,
                reportEnd);

            Console.WriteLine($"Building weekly completed-work document from {completedPrs.Count} completed PR(s)...");
            var completedDocument = completedDocumentBuilder.Build(author, reportStart, reportEnd, completedPrs);
            var completedDocumentPath = await WriteWeeklyCompletedDocumentAsync(completedDocument);
            Console.WriteLine($"Weekly completed JSON written to {completedDocumentPath}");

            Console.WriteLine("Fetching active pull requests with unresolved comments...");
            var inProgressWithUnresolvedComments = await devops.GetActivePullRequestsByAuthorAsync(org, project, repoId, author);

            Console.WriteLine("Fetching pull requests created during the reporting period...");
            var newlyCreatedInPeriod = await devops.GetPullRequestsCreatedByAuthorAsync(
                org,
                project,
                repoId,
                author,
                reportStart,
                reportEnd);

            Console.WriteLine("Building weekly report prompt...");
            var prompt = builder.Build(
                author,
                reportStart,
                reportEnd,
                completedPrs,
                inProgressWithUnresolvedComments,
                newlyCreatedInPeriod);

            Console.WriteLine("\n===== GENERATED PROMPT =====\n");
            Console.WriteLine(prompt);
            Console.WriteLine($"\nWeekly completed JSON: {completedDocumentPath}");

            Console.WriteLine("Generating baseline weekly report without historical RAG context...");
            var baselineReport = await openAiClient.GenerateWeeklyReportAsync(prompt);

            completedDocument.GeneratedSummary = baselineReport;
            await WriteWeeklyCompletedDocumentAsync(completedDocumentPath, completedDocument);

            Console.WriteLine("\n===== WEEKLY REPORT (NO RAG) =====\n");
            Console.WriteLine(baselineReport);

            var reportToPublish = baselineReport;
            WeeklyCompletedDocument? weeklyDocument = null;

            if (compareRag)
            {
                searchClient ??= CreateRequiredSearchClient();

                Console.WriteLine($"Generating content vector with Azure OpenAI deployment '{azureOpenAiEmbeddingDeployment}'...");
                weeklyDocument = await EnrichWeeklyCompletedDocumentWithEmbeddingAsync(
                    completedDocumentPath,
                    openAiClient,
                    azureOpenAiEmbeddingDeployment);

                var retrievalEndExclusive = reportStart;
                var historicalLookbackStart = retrievalEndExclusive.AddDays(-21);
                Console.WriteLine(
                    $"Running hybrid search for historical weekly content from {historicalLookbackStart:yyyy-MM-dd} " +
                    $"through {retrievalEndExclusive.AddTicks(-1):yyyy-MM-dd} UTC...");
                var historicalSummaries = await searchClient.SearchHistoricalSummariesAsync(
                    author,
                    retrievalEndExclusive,
                    historicalLookbackStart,
                    weeklyDocument.Content,
                    weeklyDocument.ContentVector,
                    top: 5);

                Console.WriteLine($"Retrieved {historicalSummaries.Count} historical summary document(s) for augmentation.");
                foreach (var historicalSummary in historicalSummaries)
                {
                    Console.WriteLine(
                        $"- {historicalSummary.Id}: {historicalSummary.WeekStartUtc:yyyy-MM-dd} to {historicalSummary.WeekEndUtc:yyyy-MM-dd}");
                }

                if (historicalSummaries.Count > 0)
                {
                    var augmentedPrompt = BuildAugmentedPrompt(prompt, historicalSummaries);
                    Console.WriteLine("Generating comparison report with historical RAG context...");
                    reportToPublish = await openAiClient.GenerateWeeklyReportAsync(augmentedPrompt);

                    Console.WriteLine("\n===== WEEKLY REPORT (WITH HISTORICAL RAG) =====\n");
                    Console.WriteLine(reportToPublish);
                }
                else
                {
                    Console.WriteLine("No historical documents were found; the baseline report will be used.");
                }
            }

            if (options.Publish)
            {
                searchClient ??= CreateRequiredSearchClient();
                weeklyDocument ??= await EnrichWeeklyCompletedDocumentWithEmbeddingAsync(
                    completedDocumentPath,
                    openAiClient,
                    azureOpenAiEmbeddingDeployment);

                Console.WriteLine($"Generating summary vector with Azure OpenAI deployment '{azureOpenAiEmbeddingDeployment}'...");
                weeklyDocument.GeneratedSummary = reportToPublish;
                weeklyDocument.SummaryVectorModel = azureOpenAiEmbeddingDeployment;
                weeklyDocument.SummaryVector = await openAiClient.GenerateEmbeddingAsync(
                    reportToPublish,
                    azureOpenAiEmbeddingDeployment);

                await WriteWeeklyCompletedDocumentAsync(completedDocumentPath, weeklyDocument);

                Console.WriteLine("Upserting weekly summary document into Azure AI Search...");
                await searchClient.UpsertWeeklySummaryAsync(weeklyDocument);
                Console.WriteLine($"Published weekly report '{weeklyDocument.Id}'.");
            }
            else
            {
                Console.WriteLine("\nReport publication is disabled. Pass --publish to store it in Azure AI Search.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Engineering assistant failed.");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void PrintStartupConfigurationStatus()
    {
        Console.WriteLine("Configuration check:");
        PrintEnvironmentVariableStatus("AZDO_ORG");
        PrintEnvironmentVariableStatus("AZDO_PROJECT");
        PrintEnvironmentVariableStatus("AZDO_REPO");
        PrintEnvironmentVariableStatus("AZDO_PAT", isSecret: true);
        PrintEnvironmentVariableStatus("AZURE_OPENAI_ENDPOINT");
        PrintEnvironmentVariableStatus("AZURE_OPENAI_API_KEY", isSecret: true);
        PrintEnvironmentVariableStatus("AZURE_OPENAI_DEPLOYMENT", showValue: true);
        PrintEnvironmentVariableStatus("AZURE_OPENAI_EMBEDDING_DEPLOYMENT", showValue: true);
        PrintEnvironmentVariableStatus("COMPARE_RAG", showValue: true);
        PrintEnvironmentVariableStatus("AZURE_SEARCH_ENDPOINT");
        PrintEnvironmentVariableStatus("AZURE_SEARCH_API_KEY", isSecret: true);
        PrintEnvironmentVariableStatus("AZURE_SEARCH_INDEX_NAME");
        Console.WriteLine();
    }

    private static AppMode ReadAppMode()
    {
        Console.WriteLine("What would you like to do?");
        Console.WriteLine("1. Generate last-week summary");
        Console.WriteLine("2. Generate summary for the last three months");
        Console.WriteLine("3. Ask a question about the last three months");
        Console.Write("Select 1, 2, or 3: ");

        return Console.ReadLine()?.Trim() switch
        {
            "1" => AppMode.LastWeekSummary,
            "2" => AppMode.ThreeMonthSummary,
            "3" => AppMode.AskQuestion,
            _ => throw new InvalidOperationException("Select a valid option: 1, 2, or 3.")
        };
    }

    private static AzureAiSearchClient? CreateSearchClientIfConfigured()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_SEARCH_ENDPOINT");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_SEARCH_API_KEY");
        var indexName = Environment.GetEnvironmentVariable("AZURE_SEARCH_INDEX_NAME");

        return string.IsNullOrWhiteSpace(endpoint) ||
               string.IsNullOrWhiteSpace(apiKey) ||
               string.IsNullOrWhiteSpace(indexName)
            ? null
            : new AzureAiSearchClient(endpoint, apiKey, indexName);
    }

    private static AzureAiSearchClient CreateRequiredSearchClient()
    {
        return new AzureAiSearchClient(
            GetRequiredEnvironmentVariable("AZURE_SEARCH_ENDPOINT"),
            GetRequiredEnvironmentVariable("AZURE_SEARCH_API_KEY"),
            GetRequiredEnvironmentVariable("AZURE_SEARCH_INDEX_NAME"));
    }

    private static void PrintEnvironmentVariableStatus(string name, bool isSecret = false, bool showValue = false)
    {
        var value = Environment.GetEnvironmentVariable(name);
        var isConfigured = !string.IsNullOrWhiteSpace(value);
        var status = isConfigured ? "set" : "missing";
        var suffix = isSecret && isConfigured
            ? " (value hidden)"
            : showValue && isConfigured
                ? $" ({value})"
                : string.Empty;
        Console.WriteLine($"- {name}: {status}{suffix}");
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Configuration value '{name}' is required. Set it in appsettings.local.json or as an environment variable.");
        }

        return value;
    }

    private static string GetEnvironmentVariableOrDefault(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static void LoadLocalSettings()
    {
        const string fileName = "appsettings.local.json";
        var settingsPath = Path.Combine(AppContext.BaseDirectory, fileName);

        if (!File.Exists(settingsPath))
        {
            return;
        }

        var json = File.ReadAllText(settingsPath);
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"{fileName} does not contain a valid settings object.");

        foreach (var setting in settings)
        {
            if (!string.IsNullOrWhiteSpace(setting.Value) &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(setting.Key)))
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
        }
    }

    private static CommandLineOptions ParseCommandLine(string[] args)
    {
        AppMode? mode = null;
        string? author = null;
        string? startDate = null;
        string? endDate = null;
        var publish = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index].ToLowerInvariant())
            {
                case "weekly":
                    mode = AppMode.LastWeekSummary;
                    break;
                case "--author" when index + 1 < args.Length:
                    author = args[++index];
                    break;
                case "--start-date" when index + 1 < args.Length:
                    startDate = args[++index];
                    break;
                case "--end-date" when index + 1 < args.Length:
                    endDate = args[++index];
                    break;
                case "--publish":
                    publish = true;
                    break;
                default:
                    throw new ArgumentException(
                        "Usage: dotnet run --project src/ConsoleApp -- [weekly] [--author alias] [--start-date yyyy-MM-dd] [--end-date yyyy-MM-dd] [--publish]");
            }
        }

        return new CommandLineOptions(mode, author, startDate, endDate, publish);
    }

    private static DateTime GetReportStart(CommandLineOptions options, DateTime reportEndExclusive)
    {
        var reportStart = string.IsNullOrWhiteSpace(options.StartDate)
            ? reportEndExclusive.AddDays(-7)
            : ParseDate(options.StartDate, "--start-date");

        if (reportStart >= reportEndExclusive)
        {
            throw new ArgumentException("--start-date must be on or before --end-date.");
        }

        return reportStart;
    }

    private static DateTime GetReportEndExclusive(CommandLineOptions options, AppMode appMode)
    {
        if (!string.IsNullOrWhiteSpace(options.EndDate))
        {
            return ParseInclusiveEndDate(options.EndDate, "--end-date");
        }

        if (appMode == AppMode.LastWeekSummary && options.Mode is null)
        {
            Console.Write("Enter weekly summary end date (yyyy-MM-dd, blank for today): ");
            var input = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(input))
            {
                return ParseInclusiveEndDate(input, "End date");
            }
        }

        return DateTime.UtcNow;
    }

    private static DateTime ParseInclusiveEndDate(string value, string inputName)
    {
        return ParseDate(value, inputName).AddDays(1);
    }

    private static DateTime ParseDate(string value, string inputName)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var reportEnd))
        {
            throw new ArgumentException($"{inputName} must use the yyyy-MM-dd format, for example 2026-08-15.");
        }

        return reportEnd;
    }

    private static async Task<string> WriteWeeklyCompletedDocumentAsync(Shared.WeeklyCompletedDocument document)
    {
        var outputDirectory = Path.Combine(AppContext.BaseDirectory, "artifacts", "weekly-completed");
        Directory.CreateDirectory(outputDirectory);

        var fileName = $"{document.Id}.json";
        var filePath = Path.Combine(outputDirectory, fileName);

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);
        return filePath;
    }

    private static async Task<WeeklyCompletedDocument> EnrichWeeklyCompletedDocumentWithEmbeddingAsync(
        string filePath,
        AzureOpenAiClient openAiClient,
        string embeddingDeployment)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var document = JsonSerializer.Deserialize<WeeklyCompletedDocument>(json)
            ?? throw new InvalidOperationException("Failed to deserialize weekly completed JSON document.");

        if (string.IsNullOrWhiteSpace(document.Content))
        {
            throw new InvalidOperationException("Weekly completed JSON document does not contain any content to embed.");
        }

        var vector = await openAiClient.GenerateEmbeddingAsync(document.Content, embeddingDeployment);
        document.ContentVectorModel = embeddingDeployment;
        document.ContentVector = vector;

        await WriteWeeklyCompletedDocumentAsync(filePath, document);
        Console.WriteLine($"Content vector written to {filePath} ({vector.Count} dimensions).");
        return document;
    }

    private static async Task WriteWeeklyCompletedDocumentAsync(string filePath, WeeklyCompletedDocument document)
    {
        var updatedJson = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, updatedJson);
    }

    private static string BuildAugmentedPrompt(string currentPrompt, List<HistoricalSummary> historicalSummaries)
    {
        if (historicalSummaries.Count == 0)
        {
            return currentPrompt;
        }

        var summaryLines = historicalSummaries.Select((item, index) =>
        {
            var summaryText = !string.IsNullOrWhiteSpace(item.GeneratedSummary)
                ? item.GeneratedSummary
                : item.Content;

            return
                $"{index + 1}. Week {item.WeekStartUtc:yyyy-MM-dd} to {item.WeekEndUtc:yyyy-MM-dd} (Author: {item.Author})\n" +
                summaryText;
        });

        return
            currentPrompt +
            "\n\nHistorical context from the last 3 weeks (use for continuity and style, but prioritize current-week facts):\n" +
            string.Join("\n\n", summaryLines);
    }

    private enum AppMode
    {
        LastWeekSummary,
        ThreeMonthSummary,
        AskQuestion
    }

    private sealed record CommandLineOptions(
        AppMode? Mode,
        string? Author,
        string? StartDate,
        string? EndDate,
        bool Publish);
}