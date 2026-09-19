namespace Shared;

public class WeeklyCompletedPullRequest
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public DateTime CreatedDateUtc { get; set; }
    public DateTime LastUpdatedAtUtc { get; set; }
    public List<string> WorkItemIds { get; set; } = new List<string>();
    public List<string> ResolvedComments { get; set; } = new List<string>();
}