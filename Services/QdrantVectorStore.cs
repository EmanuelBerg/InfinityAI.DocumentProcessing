using InfinityAI.Api.Models.Qdrant;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InfinityAI.Api.Services;

public enum CollectionEnsureResult
{
    Created,
    Existing,
    DimensionMismatch,
    Error
}

public interface IQdrantVectorStore
{
    string GetCollectionName(string provider, string model);

    Task<CollectionEnsureResult> EnsureCollectionAsync(
        string collectionName,
        int vectorSize,
        CancellationToken ct = default);

    Task UpsertAsync(
        string collectionName,
        Guid pointId,
        float[] vector,
        QdrantChunkPayload payload,
        CancellationToken ct = default);

    Task<IReadOnlyList<QdrantSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        IReadOnlyList<Guid> documentIds,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<QdrantSearchResult>> SearchByCollectionsAsync(
        string collectionName,
        float[] queryVector,
        IReadOnlyList<Guid> allowedCollectionIds,
        int limit,
        CancellationToken ct = default);

    Task SetDocumentCollectionPayloadAsync(
        string collectionName,
        Guid documentId,
        List<string> collectionIds,
        List<string> allowedGroupIds,
        CancellationToken ct = default);

    Task DeleteByDocumentIdAsync(
        string collectionName,
        Guid documentId,
        CancellationToken ct = default);

    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}

public sealed class QdrantVectorStore : IQdrantVectorStore
{
    private readonly HttpClient _http;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantVectorStore> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public QdrantVectorStore(
        HttpClient httpClient,
        IOptions<QdrantOptions> options,
        ILogger<QdrantVectorStore> logger)
    {
        _options = options.Value;
        _logger = logger;
        _http = httpClient;
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ── Collection name ───────────────────────────────────────────────────────

    public string GetCollectionName(string provider, string model)
    {
        var raw = $"{_options.CollectionPrefix}_{provider}_{model}";
        return Regex.Replace(raw.ToLowerInvariant(), @"[^a-z0-9_]", "_");
    }

    // ── Collection management ─────────────────────────────────────────────────

    public async Task<CollectionEnsureResult> EnsureCollectionAsync(
        string collectionName,
        int vectorSize,
        CancellationToken ct = default)
    {
        var check = await _http.GetAsync($"collections/{collectionName}", ct);

        if (check.StatusCode == HttpStatusCode.OK)
        {
            var raw = await check.Content.ReadAsStringAsync(ct);
            try
            {
                var info = JsonSerializer.Deserialize<QdrantCollectionInfoResponse>(raw, JsonOpts);
                var existingSize = info?.Result?.Config?.Params?.Vectors?.Size ?? 0;

                if (existingSize != 0 && existingSize != vectorSize)
                {
                    _logger.LogError(
                        "[QDRANT] Dimension mismatch for collection '{Collection}': expected={Expected}, actual={Actual}. " +
                        "Drop the collection manually or re-index with the correct embedding model.",
                        collectionName, vectorSize, existingSize);
                    return CollectionEnsureResult.DimensionMismatch;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[QDRANT] Could not parse collection info for '{Collection}' — skipping dimension validation.",
                    collectionName);
            }

            _logger.LogInformation(
                "[QDRANT] Collection '{Collection}' already exists (size={Size}).",
                collectionName, vectorSize);
            return CollectionEnsureResult.Existing;
        }

        if (check.StatusCode != HttpStatusCode.NotFound)
        {
            var body = await check.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "[QDRANT] Unexpected status {Status} when checking collection '{Collection}': {Body}",
                (int)check.StatusCode, collectionName, body);
        }

        _logger.LogInformation(
            "[QDRANT] Creating collection '{Collection}' (size={Size}, distance={Distance}).",
            collectionName, vectorSize, _options.Distance);

        var createBody = new
        {
            vectors = new
            {
                size = vectorSize,
                distance = _options.Distance
            }
        };

        var create = await _http.PutAsJsonAsync($"collections/{collectionName}", createBody, ct);
        var createResult = await create.Content.ReadAsStringAsync(ct);

        if (!create.IsSuccessStatusCode)
        {
            _logger.LogError(
                "[QDRANT] Failed to create collection '{Collection}': {Status} {Body}",
                collectionName, (int)create.StatusCode, createResult);
            return CollectionEnsureResult.Error;
        }

        _logger.LogInformation(
            "[QDRANT] Collection '{Collection}' created successfully.",
            collectionName);
        return CollectionEnsureResult.Created;
    }

