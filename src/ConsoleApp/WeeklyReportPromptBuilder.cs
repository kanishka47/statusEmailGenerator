using Shared;
using System.Text;

public class WeeklyReportPromptBuilder
{
    public string Build(
        string author,
        DateTime from,
        DateTime to,
        List<PullRequest> completedPrs,
        List<PullRequest> inProgressWithUnresolvedComments,
        List<PullRequest> newlyCreatedLastWeek)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are an engineering assistant.");
        sb.AppendLine($"Generate a weekly status update for: {author}");
        sb.AppendLine($"Time range (UTC): {from:yyyy-MM-dd} to {to:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine("Completed work items (PRs):");
        AppendPrList(sb, completedPrs);
        sb.AppendLine();

        sb.AppendLine("In progress work items with unresolved comments:");
        AppendPrList(sb, inProgressWithUnresolvedComments);
        sb.AppendLine();

        sb.AppendLine("Newly created PRs in last week:");
        AppendPrList(sb, newlyCreatedLastWeek);
        sb.AppendLine();

        sb.AppendLine("Write a concise weekly report with sections:");
        sb.AppendLine("Completed / In Progress / Risks-Blockers / Next Week Plan");
        sb.AppendLine("For every PR mentioned, include each linked task name and work-item number alongside the PR number.");
        sb.AppendLine("For Next Week Plan, summarize concrete actions needed to address the unresolved review comments below.");
        sb.AppendLine("Group related comments by PR, prioritize blockers and requested changes, and do not invent work not supported by the comments.");

        return sb.ToString();
    }

    private static void AppendPrList(StringBuilder sb, List<PullRequest> prs)
    {
        if (prs.Count == 0)
        {
            sb.AppendLine("- None");
            return;
        }

        foreach (var pr in prs)
        {
            var unresolvedTotal = pr.UnresolvedCommentsByPerson.Values.Sum();
            sb.AppendLine($"- PR #{pr.Id}: {pr.Title}");
            sb.AppendLine($"  Status: {pr.Status}");
            sb.AppendLine($"  Created: {pr.CreatedDate:yyyy-MM-dd}");
            sb.AppendLine($"  Updated: {pr.LastUpdatedAt:yyyy-MM-dd}");
            sb.AppendLine($"  Branch: {pr.Branch}");
            AppendWorkItems(sb, pr.WorkItems);
            AppendResolvedComments(sb, pr.ResolvedComments);
            sb.AppendLine($"  Unresolved comments: {unresolvedTotal}");
            AppendUnresolvedComments(sb, pr.UnresolvedComments);
        }
    }

    private static void AppendWorkItems(StringBuilder sb, List<WorkItemReference> workItems)
    {
        if (workItems.Count == 0)
        {
            sb.AppendLine("  Work items: None");
            return;
        }

        var formattedWorkItems = workItems.Select(workItem => string.IsNullOrWhiteSpace(workItem.Title)
            ? $"#{workItem.Id}"
            : $"#{workItem.Id}: {workItem.Title}");
        sb.AppendLine($"  Linked tasks: {string.Join(", ", formattedWorkItems)}");
    }

    private static void AppendResolvedComments(StringBuilder sb, List<ResolvedComment> resolvedComments)
    {
        if (resolvedComments.Count == 0)
        {
            sb.AppendLine("  Resolved comments: None");
            return;
        }

        sb.AppendLine("  Resolved comments:");

        foreach (var comment in resolvedComments)
        {
            var content = comment.Content.ReplaceLineEndings(" ").Trim();
            if (content.Length > 180)
            {
                content = content[..177] + "...";
            }

            sb.AppendLine($"    - {comment.Author} ({comment.PublishedDate:yyyy-MM-dd}): {content}");
        }
    }

    private static void AppendUnresolvedComments(StringBuilder sb, List<UnresolvedComment> unresolvedComments)
    {
        if (unresolvedComments.Count == 0)
        {
            return;
        }

        sb.AppendLine("  Unresolved review details:");

        foreach (var comment in unresolvedComments)
        {
            var content = comment.Content.ReplaceLineEndings(" ").Trim();
            if (content.Length > 500)
            {
                content = content[..497] + "...";
            }

            sb.AppendLine($"    - {comment.Author} ({comment.PublishedDate:yyyy-MM-dd}): {content}");
        }
    }
}
