using System.Text;
using Shared;

namespace StatusGenerator;

public class WeeklyCompletedDocumentBuilder
{
    public WeeklyCompletedDocument Build(
        string author,
        DateTime from,
        DateTime to,
        List<PullRequest> completedPrs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentNullException.ThrowIfNull(completedPrs);

        var orderedPrs = completedPrs
            .OrderByDescending(pr => pr.LastUpdatedAt)
            .ThenByDescending(pr => pr.CreatedDate)
            .ToList();

        var document = new WeeklyCompletedDocument
        {
            Id = BuildDocumentId(author, from, to),
            Author = author.Trim(),
            WeekStartUtc = from,
            WeekEndUtc = to,
            CompletedPullRequestCount = orderedPrs.Count,
            PullRequestIds = orderedPrs.Select(pr => pr.Id).ToList(),
            WorkItemIds = orderedPrs
                .SelectMany(pr => pr.WorkItems)
                .Select(workItem => workItem.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            CompletedPullRequests = orderedPrs.Select(MapPullRequest).ToList()
        };

        document.Content = BuildContent(document);
        return document;
    }

    private static WeeklyCompletedPullRequest MapPullRequest(PullRequest pr)
    {
        return new WeeklyCompletedPullRequest
        {
            PullRequestId = pr.Id,
            Title = pr.Title,
            Description = pr.Description,
            Repository = pr.Repository,
            Branch = pr.Branch,
            CreatedDateUtc = pr.CreatedDate,
            LastUpdatedAtUtc = pr.LastUpdatedAt,
            WorkItemIds = pr.WorkItems
                .Select(workItem => workItem.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ResolvedComments = pr.ResolvedComments
                .Select(comment => comment.Content.ReplaceLineEndings(" ").Trim())
                .Where(content => !string.IsNullOrWhiteSpace(content))
                .ToList()
        };
    }

    private static string BuildContent(WeeklyCompletedDocument document)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Weekly completed work summary for {document.Author}");
        sb.AppendLine($"Week: {document.WeekStartUtc:yyyy-MM-dd} to {document.WeekEndUtc:yyyy-MM-dd} UTC");
        sb.AppendLine($"Completed pull requests: {document.CompletedPullRequestCount}");

        if (document.WorkItemIds.Count > 0)
        {
            sb.AppendLine($"Work items: {string.Join(", ", document.WorkItemIds.Select(id => $"#{id}"))}");
        }

        sb.AppendLine();

        foreach (var pr in document.CompletedPullRequests)
        {
            sb.AppendLine($"PR #{pr.PullRequestId}: {pr.Title}");

            if (!string.IsNullOrWhiteSpace(pr.Description))
            {
                sb.AppendLine($"Description: {pr.Description}");
            }

            sb.AppendLine($"Repository: {pr.Repository}");
            sb.AppendLine($"Branch: {pr.Branch}");
            sb.AppendLine($"Completed at: {pr.LastUpdatedAtUtc:yyyy-MM-dd}");

            if (pr.WorkItemIds.Count > 0)
            {
                sb.AppendLine($"Work items: {string.Join(", ", pr.WorkItemIds.Select(id => $"#{id}"))}");
            }

            if (pr.ResolvedComments.Count > 0)
            {
                sb.AppendLine("Resolved comments:");

                foreach (var resolvedComment in pr.ResolvedComments)
                {
                    sb.AppendLine($"- {resolvedComment}");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    private static string BuildDocumentId(string author, DateTime from, DateTime to)
    {
        var normalizedAuthor = new string(author
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray())
            .Trim('-');

        if (string.IsNullOrWhiteSpace(normalizedAuthor))
        {
            normalizedAuthor = "unknown-author";
        }

        return $"weekly-{normalizedAuthor}-{from:yyyyMMdd}-{to:yyyyMMdd}";
    }
}