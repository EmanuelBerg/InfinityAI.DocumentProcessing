using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi;

public interface IStructuredDataQueryEngine
{
    /// <summary>
    /// Attempts to answer the query deterministically by parsing structured data in the
    /// extracted document text.
    /// Returns null when the engine cannot handle the query — the caller must fall through
    /// to the normal LLM path.
    /// Never throws; any internal failure produces null.
    /// </summary>
    Task<StructuredAnswer?> TryExecuteAsync(
        string extractedText,
        QueryIntent intent,
        string userQuery,
        DocumentStructureType structureType,
        CancellationToken ct = default);
}
