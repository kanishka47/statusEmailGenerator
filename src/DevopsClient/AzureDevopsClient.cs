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

    public async Task<List<PullRequest>> GetCompletedPullRequestsByAuthorAsync(
        string org,
        string project,
        string repoId,
        string author,
        DateTime from,
        DateTime to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        return await GetPullRequestsCoreAsync(
            org,
            project,
            repoId,
            "completed",
            includeDetails: true,
            author: author,
            timeFrom: from,
            timeTo: to,
            timeRangeType: "closed");
    }

    public async Task<List<PullRequest>> GetActivePullRequestsByAuthorAsync(
        string org,
        string project,
        string repoId,
        string author)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var activePrs = await GetPullRequestsCoreAsync(
            org,
            project,
            repoId,
            "active",
            includeDetails: true,
            author: author);

        return activePrs
            .Where(pr => pr.UnresolvedComments.Count > 0)
            .ToList();
    }

    public async Task<List<PullRequest>> GetPullRequestsCreatedByAuthorAsync(
        string org,
        string project,
        string repoId,
        string author,
        DateTime from,
        DateTime to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        return await GetPullRequestsCoreAsync(
            org,
            project,
            repoId,
            "all",
            includeDetails: true,
            author: author,
            timeFrom: from,
            timeTo: to,
            timeRangeType: "created");
    }

    private async Task<List<PullRequest>> GetPullRequestsCoreAsync(
        string org,
        string project,
        string repoId,
        string status,
        bool includeDetails = true,
        string? author = null,
        DateTime? timeFrom = null,
        DateTime? timeTo = null,
        string? timeRangeType = null)
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

                if (timeFrom.HasValue)
            {
                pullRequestsUrl +=
                    $"&searchCriteria.minTime={Uri.EscapeDataString(timeFrom.Value.ToUniversalTime().ToString("O"))}";
            }

                if (timeTo.HasValue)
            {
                pullRequestsUrl +=
                    $"&searchCriteria.maxTime={Uri.EscapeDataString(timeTo.Value.ToUniversalTime().ToString("O"))}";
                }

                if (!string.IsNullOrWhiteSpace(timeRangeType))
                {
                pullRequestsUrl +=
                    $"&searchCriteria.queryTimeRangeType={Uri.EscapeDataString(timeRangeType)}";
            }

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

        if (!string.IsNullOrWhiteSpace(author))
        {
            prs = prs
                .Where(pr => IsMatchForAuthor(pr, author))
                .ToList();
        }

        if (timeRangeType?.Equals("created", StringComparison.OrdinalIgnoreCase) == true)
        {
            prs = prs
                .Where(pr => !timeFrom.HasValue || pr.CreatedDate >= timeFrom.Value)
                .Where(pr => !timeTo.HasValue || pr.CreatedDate <= timeTo.Value)
                .ToList();
        }

        if (timeRangeType?.Equals("closed", StringComparison.OrdinalIgnoreCase) == true)
        {
            prs = prs
                .Where(pr => !timeFrom.HasValue || pr.LastUpdatedAt >= timeFrom.Value)
                .Where(pr => !timeTo.HasValue || pr.LastUpdatedAt <= timeTo.Value)
                .ToList();
        }

        if (includeDetails)
        {
            await Task.WhenAll(prs.Select(async pr =>
            {
                await Task.WhenAll(
                    PopulateWorkItemsAsync(pr, encodedOrg, encodedProject, encodedRepoId),
                    PopulateCommentDetailsAsync(pr, encodedOrg, encodedProject, encodedRepoId));
            }));
        }

        return prs;
    }

    private async Task PopulateWorkItemsAsync(
        PullRequest pr,
        string encodedOrg,
        string encodedProject,
        string encodedRepoId)
    {
        var pullRequestUrl =
            $"https://dev.azure.com/{encodedOrg}/{encodedProject}/_apis/git/repositories/{encodedRepoId}/pullrequests/{pr.Id}?includeWorkItemRefs=true&api-version=7.1";

        var pullRequestJson = await GetJsonAsync(pullRequestUrl);
        using var doc = JsonDocument.Parse(pullRequestJson);

        if (!doc.RootElement.TryGetProperty("workItemRefs", out var workItemRefs) || workItemRefs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var items = new List<WorkItemReference>();

        foreach (var workItemRef in workItemRefs.EnumerateArray())
        {
            var id = GetStringOrEmpty(workItemRef, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            items.Add(new WorkItemReference
            {
                Id = id,
                Url = GetStringOrEmpty(workItemRef, "url")
            });
        }

        await Task.WhenAll(items.Select(PopulateWorkItemTitleAsync));

        pr.WorkItems = items;
    }

    private async Task PopulateWorkItemTitleAsync(WorkItemReference workItem)
    {
        if (string.IsNullOrWhiteSpace(workItem.Url))
        {
            return;
        }

        var separator = workItem.Url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var workItemUrl = $"{workItem.Url}{separator}fields=System.Title&api-version=7.1";
        var workItemJson = await GetJsonAsync(workItemUrl);
        using var doc = JsonDocument.Parse(workItemJson);

        if (doc.RootElement.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Object)
        {
            workItem.Title = GetStringOrEmpty(fields, "System.Title");
        }
    }

    private async Task PopulateCommentDetailsAsync(
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
        var unresolvedComments = new List<UnresolvedComment>();
        var resolvedComments = new List<ResolvedComment>();

        foreach (var thread in threads.EnumerateArray())
        {
            var threadStatus = GetStringOrEmpty(thread, "status");
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

                if (threadStatus.Equals("fixed", StringComparison.OrdinalIgnoreCase))
                {
                    resolvedComments.Add(new ResolvedComment
                    {
                        ThreadId = GetIntOrDefault(thread, "id"),
                        Author = GetNestedStringOrEmpty(comment, "author", "displayName"),
                        Content = GetStringOrEmpty(comment, "content"),
                        PublishedDate = GetDateOrDefault(comment, "publishedDate")
                    });

                    continue;
                }

                if (!threadStatus.Equals("active", StringComparison.OrdinalIgnoreCase))
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

                var content = GetStringOrEmpty(comment, "content").Trim();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    unresolvedComments.Add(new UnresolvedComment
                    {
                        ThreadId = GetIntOrDefault(thread, "id"),
                        Author = person,
                        Content = content,
                        PublishedDate = GetDateOrDefault(comment, "publishedDate")
                    });
                }
            }
        }

        pr.UnresolvedCommentsByPerson = unresolvedCounts;
        pr.UnresolvedComments = unresolvedComments;
        pr.ResolvedComments = resolvedComments;
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
                AuthorUniqueName = GetNestedStringOrEmpty(item, "createdBy", "uniqueName"),
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
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new HttpRequestException(
                    "Azure DevOps authentication failed (401 Unauthorized). " +
                    "Check that AZDO_PAT is set in this terminal session, not expired, and has permission to read the target repository.");
            }

            throw new HttpRequestException(
                $"Azure DevOps request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"URL: {url}. Response: {BuildCompactErrorResponse(responseBody)}");
        }

        return responseBody;
    }

    private static string BuildCompactErrorResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "<empty response>";
        }

        var compact = responseBody.ReplaceLineEndings(" ").Trim();
        return compact.Length <= 400
            ? compact
            : compact[..400] + "...";
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

    private static int GetIntOrDefault(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return default;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var number)
            ? number
            : default;
    }

    private static bool IsMatchForAuthor(PullRequest pr, string requestedAuthor)
    {
        return pr.Author.Contains(requestedAuthor, StringComparison.OrdinalIgnoreCase)
            || pr.AuthorUniqueName.Contains(requestedAuthor, StringComparison.OrdinalIgnoreCase);
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