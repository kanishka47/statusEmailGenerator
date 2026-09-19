namespace Shared;

public class ResolvedComment
{
    public int ThreadId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime PublishedDate { get; set; }
}