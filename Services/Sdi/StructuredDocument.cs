using InfinityAI.Api.Models.Rag;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Parsed representation of a document's structured data, passed to each extractor.
/// Contains the parsed tables and raw text but is independent of the query.
/// </summary>
public sealed class StructuredDocument
{
    public required List<ParsedTable> Tables { get; init; }
    public required string RawText { get; init; }
    public required DocumentStructureType StructureType { get; init; }

    /// <summary>Total data rows across all tables (excludes header rows).</summary>
    public int TotalRows => Tables.Sum(t => t.Rows.Count);
}
