using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Pluggable extractor that answers one class of structured-data queries deterministically.
/// Implementations are discovered automatically via DI — no switch statements in the engine.
/// </summary>
public interface IStructuredValueExtractor
{
    /// <summary>Descriptive name used in telemetry logs.</summary>
    string Name { get; }

    /// <summary>Semantic version string (e.g. "1.0") included in telemetry for traceability.</summary>
    string Version { get; }

    /// <summary>
    /// Selection priority when multiple extractors produce confident results for the same query.
    /// Higher wins. Suggested: 1000 = highly specific domain extractor, 100 = generic fallback.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Returns true when this extractor is capable of answering the given intent
    /// against the given document.  Must be fast and side-effect-free.
    /// </summary>
    bool CanHandle(QueryIntent intent, StructuredDocument document, string userQuery);

    /// <summary>
    /// Executes the extraction. Returns a <see cref="StructuredAnswer"/> with
    /// <see cref="StructuredAnswer.Confidence"/> ≥ 95 when the result is trustworthy,
    /// or null when the extractor cannot produce a confident answer.
    /// Must never throw.
    /// </summary>
    StructuredAnswer? Extract(QueryIntent intent, StructuredDocument document, string userQuery);
}
