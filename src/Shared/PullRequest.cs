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
    public DateTime LastUpdatedAt { get; set; }
    public List<Commit> Commits { get; set; } = new List<Commit>();
    public Dictionary<string, int> UnresolvedCommentsByPerson { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

}