    // ── Upsert ────────────────────────────────────────────────────────────────

    public async Task UpsertAsync(
        string collectionName,
        Guid pointId,
        float[] vector,
        QdrantChunkPayload payload,
        CancellationToken ct = default)
    {
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = pointId.ToString(),
                    vector,
                    payload = BuildPayloadDictionary(payload)
                }
            }
        };

        var response = await _http.PutAsJsonAsync($"collections/{collectionName}/points", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "[QDRANT] Upsert failed for chunk {ChunkId} in '{Collection}': {Status} {Body}",
                pointId, collectionName, (int)response.StatusCode, err);

            throw new InvalidOperationException(
                $"Qdrant upsert failed: {(int)response.StatusCode}");
        }

        _logger.LogDebug(
            "[QDRANT] Indexed chunk {ChunkId} in '{Collection}' (document={DocumentId}, index={ChunkIndex}).",
            pointId, collectionName, payload.DocumentId, payload.ChunkIndex);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<QdrantSearchResult>> SearchAsync(
        string collectionName,
        float[] queryVector,
        IReadOnlyList<Guid> documentIds,
        int limit,
        CancellationToken ct = default)
    {
        // Filter by document_id — documents are global assets shared across users.
        // Access control is enforced upstream by resolving only the caller's accessible documentIds.
        object docFilter = documentIds.Count == 1
            ? new { key = "document_id", match = new { value = documentIds[0].ToString() } }
            : (object)new { key = "document_id", match = new { any = documentIds.Select(id => id.ToString()).ToArray() } };

        var body = new
        {
            vector = queryVector,
            filter = new { must = new[] { docFilter } },
            limit,
            with_payload = true,
            with_vector = false,
            score_threshold = 0.0
        };

        var response = await _http.PostAsJsonAsync(
            $"collections/{collectionName}/points/search", body, ct);

        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "[QDRANT] Search failed in '{Collection}': {Status} {Body}",
                collectionName, (int)response.StatusCode, raw);

            throw new InvalidOperationException(
                $"Qdrant search failed: {(int)response.StatusCode}");
        }

        QdrantSearchResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<QdrantSearchResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[QDRANT] Failed to deserialize search response from '{Collection}'.", collectionName);
            return [];
        }

        var results = new List<QdrantSearchResult>(parsed?.Result?.Count ?? 0);

        foreach (var hit in parsed?.Result ?? [])
        {
            if (!Guid.TryParse(hit.Payload.GetValueOrDefault("document_chunk_id")?.ToString(), out var chunkId))
                continue;

            if (!Guid.TryParse(hit.Payload.GetValueOrDefault("document_id")?.ToString(), out var documentId))
                continue;

            results.Add(new QdrantSearchResult
            {
                DocumentChunkId = chunkId,
                DocumentId = documentId,
                Score = hit.Score,
                Payload = MapPayload(hit.Payload)
            });
        }

        _logger.LogInformation(
            "[QDRANT] Search in '{Collection}' returned {Count} hits across {DocCount} documents.",
            collectionName, results.Count, documentIds.Count);

        return results;
    }

    // ── Search by collections ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<QdrantSearchResult>> SearchByCollectionsAsync(
        string collectionName,
        float[] queryVector,
        IReadOnlyList<Guid> allowedCollectionIds,
        int limit,
        CancellationToken ct = default)
    {
        var body = new
        {
            vector = queryVector,
            filter = new
            {
                must = new[]
                {
                    new
                    {
                        key = "collection_ids",
                        match = new { any = allowedCollectionIds.Select(id => id.ToString()).ToArray() }
                    }
                }
            },
            limit,
            with_payload = true,
            with_vector = false,
            score_threshold = 0.0
        };

        var response = await _http.PostAsJsonAsync(
            $"collections/{collectionName}/points/search", body, ct);

        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "[QDRANT] Collection search failed in '{Collection}': {Status} {Body}",
                collectionName, (int)response.StatusCode, raw);

            throw new InvalidOperationException(
                $"Qdrant collection search failed: {(int)response.StatusCode}");
        }

        QdrantSearchResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<QdrantSearchResponse>(raw, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "[QDRANT] Failed to deserialize collection search response from '{Collection}'.",
                collectionName);
            return [];
        }

        var results = new List<QdrantSearchResult>(parsed?.Result?.Count ?? 0);

        foreach (var hit in parsed?.Result ?? [])
        {
            if (!Guid.TryParse(hit.Payload.GetValueOrDefault("document_chunk_id")?.ToString(), out var chunkId))
                continue;
            if (!Guid.TryParse(hit.Payload.GetValueOrDefault("document_id")?.ToString(), out var documentId))
                continue;

            results.Add(new QdrantSearchResult
            {
                DocumentChunkId = chunkId,
                DocumentId = documentId,
                Score = hit.Score,
                Payload = MapPayload(hit.Payload)
            });
        }

        _logger.LogInformation(
            "[QDRANT] Collection search in '{Collection}' returned {Count} hits.",
            collectionName, results.Count);

        return results;
    }

    // ── Set payload ───────────────────────────────────────────────────────────

    public async Task SetDocumentCollectionPayloadAsync(
        string collectionName,
        Guid documentId,
        List<string> collectionIds,
        List<string> allowedGroupIds,
        CancellationToken ct = default)
    {
        var body = new
        {
            payload = new Dictionary<string, object?>
            {
                ["collection_ids"]    = (object)collectionIds,
                ["allowed_group_ids"] = (object)allowedGroupIds
            },
            filter = new
            {
                must = new[]
                {
                    new
                    {
                        key   = "document_id",
                        match = new { value = documentId.ToString() }
                    }
                }
            }
        };

        var response = await _http.PostAsJsonAsync(
            $"collections/{collectionName}/points/payload", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "[QDRANT] SetPayload failed for document {DocumentId} in '{Collection}': {Status} {Body}",
                documentId, collectionName, (int)response.StatusCode, err);

            throw new InvalidOperationException(
                $"Qdrant set-payload failed: {(int)response.StatusCode}");
        }

        _logger.LogDebug(
            "[QDRANT] Updated collection payload for document {DocumentId} in '{Collection}'. Collections={Count}.",
            documentId, collectionName, collectionIds.Count);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public async Task DeleteByDocumentIdAsync(
        string collectionName,
        Guid documentId,
        CancellationToken ct = default)
    {
        var body = new
        {
            filter = new
            {
                must = new[]
                {
                    new
                    {
                        key = "document_id",
                        match = new { value = documentId.ToString() }
                    }
                }
            }
        };

        var response = await _http.PostAsJsonAsync(
            $"collections/{collectionName}/points/delete", body, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "[QDRANT] Delete failed for document {DocumentId} in '{Collection}': {Status} {Body}",
                documentId, collectionName, (int)response.StatusCode, err);

            throw new InvalidOperationException(
                $"Qdrant delete failed: {(int)response.StatusCode}");
        }

        _logger.LogInformation(
            "[QDRANT] Deleted vectors for document {DocumentId} from '{Collection}'.",
            documentId, collectionName);
    }

    // ── Health ────────────────────────────────────────────────────────────────

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("collections", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[QDRANT] Health check failed.");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> BuildPayloadDictionary(QdrantChunkPayload p) =>
        new()
        {
            ["document_chunk_id"]  = p.DocumentChunkId.ToString(),
            ["document_id"]        = p.DocumentId.ToString(),
            ["user_id"]            = p.UserId?.ToString() ?? "",
            ["file_id"]            = p.FileId?.ToString() ?? "",
            ["file_name"]          = p.FileName,
            ["document_title"]     = p.DocumentTitle,
            ["chunk_index"]        = p.ChunkIndex,
            ["page_number"]        = (object?)p.PageNumber,
            ["heading"]            = p.Heading,
            ["embedding_model"]    = p.EmbeddingModel,
            ["embedding_provider"] = p.EmbeddingProvider,
            ["content_preview"]    = p.ContentPreview,
            ["created_utc"]        = p.CreatedUtc.ToString("O"),
            ["collection_ids"]     = (object?)(p.CollectionIds ?? []),
            ["allowed_group_ids"]  = (object?)(p.AllowedGroupIds ?? [])
        };

    private static QdrantChunkPayload MapPayload(Dictionary<string, object?> p)
    {
        Guid.TryParse(p.GetValueOrDefault("document_chunk_id")?.ToString(), out var chunkId);
        Guid.TryParse(p.GetValueOrDefault("document_id")?.ToString(), out var docId);
        Guid? userId = Guid.TryParse(p.GetValueOrDefault("user_id")?.ToString(), out var _userId) ? _userId : null;
        Guid? fileId = Guid.TryParse(p.GetValueOrDefault("file_id")?.ToString(), out var _fileId) ? _fileId : null;

        int? pageNumber = null;
        if (p.GetValueOrDefault("page_number") is JsonElement pe && pe.ValueKind == JsonValueKind.Number)
            pageNumber = pe.GetInt32();

        int chunkIndex = 0;
        if (p.GetValueOrDefault("chunk_index") is JsonElement ci && ci.ValueKind == JsonValueKind.Number)
            chunkIndex = ci.GetInt32();

        var collectionIds = ParseStringArray(p.GetValueOrDefault("collection_ids"));
        var allowedGroupIds = ParseStringArray(p.GetValueOrDefault("allowed_group_ids"));

        return new QdrantChunkPayload
        {
            DocumentChunkId   = chunkId,
            DocumentId        = docId,
            UserId            = userId,
            FileId            = fileId,
            FileName          = p.GetValueOrDefault("file_name")?.ToString() ?? "",
            DocumentTitle     = p.GetValueOrDefault("document_title")?.ToString() ?? "",
            ChunkIndex        = chunkIndex,
            PageNumber        = pageNumber,
            Heading           = p.GetValueOrDefault("heading")?.ToString(),
            EmbeddingModel    = p.GetValueOrDefault("embedding_model")?.ToString() ?? "",
            EmbeddingProvider = p.GetValueOrDefault("embedding_provider")?.ToString() ?? "",
            ContentPreview    = p.GetValueOrDefault("content_preview")?.ToString() ?? "",
            CollectionIds     = collectionIds.Count > 0 ? collectionIds : null,
            AllowedGroupIds   = allowedGroupIds.Count > 0 ? allowedGroupIds : null
        };
    }

    private static List<string> ParseStringArray(object? raw)
    {
        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return je.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

            if (je.ValueKind == JsonValueKind.String)
            {
                var s = je.GetString();
                return string.IsNullOrWhiteSpace(s)
                    ? []
                    : s.Split(',').Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
        }

        return [];
    }

    // ── Private response models ───────────────────────────────────────────────

    private sealed class QdrantSearchResponse
    {
        [JsonPropertyName("result")]
        public List<QdrantHit>? Result { get; set; }
    }

    private sealed class QdrantHit
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonPropertyName("payload")]
        public Dictionary<string, object?> Payload { get; set; } = [];
    }

    private sealed class QdrantCollectionInfoResponse
    {
        [JsonPropertyName("result")]
        public QdrantCollectionResult? Result { get; set; }
    }

    private sealed class QdrantCollectionResult
    {
        [JsonPropertyName("config")]
        public QdrantCollectionConfig? Config { get; set; }
    }

    private sealed class QdrantCollectionConfig
    {
        [JsonPropertyName("params")]
        public QdrantCollectionParams? Params { get; set; }
    }

    private sealed class QdrantCollectionParams
    {
        [JsonPropertyName("vectors")]
        public QdrantVectorsConfig? Vectors { get; set; }
    }

    private sealed class QdrantVectorsConfig
    {
        [JsonPropertyName("size")]
        public int Size { get; set; }

        [JsonPropertyName("distance")]
        public string Distance { get; set; } = "";
    }
}
