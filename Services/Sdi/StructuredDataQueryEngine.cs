using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Orchestrates deterministic structured-data calculations by delegating to
/// registered <see cref="IStructuredValueExtractor"/> implementations.
///
/// Selection strategy: BestConfidenceWins
///   1. Ask every extractor whose <see cref="IStructuredValueExtractor.CanHandle"/> returns true.
///   2. Collect all results with Confidence ≥ <see cref="MinimumConfidence"/>.
///   3. Pick the winner by: highest Confidence → highest Priority → highest ValuesExtracted.
///   4. Stamp extractor metadata (Name, Version, Priority, CandidateCount) onto the winner.
///   5. Return null when no extractor is confident — the caller falls through to RAG/LLM.
/// </summary>
public sealed class StructuredDataQueryEngine : IStructuredDataQueryEngine
{
    private const double MinimumConfidence = 95;
    private const string SelectionReason   = "ConfidenceThenPriority";

    private readonly IReadOnlyList<IStructuredValueExtractor> _extractors;
    private readonly ILogger<StructuredDataQueryEngine>       _logger;

    public StructuredDataQueryEngine(
        IEnumerable<IStructuredValueExtractor> extractors,
        ILogger<StructuredDataQueryEngine>? logger = null)
    {
        _extractors = extractors.ToList();
        _logger     = logger ?? NullLogger<StructuredDataQueryEngine>.Instance;
    }

    public Task<StructuredAnswer?> TryExecuteAsync(
        string extractedText,
        QueryIntent intent,
        string userQuery,
        DocumentStructureType structureType,
        CancellationToken ct = default)
    {
        try
        {
            return Task.FromResult(Execute(extractedText, intent, userQuery, structureType));
        }
        catch
        {
            return Task.FromResult<StructuredAnswer?>(null);
        }
    }

    // ── Orchestration ─────────────────────────────────────────────────────────

    private StructuredAnswer? Execute(
        string text,
        QueryIntent intent,
        string userQuery,
        DocumentStructureType structureType)
    {
        var tables = LoadTables(text, structureType);

        if (tables.Count == 0 || tables.All(t => t.Rows.Count == 0))
            return null;

        var doc = new StructuredDocument
        {
            Tables        = tables,
            RawText       = text,
            StructureType = structureType
        };

        var candidates = new List<(IStructuredValueExtractor Extractor, StructuredAnswer Answer)>();

        foreach (var extractor in _extractors)
        {
            if (!extractor.CanHandle(intent, doc, userQuery))
                continue;

            StructuredAnswer? answer;
            try
            {
                answer = extractor.Extract(intent, doc, userQuery);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "[SDI-CANDIDATE] Extractor={Name} v{Ver} threw — skipped. Error={Msg}",
                    extractor.Name, extractor.Version, ex.Message);
                continue;
            }

            if (answer is null)
            {
                _logger.LogInformation(
                    "[SDI-CANDIDATE] Extractor={Name} v{Ver} Priority={Pri} — returned null (no match)",
                    extractor.Name, extractor.Version, extractor.Priority);
                continue;
            }

            if (answer.Confidence < MinimumConfidence)
            {
                _logger.LogInformation(
                    "[SDI-CANDIDATE] Extractor={Name} v{Ver} Priority={Pri} Confidence={Conf}% — below {Min}%, rejected",
                    extractor.Name, extractor.Version, extractor.Priority,
                    answer.Confidence, MinimumConfidence);
                continue;
            }

            _logger.LogInformation(
                "[SDI-CANDIDATE] Extractor={Name} v{Ver} Priority={Pri} Confidence={Conf}% — accepted as candidate",
                extractor.Name, extractor.Version, extractor.Priority, answer.Confidence);

            candidates.Add((extractor, answer));
        }

        if (candidates.Count == 0)
            return null;

        // BestConfidenceWins: highest Confidence → highest Priority → highest ValuesExtracted
        var (winner, winnerAnswer) = candidates
            .OrderByDescending(c => c.Answer.Confidence)
            .ThenByDescending(c => c.Extractor.Priority)
            .ThenByDescending(c => c.Answer.ValuesExtracted)
            .First();

        _logger.LogInformation(
            "[SDI] Winner={Name} v{Ver} Priority={Pri} Confidence={Conf}% " +
            "CandidateCount={Count} SelectedBy={Reason}",
            winner.Name, winner.Version, winner.Priority,
            winnerAnswer.Confidence, candidates.Count, SelectionReason);

        // Stamp extractor metadata centrally — extractors do not set these themselves.
        return winnerAnswer with
        {
            ExtractorName     = winner.Name,
            ExtractorVersion  = winner.Version,
            ExtractorPriority = winner.Priority,
            CandidateCount    = candidates.Count
        };
    }

    // ── Table loading ─────────────────────────────────────────────────────────

    private static List<ParsedTable> LoadTables(string text, DocumentStructureType structureType)
    {
        var tables = MarkdownTableParser.Parse(text);
        if (tables.Count > 0) return tables;

        if (structureType == DocumentStructureType.Spreadsheet)
            return MarkdownTableParser.ParseCsv(text);

        return [];
    }
}
