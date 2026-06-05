using System.Text.RegularExpressions;
using InfinityAI.Data;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Llm;
using InfinityAI.Api.Models.Qdrant;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Services.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace InfinityAI.Api.Services;

public interface IDocumentRetrievalService
{
    Task<List<RetrievedDocumentChunk>> RetrieveRelevantChunksAsync(
        Guid userId,
        List<Guid> fileIds,
        string query,
        int top = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Returns ALL chunks for the given files in ChunkIndex order — no similarity ranking, no TopK.
    /// Use for ListAll / TableEnumeration queries where full coverage is required.
    /// </summary>
    Task<List<RetrievedDocumentChunk>> RetrieveAllChunksAsync(
        Guid userId,
        List<Guid> fileIds,
        CancellationToken ct = default);

    Task<List<RetrievedDocumentChunk>> RetrieveFromCollectionsAsync(
        List<Guid> allowedCollectionIds,
        string query,
        int top = 0,
        CancellationToken ct = default);
}

public sealed class DocumentRetrievalService : IDocumentRetrievalService
{
    private readonly ApplicationDbContext _db;
    private readonly ILlmRouter _llmRouter;
    private readonly IModelSelectionService _modelSelectionService;
    private readonly ICosineSimilarityService _cosineSimilarity;
    private readonly IQdrantVectorStore _qdrant;
    private readonly RagOptions _options;
    private readonly QdrantOptions _qdrantOptions;
    private readonly ILogger<DocumentRetrievalService> _logger;

    // Score assigned to chunks that contain an exact identifier match.
    // Set above MinRelevanceScore (default 0.45) and slightly below 1.0 so vector-exact
    // matches still win, but keyword hits always surface above the score filter.
    private const double KeywordMatchScore = 0.92;

    // Matches opaque identifiers that embedding models handle poorly:
    //   Circuit codes : FNT002400, ABC12345, FNT-002400
    //   IPv4 addresses: 10.255.1.10
    private static readonly Regex IdentifierRegex = new(
        @"\b(?:[A-Z]{2,6}-?\d{3,}|\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b",
        RegexOptions.Compiled);

    public DocumentRetrievalService(
        ApplicationDbContext db,
        ILlmRouter llmRouter,
        IModelSelectionService modelSelectionService,
        ICosineSimilarityService cosineSimilarity,
        IQdrantVectorStore qdrant,
        IOptions<RagOptions> options,
        IOptions<QdrantOptions> qdrantOptions,
        ILogger<DocumentRetrievalService> logger)
    {
        _db = db;
        _llmRouter = llmRouter;
        _modelSelectionService = modelSelectionService;
        _cosineSimilarity = cosineSimilarity;
        _qdrant = qdrant;
        _options = options.Value;
        _qdrantOptions = qdrantOptions.Value;
        _logger = logger;
    }

