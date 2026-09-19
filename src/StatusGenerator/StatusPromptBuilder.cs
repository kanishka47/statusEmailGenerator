using Shared;
using System.Text;

namespace StatusGenerator;

public class StatusPromptBuilder
{
    public string BuildWeeklyPrompt(
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

        return sb.ToString();
    }

    public string Build(List<PullRequest> prs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are an engineering assistant.");
        sb.AppendLine();
        sb.AppendLine("Here is today's work:");
        sb.AppendLine();

        foreach (var pr in prs)
        {
            sb.AppendLine("Pull Request");
            sb.AppendLine("-------------");
            sb.AppendLine($"Title: {pr.Title}");
            sb.AppendLine($"Description: {pr.Description}");
            sb.AppendLine($"Author: {pr.Author}");
            sb.AppendLine($"Branch: {pr.Branch}");
            sb.AppendLine($"Repository: {pr.Repository}");
            sb.AppendLine($"Status: {pr.Status}");
            sb.AppendLine($"Created Date: {pr.CreatedDate}");
            sb.AppendLine($"Last Updated At: {pr.LastUpdatedAt}");
            sb.AppendLine("Unresolved Comments By Person:");
            if (pr.UnresolvedCommentsByPerson.Count == 0)
            {
                sb.AppendLine("  - None");
            }
            else
            {
                foreach (var unresolved in pr.UnresolvedCommentsByPerson)
                {
                    sb.AppendLine($"  - {unresolved.Key}: {unresolved.Value}");
                }
            }
            sb.AppendLine("Commits:");
            foreach (var commit in pr.Commits)
            {
                sb.AppendLine($"  - {commit.Message} by {commit.Author} on {commit.Date}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Generate a professional status email:");
        sb.AppendLine("Completed / In Progress / Blockers / Tomorrow");

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
        }
    }

    private static void AppendWorkItems(StringBuilder sb, List<WorkItemReference> workItems)
    {
        if (workItems.Count == 0)
        {
            sb.AppendLine("  Work items: None");
            return;
        }

        sb.AppendLine($"  Work items: {string.Join(", ", workItems.Select(workItem => $"#{workItem.Id}"))}");
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
}