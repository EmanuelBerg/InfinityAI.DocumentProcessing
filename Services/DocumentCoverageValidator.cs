using InfinityAI.Api.Models.Rag;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace InfinityAI.Api.Services;

public static class DocumentCoverageValidator
{
    private static readonly Regex NumberedListRegex =
        new(@"^\s*\d+[\.\)]\s+\S", RegexOptions.Compiled);

    // ── Source analysis (from chunks before sending to LLM) ──────────────────

    /// <summary>Counts Markdown table data rows across the retrieved chunks (excludes header/separator lines).</summary>
    public static int CountSourceRows(IEnumerable<RetrievedDocumentChunk> chunks)
    {
        var count = 0;

        foreach (var chunk in chunks)
        {
            foreach (var line in chunk.Content.Split('\n'))
            {
                var trimmed = line.Trim();

                if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
                    continue;

                // Skip Markdown separator lines (|---|---|)
                var inner = trimmed.Replace("|", "").Replace("-", "").Replace(":", "").Replace(" ", "");
                if (inner.Length == 0)
                    continue;

                count++;
            }
        }

        return count;
    }

    // ── Response analysis (from LLM answer) ──────────────────────────────────

    /// <summary>Estimates how many enumerable items appear in the AI response.</summary>
    public static int CountResponseItems(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return 0;

        return response.Split('\n').Count(line =>
        {
            var t = line.TrimStart();
            return t.StartsWith("- ") ||
                   t.StartsWith("* ") ||
                   t.StartsWith("• ") ||
                   (t.StartsWith("|") && t.TrimEnd().EndsWith("|")) ||
                   NumberedListRegex.IsMatch(t);
        });
    }

    // ── Coverage computation ──────────────────────────────────────────────────

    public static CoverageResult Compute(int sourceItems, int returnedItems)
    {
        if (sourceItems <= 0)
            return new CoverageResult { Passed = true, Status = "N/A" };

        var percent = Math.Round(Math.Min(100.0, (double)returnedItems / sourceItems * 100.0), 1);

        return new CoverageResult
        {
            SourceItems     = sourceItems,
            ReturnedItems   = returnedItems,
            CoveragePercent = percent,
            Passed          = percent >= 95.0,
            Status = percent >= 100.0 ? "PASS" : percent >= 95.0 ? "WARNING" : "FAILED"
        };
    }

    // ── Logging ───────────────────────────────────────────────────────────────

    public static void Log(ILogger logger, string documentName, CoverageResult result)
    {
        if (result.Status == "N/A")
            return;

        var level = result.Status switch
        {
            "PASS"    => LogLevel.Information,
            "WARNING" => LogLevel.Warning,
            _         => LogLevel.Error
        };

        logger.Log(
            level,
            "[COVERAGE] Document={Document} SourceRows={Source} ReturnedRows={Returned} Coverage={Pct}% Status={Status}",
            documentName, result.SourceItems, result.ReturnedItems, result.CoveragePercent, result.Status);
    }

    public static void LogSession(ILogger logger, RagSessionMetrics metrics)
    {
        logger.LogInformation(
            "[RAG-TELEMETRY] " +
            "Intent={Intent} Strategy={Strategy} " +
            "TotalDocumentChars={DocChars} CharactersSentToModel={SentChars} DocumentCoverage={DocPct}% " +
            "TotalChunks={TotalChunks} ChunksSent={ChunksSent} ChunkCoverage={ChunkPct}% " +
            "Truncated={Truncated} RecoveryMode={Recovery} Passes={Passes} " +
            "ContextChars={CtxChars} PromptChars={PromptChars} ResponseChars={RespChars}",
            metrics.QueryIntent,
            metrics.RetrievalStrategy,
            metrics.TotalDocumentChars,
            metrics.CharactersSentToModel,
            metrics.DocumentCoveragePercent.ToString("F1"),
            metrics.TotalDocumentChunks,
            metrics.RetrievedChunks,
            metrics.CoveragePercent.ToString("F1"),
            metrics.IsTruncated,
            metrics.RecoveryModeActivated,
            metrics.NumberOfPasses,
            metrics.ContextCharacters,
            metrics.UserQueryCharacters,
            metrics.ResponseCharacters);
    }

    public static void LogEnumeration(ILogger logger, RagSessionMetrics metrics)
    {
        logger.LogInformation(
            "[RAG-ENUMERATION] Activated=True Version={Version} Intent={Intent} " +
            "PromptAugmented={Augmented} CandidateModels={Models} CandidateProducts={Products}",
            InfinityAI.Api.Models.Rag.EnumerationVersion.Version,
            metrics.QueryIntent,
            metrics.PromptAugmented,
            metrics.CandidateModels,
            metrics.CandidateProducts);

        if (metrics.TopCandidatesForLog.Length > 0)
        {
            logger.LogInformation(
                "[RAG-ENUMERATION] TopCandidates={Candidates}",
                string.Join(", ", metrics.TopCandidatesForLog));
        }
    }

    public static void LogEnumerationNotActivated(ILogger logger, QueryIntent intent)
    {
        logger.LogInformation(
            "[RAG-ENUMERATION] Activated=False Reason=IntentNotMatched Intent={Intent}",
            intent);
    }

    public static void LogSdi(ILogger logger, RagSessionMetrics metrics)
    {
        if (!metrics.SdiActivated)
            return;

        logger.LogInformation(
            "[SDI] Intent={Intent} DocumentStructure={Structure} Method={Method} " +
            "Result={Result} RowsProcessed={Rows} Confidence={Confidence}% Column={Column} " +
            "ColumnMatched={ColumnMatched} FallbackExtractor={FallbackExtractor} " +
            "ValuesExtracted={ValuesExtracted} DistinctValues={DistinctValues} " +
            "RejectedRows={RejectedRows} FirstValues={FirstValues} " +
            "ExtractorName={ExtractorName} ExtractorVersion={ExtractorVersion} " +
            "ExtractorPriority={ExtractorPriority} CandidateCount={CandidateCount} " +
            "SelectedBy={SelectedBy}",
            metrics.QueryIntent,
            metrics.DocumentStructureType,
            metrics.SdiMethod,
            metrics.SdiResult,
            metrics.SdiRowsProcessed,
            metrics.SdiConfidence.ToString("F0"),
            metrics.SdiColumnUsed ?? "N/A",
            metrics.SdiColumnMatched,
            metrics.SdiFallbackExtractor ?? "None",
            metrics.SdiValuesExtracted,
            metrics.SdiDistinctValues,
            metrics.SdiRejectedRows,
            metrics.SdiFirstValues ?? "",
            metrics.SdiExtractorName ?? "N/A",
            metrics.SdiExtractorVersion ?? "N/A",
            metrics.SdiExtractorPriority,
            metrics.SdiCandidateCount,
            metrics.SdiSelectedBy);
    }
}
