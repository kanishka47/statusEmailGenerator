namespace Shared;

public class PullRequest
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string AuthorUniqueName { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; }
    public List<Commit> Commits { get; set; } = new List<Commit>();
    public List<WorkItemReference> WorkItems { get; set; } = new List<WorkItemReference>();
    public List<ResolvedComment> ResolvedComments { get; set; } = new List<ResolvedComment>();
    public List<UnresolvedComment> UnresolvedComments { get; set; } = new List<UnresolvedComment>();
    public Dictionary<string, int> UnresolvedCommentsByPerson { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

}