    public async Task<List<RetrievedDocumentChunk>> RetrieveRelevantChunksAsync(
        Guid userId,
        List<Guid> fileIds,
        string query,
        int top = 0,
        CancellationToken ct = default)
    {
        if (fileIds.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var effectiveTop = top > 0 ? top : _options.TopK;
        var isDocumentWideQuery = _options.EnableDocumentWideQueryDetection && IsDocumentWideQuery(query);

        if (isDocumentWideQuery)
            effectiveTop = Math.Max(effectiveTop, _options.DocumentWideQueryTopK);

        var sourceFiles = await _db.Files
            .AsNoTracking()
            .Where(x => fileIds.Contains(x.Id))
            .Select(x => new { x.Id, x.OriginalFileName, x.StoredFileId })
            .ToListAsync(ct);

        _logger.LogInformation(
            "[RETRIEVAL] Started. UserId={UserId}, Files={FileCount}, TopK={TopK}, DocumentWide={DocumentWide}, Query={Query}\nSourceFiles:\n{FileList}",
            userId,
            fileIds.Count,
            effectiveTop,
            isDocumentWideQuery,
            Preview(query, 200),
            string.Join("\n", sourceFiles.Select(f => $"  - {f.OriginalFileName}")));

        // Resolve ApplicationFile IDs → global Document IDs.
        // Phase 4: a single global Document is shared when two users upload the same content.
        // Access control is already enforced by ChatEndpoints validating that fileIds belong to userId.
        var storedFileIds = sourceFiles
            .Where(f => f.StoredFileId.HasValue)
            .Select(f => f.StoredFileId!.Value)
            .Distinct()
            .ToList();

        var documentIds = await _db.Documents
            .AsNoTracking()
            .Where(d =>
                (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                (d.FileId.HasValue && fileIds.Contains(d.FileId.Value)))
            .Select(d => d.Id)
            .Distinct()
            .ToListAsync(ct);

        if (documentIds.Count == 0)
        {
            _logger.LogWarning(
                "[RETRIEVAL] No documents found. UserId={UserId}, IncomingFileIds={FileIds}, StoredFileIds={StoredFileIds}. " +
                "Possible causes: FileType was not 'Document' at upload time (no Document record created), " +
                "or Document.FileId/StoredFileId mismatch.",
                userId,
                string.Join(",", fileIds),
                string.Join(",", storedFileIds));
            return [];
        }

        _logger.LogInformation(
            "[RETRIEVAL] Resolved {FileCount} files → {DocCount} global documents.",
            fileIds.Count, documentIds.Count);

        // Select embedding model once — used for both query embedding and retrieval filter
        var selectionResult = await _modelSelectionService.SelectBestModelAsync(
            LlmCapability.Embeddings,
            profileId: null,
            ct);

        var embeddingModelId = selectionResult.Model.ModelId;
        var embeddingProvider = selectionResult.Model.LlmProvider?.ProviderType ?? "";

        if (string.IsNullOrWhiteSpace(embeddingModelId))
        {
            _logger.LogWarning("[RETRIEVAL] Selected embedding model has no ModelId — aborting.");
            return [];
        }

        _logger.LogInformation(
            "[RETRIEVAL] Embedding model selected. Provider={Provider}, Model={Model}",
            embeddingProvider,
            embeddingModelId);

        // Use explicit model so query and stored embeddings are guaranteed to match
        var queryEmbedding = await _llmRouter.CreateEmbeddingWithModelAsync(
            query,
            embeddingProvider,
            embeddingModelId,
            ct);

        if (queryEmbedding.Length == 0)
        {
            _logger.LogWarning("[RETRIEVAL] Query embedding returned empty vector — aborting.");
            return [];
        }

        _logger.LogInformation(
            "[RETRIEVAL] Query embedding created. Provider={Provider}, Model={Model}, Dimensions={Dimensions}",
            embeddingProvider,
            embeddingModelId,
            queryEmbedding.Length);

        // Extract opaque identifiers (circuit codes, IPs) for keyword-boosted retrieval.
        // Vector similarity alone cannot reliably rank chunks that contain these tokens.
        var identifiers = ExtractLookupIdentifiers(query);
        if (identifiers.Count > 0)
            _logger.LogInformation(
                "[RETRIEVAL] Hybrid mode — identifiers detected: [{Ids}]",
                string.Join(", ", identifiers));

        // Qdrant-first search with MySQL cosine-similarity fallback
        var scored = await SearchWithFallbackAsync(
            queryEmbedding, embeddingModelId, embeddingProvider,
            documentIds, effectiveTop, identifiers, ct);

        // Log top-20 before any filtering
        var top20 = scored.OrderByDescending(x => x.Score).Take(20).ToList();
        LogCandidates(top20, "TOP 20 BEFORE FILTERING");

        // Apply score threshold; fall back to top-3 if nothing passes
        var aboveThreshold = scored
            .Where(x => x.Score >= _options.MinRelevanceScore)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (aboveThreshold.Count == 0)
        {
            _logger.LogWarning(
                "[RETRIEVAL] No chunks above MinRelevanceScore={Score} — falling back to top 3.",
                _options.MinRelevanceScore);
            aboveThreshold = scored.OrderByDescending(x => x.Score).Take(3).ToList();
        }

        List<RetrievedDocumentChunk> ranked;

        if (isDocumentWideQuery)
            ranked = SelectDocumentWide(aboveThreshold, scored, effectiveTop);
        else
            ranked = SelectWithPageDiversity(aboveThreshold, effectiveTop, _options.MaxChunksPerPage);

        // Quality metrics
        var avgScore = ranked.Count > 0 ? ranked.Average(x => x.Score) : 0d;
        var maxScore = ranked.Count > 0 ? ranked.Max(x => x.Score) : 0d;
        var minScore = ranked.Count > 0 ? ranked.Min(x => x.Score) : 0d;

        var chunksByFile = ranked.GroupBy(x => x.FileName).OrderByDescending(g => g.Count());

        _logger.LogInformation(
            "[RETRIEVAL] Completed. RetrievedChunks={Count}, AvgScore={Avg:F4}, MaxScore={Max:F4}, MinScore={Min:F4}\nChunkSources:\n{Sources}",
            ranked.Count,
            avgScore,
            maxScore,
            minScore,
            string.Join("\n", chunksByFile.Select(g => $"  - {g.Key} ({g.Count()} chunks)")));

        LogCandidates(ranked, $"FINAL {ranked.Count} CHUNKS");

        return ranked;
    }

    // ── Full-document retrieval (no similarity ranking) ───────────────────────

    public async Task<List<RetrievedDocumentChunk>> RetrieveAllChunksAsync(
        Guid userId,
        List<Guid> fileIds,
        CancellationToken ct = default)
    {
        if (fileIds.Count == 0)
            return [];

        var documentIds = await ResolveFileIdsToDocumentIdsAsync(fileIds, ct);

        if (documentIds.Count == 0)
        {
            _logger.LogWarning(
                "[RETRIEVAL][FULL] No documents resolved. UserId={UserId}, FileIds={FileIds}",
                userId, string.Join(",", fileIds));
            return [];
        }

        _logger.LogInformation(
            "[RETRIEVAL][FULL] Fetching all chunks. UserId={UserId}, Documents={DocCount}",
            userId, documentIds.Count);

        var chunks = await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
                .ThenInclude(x => x.File)
            .Where(x => documentIds.Contains(x.DocumentId) && x.Document.Status == "Ready")
            .OrderBy(x => x.DocumentId)
            .ThenBy(x => x.ChunkIndex)
            .ToListAsync(ct);

        _logger.LogInformation(
            "[RETRIEVAL][FULL] Completed. TotalChunks={Count}, Documents={Docs}",
            chunks.Count,
            string.Join(", ", chunks.GroupBy(c => c.Document.Title).Select(g => $"{g.Key}({g.Count()})")));

        return chunks.Select(chunk => new RetrievedDocumentChunk
        {
            DocumentId    = chunk.DocumentId,
            ChunkId       = chunk.Id,
            FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
            DocumentTitle = chunk.Document.Title,
            ChunkIndex    = chunk.ChunkIndex,
            PageNumber    = chunk.PageNumber,
            Heading       = chunk.Heading,
            Content       = chunk.Content,
            Score         = 1.0  // sentinel — bypasses MinRelevanceScore filter
        }).ToList();
    }

    // ── Shared resolution helper ──────────────────────────────────────────────

    private async Task<List<Guid>> ResolveFileIdsToDocumentIdsAsync(
        List<Guid> fileIds,
        CancellationToken ct)
    {
        var storedFileIds = await _db.Files
            .AsNoTracking()
            .Where(x => fileIds.Contains(x.Id) && x.StoredFileId.HasValue)
            .Select(x => x.StoredFileId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return await _db.Documents
            .AsNoTracking()
            .Where(d =>
                (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                (d.FileId.HasValue && fileIds.Contains(d.FileId.Value)))
            .Select(d => d.Id)
            .Distinct()
            .ToListAsync(ct);
    }

    // ── Collection-aware retrieval ────────────────────────────────────────────

    public async Task<List<RetrievedDocumentChunk>> RetrieveFromCollectionsAsync(
        List<Guid> allowedCollectionIds,
        string query,
        int top = 0,
        CancellationToken ct = default)
    {
        if (allowedCollectionIds.Count == 0 || string.IsNullOrWhiteSpace(query))
            return [];

        var effectiveTop = top > 0 ? top : _options.TopK;

        var selectionResult = await _modelSelectionService.SelectBestModelAsync(
            LlmCapability.Embeddings, profileId: null, ct);

        var embeddingModelId = selectionResult.Model.ModelId;
        var embeddingProvider = selectionResult.Model.LlmProvider?.ProviderType ?? "";

        if (string.IsNullOrWhiteSpace(embeddingModelId))
        {
            _logger.LogWarning("[RETRIEVAL][KC] No embedding model available — aborting.");
            return [];
        }

        var queryEmbedding = await _llmRouter.CreateEmbeddingWithModelAsync(
            query, embeddingProvider, embeddingModelId, ct);

        if (queryEmbedding.Length == 0)
        {
            _logger.LogWarning("[RETRIEVAL][KC] Query embedding returned empty vector — aborting.");
            return [];
        }

        List<RetrievedDocumentChunk> scored;

        if (_qdrantOptions.IsEnabled && _qdrantOptions.SearchEnabled)
        {
            var collectionName = _qdrant.GetCollectionName(embeddingProvider, embeddingModelId);
            try
            {
                var hits = await _qdrant.SearchByCollectionsAsync(
                    collectionName, queryEmbedding, allowedCollectionIds, effectiveTop * 2, ct);

                if (hits.Count > 0)
                {
                    scored = await MapQdrantHitsToChunksAsync(hits, ct);
                    _logger.LogInformation(
                        "[RETRIEVAL][KC] Qdrant returned {Count} candidates from '{Collection}'.",
                        scored.Count, collectionName);
                    return ApplyScoreFilterAndRank(scored, effectiveTop);
                }

                _logger.LogInformation(
                    "[RETRIEVAL][KC] Qdrant returned 0 results — trying MySQL fallback.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RETRIEVAL][KC] Qdrant search failed — falling back to MySQL.");
            }
        }

        scored = await SearchFromCollectionsMySqlAsync(
            queryEmbedding, embeddingModelId, embeddingProvider, allowedCollectionIds, ct);

        return ApplyScoreFilterAndRank(scored, effectiveTop);
    }

    private async Task<List<RetrievedDocumentChunk>> MapQdrantHitsToChunksAsync(
        IReadOnlyList<QdrantSearchResult> hits,
        CancellationToken ct)
    {
        var chunkIds = hits.Select(h => h.DocumentChunkId).ToList();

        var chunks = await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
                .ThenInclude(x => x.File)
            .Where(x => chunkIds.Contains(x.Id))
            .ToListAsync(ct);

        var chunkMap = chunks.ToDictionary(c => c.Id);
        var result = new List<RetrievedDocumentChunk>(hits.Count);

        foreach (var hit in hits)
        {
            if (!chunkMap.TryGetValue(hit.DocumentChunkId, out var chunk))
            {
                _logger.LogWarning(
                    "[RETRIEVAL][KC] Qdrant hit {ChunkId} not found in MySQL — stale entry.",
                    hit.DocumentChunkId);
                continue;
            }

            result.Add(new RetrievedDocumentChunk
            {
                DocumentId    = chunk.DocumentId,
                ChunkId       = chunk.Id,
                FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
                DocumentTitle = chunk.Document.Title,
                ChunkIndex    = chunk.ChunkIndex,
                PageNumber    = chunk.PageNumber,
                Heading       = chunk.Heading,
                Content       = chunk.Content,
                Score         = hit.Score
            });
        }

        return result;
    }

    private async Task<List<RetrievedDocumentChunk>> SearchFromCollectionsMySqlAsync(
        float[] queryEmbedding,
        string embeddingModelId,
        string embeddingProvider,
        List<Guid> allowedCollectionIds,
        CancellationToken ct)
    {
        _logger.LogInformation("[RETRIEVAL][KC] Using MySQL cosine similarity search for collections.");

        var documentIds = await _db.KnowledgeCollectionDocuments
            .AsNoTracking()
            .Where(x => allowedCollectionIds.Contains(x.KnowledgeCollectionId))
            .Select(x => x.DocumentId)
            .Distinct()
            .ToListAsync(ct);

        if (documentIds.Count == 0)
            return [];

        var embeddings = await _db.DocumentChunkEmbeddings
            .Include(x => x.DocumentChunk)
                .ThenInclude(x => x.Document)
                    .ThenInclude(x => x.File)
            .Where(x =>
                x.Model == embeddingModelId &&
                (x.EmbeddingProvider == "" || x.EmbeddingProvider == embeddingProvider) &&
                documentIds.Contains(x.DocumentChunk.DocumentId) &&
                x.DocumentChunk.Document.Status == "Ready")
            .ToListAsync(ct);

        _logger.LogInformation(
            "[RETRIEVAL][KC] MySQL collection candidates loaded. Count={Count}", embeddings.Count);

        var scored = new List<RetrievedDocumentChunk>(embeddings.Count);

        foreach (var emb in embeddings)
        {
            float[] vector;
            try { vector = System.Text.Json.JsonSerializer.Deserialize<float[]>(emb.VectorJson) ?? []; }
            catch { continue; }

            if (vector.Length != queryEmbedding.Length) continue;

            var score = _cosineSimilarity.Calculate(queryEmbedding, vector);
            var chunk = emb.DocumentChunk;

            scored.Add(new RetrievedDocumentChunk
            {
                DocumentId    = chunk.DocumentId,
                ChunkId       = emb.DocumentChunkId,
                FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
                DocumentTitle = chunk.Document.Title,
                ChunkIndex    = chunk.ChunkIndex,
                PageNumber    = chunk.PageNumber,
                Heading       = chunk.Heading,
                Content       = chunk.Content,
                Score         = score
            });
        }

        return scored;
    }

    private List<RetrievedDocumentChunk> ApplyScoreFilterAndRank(
        List<RetrievedDocumentChunk> scored,
        int top)
    {
        var aboveThreshold = scored
            .Where(x => x.Score >= _options.MinRelevanceScore)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (aboveThreshold.Count == 0)
            aboveThreshold = scored.OrderByDescending(x => x.Score).Take(3).ToList();

        return SelectWithPageDiversity(aboveThreshold, top, _options.MaxChunksPerPage);
    }

    // ── Search strategies ─────────────────────────────────────────────────────

    private async Task<List<RetrievedDocumentChunk>> SearchWithFallbackAsync(
        float[] queryEmbedding,
        string embeddingModelId,
        string embeddingProvider,
        List<Guid> documentIds,
        int limit,
        List<string> identifiers,
        CancellationToken ct)
    {
        if (_qdrantOptions.IsEnabled && _qdrantOptions.SearchEnabled)
        {
            var collectionName = _qdrant.GetCollectionName(embeddingProvider, embeddingModelId);
            try
            {
                var qdrantResults = await SearchViaQdrantAsync(
                    collectionName, queryEmbedding, documentIds, limit * 2, identifiers, ct);

                if (qdrantResults.Count > 0)
                {
                    _logger.LogInformation(
                        "[RETRIEVAL] Qdrant returned {Count} candidates from '{Collection}'.",
                        qdrantResults.Count, collectionName);
                    return qdrantResults;
                }

                _logger.LogInformation(
                    "[RETRIEVAL] Qdrant returned 0 results from '{Collection}' — trying MySQL fallback.",
                    collectionName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[RETRIEVAL] Qdrant search failed — falling back to MySQL cosine similarity.");
            }
        }

        return await SearchViaMySqlAsync(
            queryEmbedding, embeddingModelId, embeddingProvider, documentIds, identifiers, ct);
    }

    private async Task<List<RetrievedDocumentChunk>> SearchViaQdrantAsync(
        string collectionName,
        float[] queryEmbedding,
        List<Guid> documentIds,
        int limit,
        List<string> identifiers,
        CancellationToken ct)
    {
        var hits = await _qdrant.SearchAsync(collectionName, queryEmbedding, documentIds, limit, ct);

        if (hits.Count == 0)
            return [];

        // MySQL is source of truth for content — fetch chunks by the IDs Qdrant returned
        var chunkIds = hits.Select(h => h.DocumentChunkId).ToList();

        var chunks = await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
                .ThenInclude(x => x.File)
            .Where(x => chunkIds.Contains(x.Id))
            .ToListAsync(ct);

        var chunkMap = chunks.ToDictionary(c => c.Id);
        var result = new List<RetrievedDocumentChunk>(hits.Count);

        foreach (var hit in hits)
        {
            if (!chunkMap.TryGetValue(hit.DocumentChunkId, out var chunk))
            {
                _logger.LogWarning(
                    "[RETRIEVAL] Qdrant hit {ChunkId} not found in MySQL — stale index entry.",
                    hit.DocumentChunkId);
                continue;
            }

            result.Add(new RetrievedDocumentChunk
            {
                DocumentId    = chunk.DocumentId,
                ChunkId       = chunk.Id,
                FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
                DocumentTitle = chunk.Document.Title,
                ChunkIndex    = chunk.ChunkIndex,
                PageNumber    = chunk.PageNumber,
                Heading       = chunk.Heading,
                Content       = chunk.Content,
                Score         = hit.Score
            });
        }

        // Supplement Qdrant results with keyword hits for opaque identifiers
        if (identifiers.Count > 0)
        {
            var keywordChunks = await SearchByKeywordsAsync(documentIds, identifiers, ct);
            result = MergeWithKeywordHits(result, keywordChunks);
        }

        return result;
    }

    private async Task<List<RetrievedDocumentChunk>> SearchViaMySqlAsync(
        float[] queryEmbedding,
        string embeddingModelId,
        string embeddingProvider,
        List<Guid> documentIds,
        List<string> identifiers,
        CancellationToken ct)
    {
        _logger.LogInformation("[RETRIEVAL] Using MySQL cosine similarity search.");

        var embeddings = await _db.DocumentChunkEmbeddings
            .Include(x => x.DocumentChunk)
                .ThenInclude(x => x.Document)
                    .ThenInclude(x => x.File)
            .Where(x =>
                x.Model == embeddingModelId &&
                (x.EmbeddingProvider == "" || x.EmbeddingProvider == embeddingProvider) &&
                documentIds.Contains(x.DocumentChunk.DocumentId) &&
                x.DocumentChunk.Document.Status == "Ready")
            .ToListAsync(ct);

        _logger.LogInformation(
            "[RETRIEVAL] MySQL candidate embeddings loaded. Count={Count}", embeddings.Count);

        var scored = new List<RetrievedDocumentChunk>(embeddings.Count);

        foreach (var emb in embeddings)
        {
            float[] vector;
            try { vector = JsonSerializer.Deserialize<float[]>(emb.VectorJson) ?? []; }
            catch { continue; }

            if (vector.Length != queryEmbedding.Length)
            {
                _logger.LogWarning(
                    "[RETRIEVAL] Dimension mismatch — skipping. ChunkId={ChunkId}, QueryDims={Q}, ChunkDims={C}",
                    emb.DocumentChunkId, queryEmbedding.Length, vector.Length);
                continue;
            }

            var score = _cosineSimilarity.Calculate(queryEmbedding, vector);
            var chunk = emb.DocumentChunk;

            // Boost score for chunks that contain an exact identifier match
            if (identifiers.Count > 0)
            {
                var hasIdentifier = identifiers.Any(id =>
                    chunk.Content.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                if (hasIdentifier)
                    score = Math.Max(score, KeywordMatchScore);
            }

            scored.Add(new RetrievedDocumentChunk
            {
                DocumentId    = chunk.DocumentId,
                ChunkId       = emb.DocumentChunkId,
                FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
                DocumentTitle = chunk.Document.Title,
                ChunkIndex    = chunk.ChunkIndex,
                PageNumber    = chunk.PageNumber,
                Heading       = chunk.Heading,
                Content       = chunk.Content,
                Score         = score
            });
        }

        return scored;
    }

    // ── Hybrid retrieval helpers ──────────────────────────────────────────────

    private static List<string> ExtractLookupIdentifiers(string query)
    {
        var matches = IdentifierRegex.Matches(query);
        return matches.Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<RetrievedDocumentChunk>> SearchByKeywordsAsync(
        List<Guid> documentIds,
        List<string> identifiers,
        CancellationToken ct)
    {
        // Load all chunks for these documents, then filter in memory.
        // EF Core cannot translate Contains(string) with case-insensitive collation portably,
        // so we load and filter — acceptable because document scope is already narrow.
        var chunks = await _db.DocumentChunks
            .AsNoTracking()
            .Include(x => x.Document)
                .ThenInclude(x => x.File)
            .Where(x => documentIds.Contains(x.DocumentId) && x.Document.Status == "Ready")
            .ToListAsync(ct);

        var result = new List<RetrievedDocumentChunk>();

        foreach (var chunk in chunks)
        {
            var matched = identifiers.Any(id =>
                chunk.Content.IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!matched) continue;

            result.Add(new RetrievedDocumentChunk
            {
                DocumentId    = chunk.DocumentId,
                ChunkId       = chunk.Id,
                FileName      = chunk.Document.File?.OriginalFileName ?? chunk.Document.Title,
                DocumentTitle = chunk.Document.Title,
                ChunkIndex    = chunk.ChunkIndex,
                PageNumber    = chunk.PageNumber,
                Heading       = chunk.Heading,
                Content       = chunk.Content,
                Score         = KeywordMatchScore
            });
        }

        if (result.Count > 0)
            _logger.LogInformation(
                "[RETRIEVAL] Keyword search found {Count} matching chunks for identifiers [{Ids}].",
                result.Count, string.Join(", ", identifiers));

        return result;
    }

    private static List<RetrievedDocumentChunk> MergeWithKeywordHits(
        List<RetrievedDocumentChunk> vectorResults,
        List<RetrievedDocumentChunk> keywordResults)
    {
        if (keywordResults.Count == 0)
            return vectorResults;

        var vectorById = vectorResults.ToDictionary(c => c.ChunkId);

        foreach (var kw in keywordResults)
        {
            if (vectorById.TryGetValue(kw.ChunkId, out var existing))
            {
                // Boost the vector score if keyword score is higher
                if (kw.Score > existing.Score)
                    existing.Score = kw.Score;
            }
            else
            {
                // Add keyword-only hits not returned by vector search
                vectorResults.Add(kw);
                vectorById[kw.ChunkId] = kw;
            }
        }

        return vectorResults;
    }

    // ── Selection strategies ──────────────────────────────────────────────────

    private static List<RetrievedDocumentChunk> SelectWithPageDiversity(
        List<RetrievedDocumentChunk> candidates,
        int top,
        int maxPerPage)
    {
        var result = new List<RetrievedDocumentChunk>(top);
        var pageHits = new Dictionary<(Guid docId, int? page), int>();

        foreach (var chunk in candidates)
        {
            if (result.Count >= top) break;

            var key = (chunk.DocumentId, chunk.PageNumber);
            pageHits.TryGetValue(key, out var count);

            if (count < maxPerPage)
            {
                result.Add(chunk);
                pageHits[key] = count + 1;
            }
        }

        // Fill remaining slots if page-diversity left gaps
        if (result.Count < top)
        {
            foreach (var chunk in candidates)
            {
                if (result.Count >= top) break;
                if (!result.Contains(chunk))
                    result.Add(chunk);
            }
        }

        return result;
    }

    private static List<RetrievedDocumentChunk> SelectDocumentWide(
        List<RetrievedDocumentChunk> aboveThreshold,
        List<RetrievedDocumentChunk> allScored,
        int top)
    {
        var half = top / 2;

        // Top-scoring half
        var topSimilarity = aboveThreshold.Take(half).ToList();

        // Evenly-distributed half sampled across the whole document (by ChunkIndex)
        var sorted = allScored.OrderBy(x => x.ChunkIndex).ToList();
        var distributed = new List<RetrievedDocumentChunk>(half);
        var step = Math.Max(1, sorted.Count / (half + 1));

        for (var i = step; i < sorted.Count && distributed.Count < half; i += step)
            distributed.Add(sorted[i]);

        // Merge: add distributed chunks not already in the similarity set
        var selectedIds = new HashSet<Guid>(topSimilarity.Select(c => c.ChunkId));
        var merged = new List<RetrievedDocumentChunk>(topSimilarity);

        foreach (var c in distributed)
        {
            if (merged.Count >= top) break;
            if (selectedIds.Add(c.ChunkId))
                merged.Add(c);
        }

        return merged.OrderByDescending(x => x.Score).ToList();
    }

    // ── Document-wide query detection ─────────────────────────────────────────

    private static readonly string[] DocumentWideKeywords =
    [
        "sammanfatta", "summera", "översikt", "vad handlar", "vad innehåller",
        "beskriv dokumentet", "hela dokumentet", "allt i ", "viktigaste kraven",
        "viktigaste krav", "lista alla", "lista samtliga", "alla krav", "samtliga krav",
        "nyckelkrav", "tekniska krav", "sla-krav", "säkerhetskrav",
        "summary", "summarize", "overview", "key requirements", "all requirements",
        "main points", "key points", "what does the document"
    ];

    private static bool IsDocumentWideQuery(string query)
    {
        var lower = query.ToLowerInvariant();
        return DocumentWideKeywords.Any(kw => lower.Contains(kw));
    }

    // ── Debug report ──────────────────────────────────────────────────────────

    public static string GenerateRetrievalDebugReport(
        string query,
        string embeddingProvider,
        string embeddingModel,
        List<RetrievedDocumentChunk> chunks,
        int promptCharacters)
    {
        var sb = new StringBuilder();

        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║               INFINITY AI — RETRIEVAL DEBUG REPORT           ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"Query          : {query}");
        sb.AppendLine($"Provider       : {embeddingProvider}");
        sb.AppendLine($"Model          : {embeddingModel}");
        sb.AppendLine($"Chunks returned: {chunks.Count}");
        sb.AppendLine($"Prompt chars   : {promptCharacters:N0}");
        sb.AppendLine($"Prompt tokens  : ~{promptCharacters / 4:N0}");
        sb.AppendLine();

        if (chunks.Count > 0)
        {
            var avgScore = chunks.Average(c => c.Score);
            var maxScore = chunks.Max(c => c.Score);
            var minScore = chunks.Min(c => c.Score);

            sb.AppendLine($"Scores         : avg={avgScore:F4}  max={maxScore:F4}  min={minScore:F4}");
            sb.AppendLine();

            var byFile = chunks.GroupBy(c => c.FileName).OrderByDescending(g => g.Count());

            sb.AppendLine("Source files:");
            foreach (var g in byFile)
                sb.AppendLine($"  {g.Key} — {g.Count()} chunk(s)");

            sb.AppendLine();
            sb.AppendLine("Chunks (ranked by score):");

            foreach (var (c, i) in chunks.OrderByDescending(c => c.Score).Select((c, i) => (c, i + 1)))
            {
                var page = c.PageNumber.HasValue ? $"p.{c.PageNumber}" : "p.?";
                var heading = string.IsNullOrWhiteSpace(c.Heading) ? "" : $" [{c.Heading}]";
                sb.AppendLine($"  [{i:D2}] {c.FileName} {page} chunk={c.ChunkIndex} score={c.Score:F4}{heading}");
                sb.AppendLine($"       {Preview(c.Content, 200)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("──────────────────────────────────────────────────────────────");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void LogCandidates(List<RetrievedDocumentChunk> chunks, string label)
    {
        if (chunks.Count == 0)
        {
            _logger.LogInformation("[RETRIEVAL] {Label}: (empty)", label);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"[RETRIEVAL] {label}:");

        foreach (var (c, i) in chunks.Select((c, i) => (c, i + 1)))
        {
            var page = c.PageNumber.HasValue ? $"Page={c.PageNumber}" : "Page=?";
            var heading = !string.IsNullOrWhiteSpace(c.Heading) ? $" Heading=\"{Preview(c.Heading, 60)}\"" : "";

            sb.AppendLine(
                $"  [{i:D2}] Document={c.FileName} {page} Chunk={c.ChunkIndex} Score={c.Score:F4}{heading}");
            sb.AppendLine(
                $"       ContentLen={c.Content.Length} Preview=\"{Preview(c.Content, 300)}\"");
        }

        _logger.LogInformation("{Report}", sb.ToString());
    }

    private static string Preview(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var clean = text.Replace("\n", " ").Replace("\r", "").Trim();
        return clean.Length <= max ? clean : clean[..max] + "…";
    }
}

public sealed class RetrievedDocumentChunk
{
    public Guid DocumentId { get; set; }
    public Guid ChunkId { get; set; }

    public string FileName { get; set; } = "";
    public string DocumentTitle { get; set; } = "";

    public int ChunkIndex { get; set; }
    public int? PageNumber { get; set; }
    public string? Heading { get; set; }

    public string Content { get; set; } = "";
    public double Score { get; set; }
}
