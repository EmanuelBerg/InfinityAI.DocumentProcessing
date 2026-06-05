using InfinityAI.Api.Models.Gateway;
using System.Text.Json;

namespace InfinityAI.Api.Services;

public sealed class EmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<EmbeddingService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;

        var baseUrl = _configuration["InfinityAI:Gateway:BaseUrl"]
            ?? throw new InvalidOperationException("InfinityAI:Gateway:BaseUrl is missing.");

        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<EmbeddingExecutionResult> CreateEmbeddingAsync(
        string text,
        string providerType,
        string modelId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new EmbeddingExecutionResult { Embedding = [] };

        if (string.IsNullOrWhiteSpace(providerType))
            throw new InvalidOperationException("Embedding provider type is required.");

        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException("Embedding model id is required.");

        _logger.LogInformation(
            "Embedding service using model {Provider}/{Model}",
            providerType,
            modelId);

        var response = await _httpClient.PostAsJsonAsync(
            "embeddings",
            new
            {
                Provider = providerType,
                Model = modelId,
                Input = text
            },
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode == 429)
            {
                throw new InvalidOperationException(
                    "Embedding-kvoten är tillfälligt slut. Dokumentet kan fortfarande användas, men RAG-indexering behöver köras igen senare.");
            }

            throw new InvalidOperationException(
                $"Embedding misslyckades: {(int)response.StatusCode} {ExtractEmbeddingErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize<EmbeddingResponse>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return new EmbeddingExecutionResult
        {
            Embedding = result?.Data?.FirstOrDefault()?.Embedding ?? [],
            InputTokens = result?.InputTokens ?? 0
        };
    }

    private static string ExtractEmbeddingErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "Okänt fel.";

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? body;

                if (detail.ValueKind == JsonValueKind.Object &&
                    detail.TryGetProperty("error", out var error) &&
                    error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? body;
                }
            }

            if (doc.RootElement.TryGetProperty("error", out var rootError) &&
                rootError.TryGetProperty("message", out var rootMessage))
            {
                return rootMessage.GetString() ?? body;
            }

            return body;
        }
        catch
        {
            return body;
        }
    }

    private sealed class EmbeddingResponse
    {
        public List<EmbeddingData> Data { get; set; } = [];
        public int InputTokens { get; set; }
    }

    private sealed class EmbeddingData
    {
        public float[] Embedding { get; set; } = [];
    }
}