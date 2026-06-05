namespace InfinityAI.Api.Models.Sdi;

/// <summary>
/// Result of a deterministic structured data calculation.
/// Produced by the SDI engine; the LLM receives this and is asked to explain it, not recalculate it.
/// Declared as a record to enable non-destructive mutation via <c>with</c> expressions in the engine.
/// </summary>
public sealed record StructuredAnswer
{
    public string Value { get; init; } = "";
    public double Confidence { get; init; }
    public string Method { get; init; } = "";
    public int RowsProcessed { get; init; }
    public string? ColumnUsed { get; init; }
    public string? SheetName { get; init; }
    public bool IsExact { get; init; }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>True when a named column was matched; false when a fallback extractor was used.</summary>
    public bool ColumnMatched { get; init; }

    /// <summary>Name of the fallback extractor used, or null when the column-based path ran.</summary>
    public string? FallbackExtractor { get; init; }

    /// <summary>Number of individual values extracted from the data (before distinct/aggregation).</summary>
    public int ValuesExtracted { get; init; }

    /// <summary>Number of distinct values found (for DistinctCount operations).</summary>
    public int DistinctValues { get; init; }

    /// <summary>Number of rows that were skipped because they did not yield a usable value.</summary>
    public int RejectedRows { get; init; }

    /// <summary>Comma-separated first 10 distinct values (sorted), for diagnostic logging.</summary>
    public string? FirstValues { get; init; }

    // ── Extractor metadata (set centrally by the engine after winner selection) ──

    /// <summary>Name of the extractor that produced this answer.</summary>
    public string? ExtractorName { get; init; }

    /// <summary>Version of the extractor that produced this answer.</summary>
    public string? ExtractorVersion { get; init; }

    /// <summary>Priority of the extractor that produced this answer.</summary>
    public int ExtractorPriority { get; init; }

    /// <summary>Number of extractors that produced confident candidates before selection.</summary>
    public int CandidateCount { get; init; }
}
