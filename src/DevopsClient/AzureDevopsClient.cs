using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Shared;

namespace DevOpsClient;

public class AzureDevOpsClient
{
    private readonly HttpClient _http;

    public AzureDevOpsClient(string pat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pat);

        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(60);

        var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", auth);
    }

    public async Task<List<PullRequest>> GetPullRequestsAsync(
        string org,
        string project,
        string repoId,
        DateTime from,
        DateTime to)
    {
        var allPrs = await GetPullRequestsCoreAsync(org, project, repoId, "all", includeCommentDetails: false);
        return allPrs
            .Where(pr => pr.CreatedDate >= from && pr.CreatedDate <= to)
            .ToList();
    }

    public async Task<List<PullRequest>> GetActivePullRequestsByAuthorAsync(
        string org,
        string project,
        string repoId,
        string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var activePrs = await GetPullRequestsCoreAsync(org, project, repoId, "active", includeCommentDetails: false);
        var filtered = activePrs
            .Where(pr => IsMatchForAuthor(pr.Author, author))
            .ToList();

        foreach (var pr in filtered)
        {
            await PopulateUnresolvedCommentsByPersonAsync(pr, Uri.EscapeDataString(org.Trim()), Uri.EscapeDataString(project.Trim()), Uri.EscapeDataString(repoId.Trim()));
        }

        return filtered;
    }

    private async Task<List<PullRequest>> GetPullRequestsCoreAsync(
        string org,
        string project,
        string repoId,
        string status,
        bool includeCommentDetails = true)
    {
        ValidatePathInput("org", org);
        ValidatePathInput("project", project);
        ValidatePathInput("repoId", repoId);
        ValidatePathInput("status", status);

        var encodedOrg = Uri.EscapeDataString(org.Trim());
        var encodedProject = Uri.EscapeDataString(project.Trim());
        var encodedRepoId = Uri.EscapeDataString(repoId.Trim());
        var encodedStatus = Uri.EscapeDataString(status.Trim());

        const int pageSize = 100;
        const int maxPages = 50;
        var skip = 0;
        var prs = new List<PullRequest>();
        var seenIds = new HashSet<int>();

        for (var pageNumber = 0; pageNumber < maxPages; pageNumber++)
        {
            var pullRequestsUrl =
                $"https://dev.azure.com/{encodedOrg}/{encodedProject}/_apis/git/repositories/{encodedRepoId}/pullrequests" +
                $"?searchCriteria.status={encodedStatus}&$top={pageSize}&$skip={skip}&api-version=7.1";

            var json = await GetJsonAsync(pullRequestsUrl);
            var page = ParsePullRequests(json, repoId);

            if (page.Count == 0)
            {
                break;
            }

            var newItems = page.Where(pr => seenIds.Add(pr.Id)).ToList();
            if (newItems.Count == 0)
            {
                break;
            }

            prs.AddRange(newItems);

            if (page.Count < pageSize)
            {
                break;
            }

            skip += pageSize;
        }

        if (includeCommentDetails)
        {
            foreach (var pr in prs)
            {
                await PopulateUnresolvedCommentsByPersonAsync(pr, encodedOrg, encodedProject, encodedRepoId);
            }
        }

        return prs;
    }

    private async Task PopulateUnresolvedCommentsByPersonAsync(
        PullRequest pr,
        string encodedOrg,
        string encodedProject,
        string encodedRepoId)
    {
        var threadsUrl =
            $"https://dev.azure.com/{encodedOrg}/{encodedProject}/_apis/git/repositories/{encodedRepoId}/pullRequests/{pr.Id}/threads?api-version=7.1";

        var threadsJson = await GetJsonAsync(threadsUrl);
        using var doc = JsonDocument.Parse(threadsJson);

        if (!doc.RootElement.TryGetProperty("value", out var threads) || threads.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var unresolvedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var thread in threads.EnumerateArray())
        {
            var threadStatus = GetStringOrEmpty(thread, "status");
            if (!threadStatus.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!thread.TryGetProperty("comments", out var comments) || comments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var comment in comments.EnumerateArray())
            {
                var isDeleted = comment.TryGetProperty("isDeleted", out var deletedProp) && deletedProp.ValueKind == JsonValueKind.True;
                if (isDeleted)
                {
                    continue;
                }

                var person = GetNestedStringOrEmpty(comment, "author", "displayName");
                if (string.IsNullOrWhiteSpace(person))
                {
                    person = "Unknown";
                }

                unresolvedCounts[person] = unresolvedCounts.TryGetValue(person, out var current)
                    ? current + 1
                    : 1;
            }
        }

        pr.UnresolvedCommentsByPerson = unresolvedCounts;
    }

    private static List<PullRequest> ParsePullRequests(string json, string repoId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return new List<PullRequest>();
        }

        var prs = new List<PullRequest>();

        foreach (var item in values.EnumerateArray())
        {
            var pr = new PullRequest
            {
                Id = item.TryGetProperty("pullRequestId", out var idProp) && idProp.TryGetInt32(out var id) ? id : 0,
                Title = GetStringOrEmpty(item, "title"),
                Description = GetStringOrEmpty(item, "description"),
                Status = GetStringOrEmpty(item, "status"),
                Repository = repoId,
                Branch = GetStringOrEmpty(item, "sourceRefName"),
                Author = GetAuthor(item),
                CreatedDate = GetDateOrDefault(item, "creationDate"),
                LastUpdatedAt = GetDateOrDefault(item, "closedDate")
            };

            if (pr.LastUpdatedAt == default)
            {
                pr.LastUpdatedAt = GetDateOrDefault(item, "creationDate");
            }

            prs.Add(pr);
        }

        return prs;
    }

    private static string GetAuthor(JsonElement pullRequest)
    {
        var displayName = GetNestedStringOrEmpty(pullRequest, "createdBy", "displayName");
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        var uniqueName = GetNestedStringOrEmpty(pullRequest, "createdBy", "uniqueName");
        if (!string.IsNullOrWhiteSpace(uniqueName))
        {
            return uniqueName;
        }

        return GetNestedStringOrEmpty(pullRequest, "createdBy", "id");
    }

    private async Task<string> GetJsonAsync(string url)
    {
        using var response = await _http.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Azure DevOps request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"URL: {url}. Response: {responseBody}");
        }

        return await response.Content.ReadAsStringAsync();
    }

    private static string GetStringOrEmpty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return prop.GetString() ?? string.Empty;
    }

    private static string GetNestedStringOrEmpty(JsonElement element, string objectProperty, string valueProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return GetStringOrEmpty(obj, valueProperty);
    }

    private static DateTime GetDateOrDefault(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            return default;
        }

        return prop.TryGetDateTime(out var date) ? date : default;
    }

    private static bool IsMatchForAuthor(string prAuthor, string requestedAuthor)
    {
        if (string.IsNullOrWhiteSpace(prAuthor))
        {
            return false;
        }

        return prAuthor.Contains(requestedAuthor, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidatePathInput(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} cannot be empty.", name);
        }

        if (value.Contains("://", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"{name} should be a plain name, not a full URL. Example: org='msdata', project='Database Systems', repoId='DsMainDev'.",
                name);
        }
    }
}