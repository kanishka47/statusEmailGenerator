using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Shared;

public sealed class AzureAiSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _indexName;
    private readonly string _apiVersion;

    public AzureAiSearchClient(string endpoint, string apiKey, string indexName, string apiVersion = "2024-07-01")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        _endpoint = endpoint.TrimEnd('/');
        _indexName = indexName.Trim();
        _apiVersion = apiVersion.Trim();

        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<List<HistoricalSummary>> SearchHistoricalSummariesAsync(
        string author,
        DateTime weekEndExclusive,
        DateTime lookbackStartInclusive,
        string queryText,
        List<float> queryVector,
        int top = 5)
    {
        return await SearchRelevantSummariesAsync(
            author,
            lookbackStartInclusive,
            weekEndExclusive,
            queryText,
            queryVector,
            top);
    }

    public async Task<List<HistoricalSummary>> SearchRelevantSummariesAsync(
        string author,
        DateTime fromInclusive,
        DateTime toExclusive,
        string queryText,
        List<float> queryVector,
        int top = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText);
        ArgumentNullException.ThrowIfNull(queryVector);

        var requestUri =
            $"{_endpoint}/indexes/{Uri.EscapeDataString(_indexName)}/docs/search?api-version={Uri.EscapeDataString(_apiVersion)}";

        var escapedAuthor = author.Replace("'", "''", StringComparison.Ordinal);
        var filter =
            $"author eq '{escapedAuthor}' and weekStartUtc lt {toExclusive:O} and weekEndUtc ge {fromInclusive:O}";

        var payload = new
        {
            search = queryText,
            top,
            filter,
            select = "id,author,weekStartUtc,weekEndUtc,generatedSummary,content",
            vectorQueries = new[]
            {
                new
                {
                    kind = "vector",
                    vector = queryVector,
                    fields = "summaryVector",
                    k = top
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(requestUri, payload);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure AI Search query failed with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"URL: {requestUri}. Response: {responseBody}");
        }

        var results = ParseHistoricalSummaries(responseBody);
        if (results.Count > 0)
        {
            return results;
        }

        var keywordPayload = new
        {
            search = queryText,
            top,
            filter,
            select = "id,author,weekStartUtc,weekEndUtc,generatedSummary,content"
        };

        using var keywordResponse = await _httpClient.PostAsJsonAsync(requestUri, keywordPayload);
        var keywordResponseBody = await keywordResponse.Content.ReadAsStringAsync();

        if (!keywordResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure AI Search keyword fallback failed with {(int)keywordResponse.StatusCode} ({keywordResponse.ReasonPhrase}). " +
                $"URL: {requestUri}. Response: {keywordResponseBody}");
        }

        return ParseHistoricalSummaries(keywordResponseBody);
    }

    private static List<HistoricalSummary> ParseHistoricalSummaries(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return new List<HistoricalSummary>();
        }

        var results = new List<HistoricalSummary>();
        foreach (var item in values.EnumerateArray())
        {
            results.Add(new HistoricalSummary
            {
                Id = GetString(item, "id"),
                Author = GetString(item, "author"),
                WeekStartUtc = GetDate(item, "weekStartUtc"),
                WeekEndUtc = GetDate(item, "weekEndUtc"),
                GeneratedSummary = GetString(item, "generatedSummary"),
                Content = GetString(item, "content")
            });
        }

        return results;
    }

    public async Task UpsertWeeklySummaryAsync(WeeklyCompletedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var requestUri =
            $"{_endpoint}/indexes/{Uri.EscapeDataString(_indexName)}/docs/index?api-version={Uri.EscapeDataString(_apiVersion)}";

        using var content = JsonContent.Create(new
        {
            value = new[]
            {
                new Dictionary<string, object?>
                {
                    ["@search.action"] = "mergeOrUpload",
                    ["id"] = document.Id,
                    ["documentType"] = document.DocumentType,
                    ["author"] = document.Author,
                    ["weekStartUtc"] = document.WeekStartUtc,
                    ["weekEndUtc"] = document.WeekEndUtc,
                    ["completedPullRequestCount"] = document.CompletedPullRequestCount,
                    ["pullRequestIds"] = document.PullRequestIds,
                    ["workItemIds"] = document.WorkItemIds,
                    ["completedPullRequests"] = document.CompletedPullRequests,
                    ["content"] = document.Content,
                    ["contentVectorModel"] = document.ContentVectorModel,
                    ["contentVector"] = document.ContentVector,
                    ["generatedSummary"] = document.GeneratedSummary,
                    ["summaryVectorModel"] = document.SummaryVectorModel,
                    ["summaryVector"] = document.SummaryVector
                }
            }
        });

        using var response = await _httpClient.PostAsync(requestUri, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure AI Search upsert failed with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"URL: {requestUri}. Response: {responseBody}");
        }
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return property.GetString() ?? string.Empty;
    }

    private static DateTime GetDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return default;
        }

        return property.TryGetDateTime(out var date) ? date : default;
    }
}

public sealed class HistoricalSummary
{
    public string Id { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime WeekStartUtc { get; set; }
    public DateTime WeekEndUtc { get; set; }
    public string GeneratedSummary { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
