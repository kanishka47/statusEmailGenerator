using DevOpsClient;

class Program
{
    static async Task Main()
    {
        var org = GetRequiredEnvironmentVariable("AZDO_ORG");
        var project = GetRequiredEnvironmentVariable("AZDO_PROJECT");
        var repoId = GetRequiredEnvironmentVariable("AZDO_REPO");
        var pat = GetRequiredEnvironmentVariable("AZDO_PAT");

        var devops = new AzureDevOpsClient(pat);
        var builder = new WeeklyReportPromptBuilder();

        Console.Write("Enter author name (or alias) for weekly report: ");
        var author = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(author))
        {
            throw new InvalidOperationException("Author input is required.");
        }

        var now = DateTime.UtcNow;
        var weekStart = now.AddDays(-7);

        var allPrs = await devops.GetPullRequestsAsync(org, project, repoId, weekStart, now);
        var authorPrs = allPrs
            .Where(pr => pr.Author.Contains(author, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var completedPrs = authorPrs
            .Where(pr => pr.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var inProgressWithUnresolvedComments = await devops.GetActivePullRequestsByAuthorAsync(org, project, repoId, author);

        var newlyCreatedLastWeek = authorPrs
            .Where(pr => pr.CreatedDate >= weekStart && pr.CreatedDate <= now)
            .ToList();

        var prompt = builder.Build(
            author,
            weekStart,
            now,
            completedPrs,
            inProgressWithUnresolvedComments,
            newlyCreatedLastWeek);

        Console.WriteLine("\n===== GENERATED PROMPT =====\n");
        Console.WriteLine(prompt);
    }

    private static string GetRequiredEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Environment variable '{name}' is required. Set it before running the app.");
        }

        return value;
    }
}