namespace Shared;

public class WeeklyCompletedDocument
{
    public string Id { get; set; } = string.Empty;
    public string DocumentType { get; set; } = "weeklyCompletedSummary";
    public string Author { get; set; } = string.Empty;
    public DateTime WeekStartUtc { get; set; }
    public DateTime WeekEndUtc { get; set; }
    public int CompletedPullRequestCount { get; set; }
    public List<int> PullRequestIds { get; set; } = new List<int>();
    public List<string> WorkItemIds { get; set; } = new List<string>();
    public List<WeeklyCompletedPullRequest> CompletedPullRequests { get; set; } = new List<WeeklyCompletedPullRequest>();
    public string Content { get; set; } = string.Empty;
    public string ContentVectorModel { get; set; } = string.Empty;
    public List<float> ContentVector { get; set; } = new List<float>();
    public string GeneratedSummary { get; set; } = string.Empty;
    public string SummaryVectorModel { get; set; } = string.Empty;
    public List<float> SummaryVector { get; set; } = new List<float>();
}