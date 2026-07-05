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
            sb.AppendLine($"  Unresolved comments: {unresolvedTotal}");
        }
    }
}
