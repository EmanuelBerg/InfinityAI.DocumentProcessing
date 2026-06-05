namespace InfinityAI.Api.Models.Rag;

public sealed class RagOptions
{
    /// <summary>Number of chunks returned to the LLM by default.</summary>
    public int TopK { get; set; } = 15;

    /// <summary>Hard cap on total characters injected as document context into the prompt.</summary>
    public int MaxContextCharacters { get; set; } = 16000;

    /// <summary>Maximum characters taken from a single chunk.</summary>
    public int MaxChunkCharacters { get; set; } = 2400;

    /// <summary>Cosine similarity threshold below which chunks are discarded (0–1).</summary>
    public double MinRelevanceScore { get; set; } = 0.45;

    /// <summary>Maximum chunks allowed from the same page before diversity kicks in.</summary>
    public int MaxChunksPerPage { get; set; } = 3;

    /// <summary>When true, broad queries trigger distributed-sampling instead of pure similarity search.</summary>
    public bool EnableDocumentWideQueryDetection { get; set; } = true;

    /// <summary>TopK used for document-wide queries (summary, "list all requirements", etc.).</summary>
    public int DocumentWideQueryTopK { get; set; } = 30;

    /// <summary>
    /// Documents whose ExtractedText is shorter than this (in characters) are sent as full direct
    /// context to the LLM instead of going through chunk retrieval. 0 = disabled.
    /// Modern models handle 50 000 chars comfortably; most product sheets / specs fit within this.
    /// </summary>
    public int SmallDocumentThresholdCharacters { get; set; } = 50000;

    /// <summary>
    /// Hard cap on total characters injected when using FullDocumentRetrieval (ListAll / TableEnumeration).
    /// Higher than MaxContextCharacters to allow complete enumeration of large tables.
    /// </summary>
    public int FullDocumentMaxContextCharacters { get; set; } = 48000;

    // ── Recovery options ──────────────────────────────────────────────────────

    /// <summary>
    /// Characters per extraction pass in multi-pass recovery mode.
    /// 0 = same as FullDocumentMaxContextCharacters.
    /// </summary>
    public int PassMaxContextCharacters { get; set; } = 0;

    /// <summary>Maximum recovery passes to prevent runaway LLM costs.</summary>
    public int MaxRecoveryPasses { get; set; } = 5;

    /// <summary>
    /// When true, runs a verification LLM call after recovery to check for missed items.
    /// Only for ListAll / TableEnumeration. Adds one extra LLM call.
    /// </summary>
    public bool EnableVerificationPass { get; set; } = false;

    // ── SDI options ───────────────────────────────────────────────────────────

    /// <summary>
    /// When true, the SDI explanation prompt sent to the LLM includes internal technical details
    /// (extractor name, method, confidence, row counts) — useful during development and testing.
    /// Default false: the LLM receives a human-friendly instruction with no internal diagnostics.
    /// </summary>
    public bool IncludeStructuredDebugInPrompt { get; set; } = false;
}
