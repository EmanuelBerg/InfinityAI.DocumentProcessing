namespace InfinityAI.Api.Models.Rag;

/// <summary>
/// Semantic version of the RAG retrieval pipeline.
/// Increment when retrieval strategy, intent classification, or recovery logic changes.
/// History:
///   1.0 – Original RAG (semantic retrieval only)
///   2.0 – QueryIntent + FullDocumentRetrieval
///   2.5 – Coverage metrics + multi-pass recovery
///   3.0 – Enumeration Mode (extraction-first prompt, pattern analysis)
/// </summary>
public static class RagPipelineVersion
{
    public const string Version = "3.0";
}

/// <summary>
/// Semantic version of the Enumeration extraction subsystem.
/// Increment when enumeration prompt, pattern regex, or context strategy changes.
/// </summary>
public static class EnumerationVersion
{
    public const string Version = "1.1";
}
