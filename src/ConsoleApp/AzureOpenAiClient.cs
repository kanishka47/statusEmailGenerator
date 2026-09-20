using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class AzureOpenAiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _chatDeployment;
    private readonly string _apiVersion;

    public AzureOpenAiClient(string endpoint, string apiKey, string deployment, string apiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployment);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiVersion);

        _endpoint = endpoint.TrimEnd('/');
        _chatDeployment = deployment.Trim();
        _apiVersion = apiVersion.Trim();

        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(90);
        _httpClient.DefaultRequestHeaders.Add("api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GenerateWeeklyReportAsync(string prompt)
    {
        return await GenerateAnswerAsync(
            prompt,
            "You are a concise engineering status report assistant.");
    }

    public async Task<string> GenerateAnswerAsync(string prompt, string systemPrompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);

        var requestUri =
            $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(_chatDeployment)}/chat/completions?api-version={Uri.EscapeDataString(_apiVersion)}";

        var bodyWithMaxCompletionTokens = new
        {
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = prompt }
            },
            max_completion_tokens = 4000
        };

        var primaryResult = await SendChatCompletionRequestAsync(requestUri, bodyWithMaxCompletionTokens);
        if (primaryResult.IsSuccess)
        {
            return primaryResult.Content!;
        }

        if (primaryResult.ResponseBody.IndexOf("max_completion_tokens", StringComparison.OrdinalIgnoreCase) >= 0 &&
            primaryResult.ResponseBody.IndexOf("max_tokens", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var bodyWithMaxTokens = new
            {
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = prompt }
                },
                max_tokens = 4000
            };

            var fallbackResult = await SendChatCompletionRequestAsync(requestUri, bodyWithMaxTokens);
            if (fallbackResult.IsSuccess)
            {
                return fallbackResult.Content!;
            }

            throw BuildHttpException(requestUri, fallbackResult);
        }

        throw BuildHttpException(requestUri, primaryResult);
    }

    public async Task<List<float>> GenerateEmbeddingAsync(string input, string embeddingDeployment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingDeployment);

        var requestUri =
            $"{_endpoint}/openai/deployments/{Uri.EscapeDataString(embeddingDeployment.Trim())}/embeddings?api-version={Uri.EscapeDataString(_apiVersion)}";

        var body = new
        {
            input
        };

        var jsonBody = JsonSerializer.Serialize(body);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(requestUri, content);

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Azure OpenAI embeddings request failed with {(int)response.StatusCode} ({response.ReasonPhrase}). " +
                $"URL: {requestUri}. Response: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Oops!Azure OpenAI embeddings response did not contain any data.");
        }

        var firstItem = data[0];
        if (!firstItem.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Azure OpenAI embeddings response did not contain a valid embedding array.");
        }

        var vector = new List<float>(embedding.GetArrayLength());
        foreach (var value in embedding.EnumerateArray())
        {
            vector.Add(value.GetSingle());
        }

        return vector;
    }

    private async Task<ChatCompletionResult> SendChatCompletionRequestAsync(string requestUri, object body)
    {
        var jsonBody = JsonSerializer.Serialize(body);
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(requestUri, content);

        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return ChatCompletionResult.Failure((int)response.StatusCode, response.ReasonPhrase, responseBody);
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Azure OpenAI response did not contain any choices.");
        }

        var firstChoice = choices[0];
        if (!firstChoice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Azure OpenAI response did not contain a valid message payload.");
        }

        if (!message.TryGetProperty("content", out var contentNode) || contentNode.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Azure OpenAI response message content is missing.");
        }

        var report = contentNode.GetString();
        if (string.IsNullOrWhiteSpace(report))
        {
            var finishReason = firstChoice.TryGetProperty("finish_reason", out var finishReasonNode)
                ? finishReasonNode.GetString()
                : "unknown";

            throw new InvalidOperationException(
                $"Azure OpenAI response content was empty (finish reason: {finishReason}). " +
                "The deployment may need a larger completion-token budget.");
        }

        return ChatCompletionResult.Success(report.Trim());
    }

    private static HttpRequestException BuildHttpException(string requestUri, ChatCompletionResult result)
    {
        var reason = string.IsNullOrWhiteSpace(result.ReasonPhrase) ? "Unknown" : result.ReasonPhrase;
        return new HttpRequestException(
            $"Azure OpenAI request failed with {result.StatusCode} ({reason}). " +
            $"URL: {requestUri}. Response: {result.ResponseBody}");
    }

    private sealed record ChatCompletionResult(bool IsSuccess, string? Content, int StatusCode, string? ReasonPhrase, string ResponseBody)
    {
        public static ChatCompletionResult Success(string content) => new(true, content, 200, "OK", string.Empty);

        public static ChatCompletionResult Failure(int statusCode, string? reasonPhrase, string responseBody) =>
            new(false, null, statusCode, reasonPhrase, responseBody);
    }
}
