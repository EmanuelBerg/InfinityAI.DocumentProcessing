using InfinityAI.Api.Services;

namespace InfinityAI.Api.Models.Rag;

/// <summary>
/// Accumulated telemetry for one RAG retrieval session.
/// Populated in BuildAiPromptWithFileContextAsync and completed by the caller after the LLM responds.
/// </summary>
public sealed class RagSessionMetrics
{
    public QueryIntent QueryIntent { get; set; }
    public RetrievalStrategy RetrievalStrategy { get; set; }

    public int DocumentCount { get; set; }
    public int RetrievedChunks { get; set; }

    /// <summary>Total chunks in the document(s). 0 for SmallDocumentDirectContext.</summary>
    public int TotalDocumentChunks { get; set; }

    /// <summary>Row count from source document (table rows in chunks).</summary>
    public int SourceItems { get; set; }

    // ── Character-level coverage ──────────────────────────────────────────────
    public int TotalDocumentChars { get; set; }
    public int CharactersSentToModel { get; set; }

    // ── Derived coverage ──────────────────────────────────────────────────────
    /// <summary>Chunk coverage % (RetrievedChunks / TotalDocumentChunks).</summary>
    public double CoveragePercent { get; set; }

    /// <summary>Character coverage % (CharactersSentToModel / TotalDocumentChars).</summary>
    public double DocumentCoveragePercent { get; set; }

    /// <summary>True when chunks were truncated due to context limit.</summary>
    public bool IsTruncated { get; set; }

    // ── Recovery ──────────────────────────────────────────────────────────────
    public bool RecoveryModeActivated { get; set; }
    public int NumberOfPasses { get; set; }

    // ── Size metrics ──────────────────────────────────────────────────────────
    public int ContextCharacters { get; set; }
    public int UserQueryCharacters { get; set; }
    public int AssembledPromptCharacters { get; set; }
    public int ResponseCharacters { get; set; }

    // ── Enumeration mode ──────────────────────────────────────────────────────
    public int CandidateModels { get; set; }
    public int CandidateProducts { get; set; }
    public bool PromptAugmented { get; set; }

    // ── Structured Data Intelligence (SDI) ───────────────────────────────────
    public bool SdiActivated { get; set; }
    public DocumentStructureType DocumentStructureType { get; set; }
    public string? SdiMethod { get; set; }
    public string? SdiResult { get; set; }
    public int SdiRowsProcessed { get; set; }
    public double SdiConfidence { get; set; }
    public string? SdiColumnUsed { get; set; }

    // SDI diagnostics — mirrors StructuredAnswer diagnostic fields
    public bool SdiColumnMatched { get; set; }
    public string? SdiFallbackExtractor { get; set; }
    public int SdiValuesExtracted { get; set; }
    public int SdiDistinctValues { get; set; }
    public int SdiRejectedRows { get; set; }
    public string? SdiFirstValues { get; set; }

    // SDI extractor metadata — set by engine winner selection
    public string? SdiExtractorName { get; set; }
    public string? SdiExtractorVersion { get; set; }
    public int SdiExtractorPriority { get; set; }
    public int SdiCandidateCount { get; set; }
    public string SdiSelectedBy { get; set; } = "";

    // ── Retrieval quality scores ───────────────────────────────────────────────
    // Populated by EndpointHelpers from the scored chunk list after retrieval.
    // Null for strategies that don't produce similarity scores (SDI, SmallDocumentDirectContext).
    public double? TopScore { get; set; }
    public double? AverageScore { get; set; }
    public double? LowestScore { get; set; }

    // ── Retrieval timing ──────────────────────────────────────────────────────
    /// <summary>Milliseconds spent in the vector/keyword retrieval call (not prompt assembly).</summary>
    public long RetrievalDurationMs { get; set; }

    // ── Empty retrieval flag ──────────────────────────────────────────────────
    /// <summary>True when SemanticRetrieval returned zero chunks for the query.</summary>
    public bool EmptyRetrieval { get; set; }

    // Transient recovery data — set during RAG and consumed by the calling endpoint.
    public List<RetrievedDocumentChunk>? AllChunksForRecovery { get; set; }
    public string[] TopCandidatesForLog { get; set; } = [];
}
