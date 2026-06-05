namespace InfinityAI.Api.Models.Rag;

/// <summary>
/// Measures how much of the physical document was actually sent to the LLM.
/// Distinct from CoverageResult (which measures AI response vs. source items).
/// </summary>
public sealed class DocumentCoverageResult
{
    // ── Row-level coverage ────────────────────────────────────────────────────
    public int TotalRows { get; set; }
    public int RowsSentToModel { get; set; }

    // ── Character-level coverage ──────────────────────────────────────────────
    public int TotalDocumentChars { get; set; }
    public int CharactersSentToModel { get; set; }

    // ── Chunk-level coverage ──────────────────────────────────────────────────
    public int TotalChunks { get; set; }
    public int ChunksSent { get; set; }

    // ── Derived metrics ───────────────────────────────────────────────────────
    public double CoveragePercent { get; set; }
    public double ChunkCoveragePercent { get; set; }
    public bool IsTruncated { get; set; }

    public static DocumentCoverageResult Compute(
        int totalChunks, int chunksSent,
        int totalChars, int charsSent,
        int totalRows = 0, int rowsSent = 0)
    {
        var coveragePct = totalChunks > 0
            ? Math.Round(Math.Min(100.0, (double)chunksSent / totalChunks * 100.0), 1)
            : 100.0;

        var chunkCoveragePct = totalChunks > 0
            ? Math.Round(Math.Min(100.0, (double)chunksSent / totalChunks * 100.0), 1)
            : 100.0;

        return new DocumentCoverageResult
        {
            TotalRows            = totalRows,
            RowsSentToModel      = rowsSent,
            TotalDocumentChars   = totalChars,
            CharactersSentToModel = charsSent,
            TotalChunks          = totalChunks,
            ChunksSent           = chunksSent,
            CoveragePercent      = coveragePct,
            ChunkCoveragePercent = chunkCoveragePct,
            IsTruncated          = chunksSent < totalChunks
        };
    }
}
