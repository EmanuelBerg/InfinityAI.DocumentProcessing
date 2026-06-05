using InfinityAI.Data;
using InfinityAI.Pipeline.Contracts;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;
using InfinityAI.Api.Services;
using InfinityAI.Api.Services.Sdi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using RetrievalStrategy = InfinityAI.Api.Models.Rag.RetrievalStrategy;

namespace InfinityAI.Api.Helpers;

public static class EndpointHelpers
{
    public static string TruncatePrompt(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public static string BuildTriggeredFilters<TFinding>(IEnumerable<TFinding>? findings)
    {
        if (findings is null)
            return "";

        return string.Join(",",
            findings
                .Select(ExtractFindingName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct());
    }

    private static string ExtractFindingName<TFinding>(TFinding finding)
    {
        if (finding is null)
            return "";

        var type = finding.GetType();

        foreach (var propertyName in new[]
        {
            "FilterName", "ProcessorName", "Processor",
            "Category", "Type", "Code", "Name"
        })
        {
            var property = type.GetProperty(propertyName);
            var value = property?.GetValue(finding)?.ToString();

            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return type.Name;
    }

    // ── RAG prompt assembly ───────────────────────────────────────────────────

    public static async Task<string> BuildAiPromptWithFileContextAsync(
        ApplicationDbContext db,
        IDocumentRetrievalService documentRetrievalService,
        Guid userId,
        string userPrompt,
        List<Guid> fileIds,
        RagOptions? options = null,
        CancellationToken ct = default,
        ILogger? logger = null,
        RagSessionMetrics? metrics = null,
        IStructuredDataQueryEngine? sdiEngine = null)
    {
        if (fileIds.Count == 0)
            return userPrompt;

        var threshold = options?.SmallDocumentThresholdCharacters ?? 50000;

        // Classify intent once — used by all strategy branches
        var intent = QueryIntentClassifier.Classify(userPrompt);

        // Resolve fileIds → StoredFileIds once — shared by all strategy branches
        var storedFileIds = await db.Files
            .AsNoTracking()
            .Where(x => fileIds.Contains(x.Id) && x.StoredFileId.HasValue)
            .Select(x => x.StoredFileId!.Value)
            .Distinct()
            .ToListAsync(ct);

        // ── -1. Processing-status guard ───────────────────────────────────────────
        // If any attached documents are still being ingested, inform the LLM so it
        // can tell the user to wait — rather than silently omitting those documents.
        var processingDocs = await db.Documents
            .AsNoTracking()
            .Where(d =>
                (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                (d.FileId.HasValue      && fileIds.Contains(d.FileId.Value)))
            .Where(d => d.Status != "Ready" && d.Status != "Blocked" && d.Status != "Failed")
            .Select(d => new
            {
                d.Title, d.Status, d.ProcessingStage,
                d.ProgressCurrent, d.ProgressTotal
            })
            .ToListAsync(ct);

        if (processingDocs.Count > 0)
        {
            var lines = processingDocs.Select(d =>
            {
                var stageDetail = d.ProcessingStage ?? d.Status;
                var progress    = d.ProgressCurrent.HasValue && d.ProgressTotal.HasValue
                    ? $" {d.ProgressCurrent}/{d.ProgressTotal}"
                    : "";
                return $"- {d.Title}: {stageDetail}{progress}";
            });

            var statusBlock =
                "[SYSTEM: The following documents are still being processed and cannot be read yet:\n" +
                string.Join("\n", lines) + "\n" +
                "Inform the user clearly which documents are still processing. " +
                "Tell them to wait a few minutes. Do not hallucinate content from those files.]";

            // If ALL attached files are still processing, short-circuit entirely.
            var readyCount = await db.Documents
                .AsNoTracking()
                .Where(d =>
                    (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                    (d.FileId.HasValue      && fileIds.Contains(d.FileId.Value)))
                .CountAsync(d => d.Status == "Ready", ct);

            if (readyCount == 0)
                return userPrompt + "\n\n" + statusBlock;

            // Some files are ready, some are not — continue RAG for ready files
            // but append the status notice so the LLM mentions the pending ones.
            userPrompt = userPrompt + "\n\n" + statusBlock;
        }

        // ── 0. Structured Data Intelligence — deterministic calculations ─────────
        // Runs on spreadsheet documents (Excel/CSV) when the query requests a
        // calculation (Count, DistinctCount, Sum, Average, Max, Min, Aggregation).
        // Works on the full ExtractedText regardless of document size.
        // Falls through (returns null) if no tables are found or the engine
        // cannot satisfy the query — the normal RAG path handles it from there.
        if (sdiEngine is not null && QueryIntentClassifier.IsStructuredDataIntent(intent))
        {
            var sdiDocs = await db.Documents
                .AsNoTracking()
                .Where(d =>
                    (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                    (d.FileId.HasValue && fileIds.Contains(d.FileId.Value)))
                .Where(d => d.Status == "Ready" && d.ExtractedText != null &&
                            (d.DocumentType == "Excel" || d.DocumentType == "Csv"))
                .Select(d => new { d.Title, d.DocumentType, d.ExtractedText })
                .ToListAsync(ct);

            if (sdiDocs.Count > 0)
            {
                var fullText  = string.Join("\n\n", sdiDocs.Select(d => d.ExtractedText ?? ""));
                var structure = DocumentStructureType.Spreadsheet;

                var sdiAnswer = await sdiEngine.TryExecuteAsync(
                    fullText, intent, userPrompt, structure, ct);

                if (sdiAnswer is not null)
                {
                    if (metrics is not null)
                    {
                        metrics.RetrievalStrategy     = RetrievalStrategy.StructuredDataEngine;
                        metrics.QueryIntent           = intent;
                        metrics.DocumentCount         = sdiDocs.Count;
                        metrics.DocumentStructureType = structure;
                        metrics.SdiActivated          = true;
                        metrics.SdiMethod             = sdiAnswer.Method;
                        metrics.SdiResult             = sdiAnswer.Value;
                        metrics.SdiRowsProcessed      = sdiAnswer.RowsProcessed;
                        metrics.SdiConfidence         = sdiAnswer.Confidence;
                        metrics.SdiColumnUsed         = sdiAnswer.ColumnUsed;
                        metrics.SdiColumnMatched      = sdiAnswer.ColumnMatched;
                        metrics.SdiFallbackExtractor  = sdiAnswer.FallbackExtractor;
                        metrics.SdiValuesExtracted    = sdiAnswer.ValuesExtracted;
                        metrics.SdiDistinctValues     = sdiAnswer.DistinctValues;
                        metrics.SdiRejectedRows       = sdiAnswer.RejectedRows;
                        metrics.SdiFirstValues        = sdiAnswer.FirstValues;
                        metrics.SdiExtractorName      = sdiAnswer.ExtractorName;
                        metrics.SdiExtractorVersion   = sdiAnswer.ExtractorVersion;
                        metrics.SdiExtractorPriority  = sdiAnswer.ExtractorPriority;
                        metrics.SdiCandidateCount     = sdiAnswer.CandidateCount;
                        metrics.SdiSelectedBy         = "ConfidenceThenPriority";
                    }

                    logger?.LogInformation(
                        "[SDI] Activated. Intent={Intent} Extractor={Extractor} v{Ver} " +
                        "Priority={Pri} CandidateCount={Candidates} SelectedBy={SelectedBy} " +
                        "Method={Method} Result={Result} Confidence={Confidence}% " +
                        "RowsProcessed={Rows} ColumnMatched={ColMatched} " +
                        "FallbackExtractor={Fallback} ValuesExtracted={Extracted} " +
                        "DistinctValues={Distinct} RejectedRows={Rejected} FirstValues={First}",
                        intent,
                        sdiAnswer.ExtractorName ?? "unknown",
                        sdiAnswer.ExtractorVersion ?? "?",
                        sdiAnswer.ExtractorPriority,
                        sdiAnswer.CandidateCount,
                        "ConfidenceThenPriority",
                        sdiAnswer.Method, sdiAnswer.Value,
                        sdiAnswer.Confidence,
                        sdiAnswer.RowsProcessed,
                        sdiAnswer.ColumnMatched,
                        sdiAnswer.FallbackExtractor ?? "None",
                        sdiAnswer.ValuesExtracted,
                        sdiAnswer.DistinctValues,
                        sdiAnswer.RejectedRows,
                        sdiAnswer.FirstValues ?? "");

                    var sdiDocList = sdiDocs
                        .Select(d => (d.Title ?? "", d.DocumentType ?? "", d.ExtractedText ?? ""))
                        .ToList();

                    return SdiPromptBuilder.Build(userPrompt, sdiDocList, sdiAnswer, options);
                }

                logger?.LogInformation(
                    "[SDI] Engine returned null for Intent={Intent} — falling through to RAG.",
                    intent);
            }
        }

        // ── 1. Small-document direct context ─────────────────────────────────────
        // Send the full extracted text when documents fit within the threshold.
        // Avoids chunk splitting that destroys row/table relationships.
        if (threshold > 0)
        {
            var docInfos = await db.Documents
                .AsNoTracking()
                .Where(d =>
                    (d.StoredFileId.HasValue && storedFileIds.Contains(d.StoredFileId.Value)) ||
                    (d.FileId.HasValue && fileIds.Contains(d.FileId.Value)))
                .Where(d => d.Status == "Ready" && d.ExtractedText != null)
                .Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.DocumentType,
                    TextLength = d.ExtractedText == null ? 0 : d.ExtractedText.Length
                })
                .ToListAsync(ct);

            if (docInfos.Count > 0 && docInfos.All(d => d.TextLength > 0 && d.TextLength <= threshold))
            {
                var docIds = docInfos.Select(d => d.Id).ToList();
                var fullTexts = await db.Documents
                    .AsNoTracking()
                    .Where(d => docIds.Contains(d.Id))
                    .Select(d => new { d.Id, d.ExtractedText })
                    .ToListAsync(ct);

                var textMap = fullTexts.ToDictionary(d => d.Id, d => d.ExtractedText ?? "");
                var totalChars = docInfos.Sum(d => d.TextLength);

                if (metrics is not null)
                {
                    metrics.RetrievalStrategy = RetrievalStrategy.SmallDocumentDirectContext;
                    metrics.QueryIntent = intent;
                    metrics.DocumentCount = docInfos.Count;
                    metrics.RetrievedChunks = 0;
                    metrics.TotalDocumentChunks = 0;
                    metrics.CoveragePercent = 100.0;
                    metrics.ContextCharacters = totalChars;
                }

                logger?.LogInformation(
                    "[RAG] Strategy=SmallDocumentDirectContext. Docs={Count}, TotalChars={Chars}, Intent={Intent}",
                    docInfos.Count, totalChars, intent);

                var docs = docInfos
                    .Select(d => (d.Title, d.DocumentType, textMap.GetValueOrDefault(d.Id, "")))
                    .ToList();

                if (QueryIntentClassifier.IsEnumerationIntent(intent))
                {
                    var fullText = string.Join("\n\n", docs.Select(d => d.Item3));
                    var patterns = DocumentPatternAnalyzer.Analyze(fullText);

                    if (metrics is not null)
                    {
                        metrics.CandidateModels = patterns.CandidateModels.Count;
                        metrics.CandidateProducts = patterns.CandidateProducts.Count;
                        metrics.PromptAugmented = patterns.CandidateModels.Count > 0 || patterns.CandidateProducts.Count > 0;
                        metrics.TopCandidatesForLog = patterns.CandidateModels.Take(20).ToArray();
                    }

                    logger?.LogInformation(
                        "[RAG] EnumerationMode activated. CandidateModels={Models}",
                        patterns.CandidateModels.Count);

                    return BuildEnumerationDirectContextPrompt(userPrompt, docs, intent, patterns, options);
                }

                return BuildDirectDocumentContextPrompt(userPrompt, docs, options);
            }
        }

        // ── 2. Select retrieval strategy ─────────────────────────────────────────
        var strategy = QueryIntentClassifier.RequiresFullDocumentRetrieval(intent)
            ? RetrievalStrategy.FullDocumentRetrieval
            : RetrievalStrategy.SemanticRetrieval;

        logger?.LogInformation(
            "[RAG] Strategy={Strategy}, Intent={Intent}, FileIds={Count}",
            strategy, intent, fileIds.Count);

        // ── 3. FullDocumentRetrieval — all chunks in ChunkIndex order ────────────
        if (strategy == RetrievalStrategy.FullDocumentRetrieval)
        {
            var retrievalSw = Stopwatch.StartNew();
            var allChunks = await documentRetrievalService.RetrieveAllChunksAsync(userId, fileIds, ct);
            if (metrics is not null)
                metrics.RetrievalDurationMs = retrievalSw.ElapsedMilliseconds;

            if (allChunks.Count == 0)
                return userPrompt + "\n\n\nBifogade filer finns, men inga dokumentchunks hittades.\n";

            var maxFullDoc = options?.FullDocumentMaxContextCharacters ?? 48000;
            var context = BuildOrderedDocumentContext(allChunks, maxFullDoc, out var chunksSent);

            var totalDocChars = allChunks.Sum(c => c.Content.Length);
            var charsSent     = Math.Min(context.Length, totalDocChars);
            var sourceRows    = DocumentCoverageValidator.CountSourceRows(allChunks.Take(chunksSent));
            var isTruncated   = chunksSent < allChunks.Count;

            var chunkCovPct = allChunks.Count > 0
                ? Math.Round((double)chunksSent / allChunks.Count * 100.0, 1)
                : 100.0;
            var charCovPct = totalDocChars > 0
                ? Math.Round((double)charsSent / totalDocChars * 100.0, 1)
                : 100.0;

            // Pattern analysis for enumeration intents
            PatternAnalysisResult? patterns = null;
            if (QueryIntentClassifier.IsEnumerationIntent(intent))
            {
                var chunkText = string.Join("\n", allChunks.Select(c => c.Content));
                patterns = DocumentPatternAnalyzer.Analyze(chunkText);
            }

            if (metrics is not null)
            {
                metrics.RetrievalStrategy       = RetrievalStrategy.FullDocumentRetrieval;
                metrics.QueryIntent             = intent;
                metrics.DocumentCount           = allChunks.Select(c => c.DocumentId).Distinct().Count();
                metrics.RetrievedChunks         = chunksSent;
                metrics.TotalDocumentChunks     = allChunks.Count;
                metrics.SourceItems             = sourceRows;
                metrics.CoveragePercent         = chunkCovPct;
                metrics.TotalDocumentChars      = totalDocChars;
                metrics.CharactersSentToModel   = charsSent;
                metrics.DocumentCoveragePercent = charCovPct;
                metrics.IsTruncated             = isTruncated;
                metrics.ContextCharacters       = context.Length;

                if (patterns is not null)
                {
                    metrics.CandidateModels = patterns.CandidateModels.Count;
                    metrics.CandidateProducts = patterns.CandidateProducts.Count;
                    metrics.PromptAugmented = patterns.CandidateModels.Count > 0 || patterns.CandidateProducts.Count > 0;
                    metrics.TopCandidatesForLog = patterns.CandidateModels.Take(20).ToArray();
                }

                // Pass full chunk list so ChatEndpoints can activate recovery if needed
                if (isTruncated)
                    metrics.AllChunksForRecovery = allChunks;
            }

            if (isTruncated)
            {
                logger?.LogWarning(
                    "[RAG] FullDocumentRetrieval truncated. " +
                    "DocumentChars={DocChars} ContextChars={CtxChars} Truncated=True " +
                    "ChunkCoverage={ChunkPct}% CharCoverage={CharPct}%",
                    totalDocChars, charsSent, chunkCovPct, charCovPct);
            }

            var fullInstruction = QueryIntentClassifier.IsEnumerationIntent(intent)
                ? BuildEnumerationInstruction(intent, patterns)
                : """


Du ombeds lista samtliga poster i dokumentet. Nedan finns hela dokumentet i dokumentordning.
Returnera samtliga poster utan undantag. Hoppa inte över några.

Dokumentinnehåll (komplett, i dokumentordning):
""";

            return userPrompt + fullInstruction + "\n" + context;
        }

        // ── 4. SemanticRetrieval — existing similarity-based path ────────────────
        var semanticSw = Stopwatch.StartNew();
        var relevantChunks = await documentRetrievalService.RetrieveRelevantChunksAsync(
            userId, fileIds, userPrompt, top: 0, ct);

        if (metrics is not null)
            metrics.RetrievalDurationMs = semanticSw.ElapsedMilliseconds;

        if (relevantChunks.Count == 0)
        {
            if (metrics is not null)
            {
                metrics.EmptyRetrieval     = true;
                metrics.RetrievalStrategy  = RetrievalStrategy.SemanticRetrieval;
                metrics.QueryIntent        = intent;
            }
            return userPrompt + "\n\n\nBifogade filer finns, men ingen relevant dokumentkontext kunde hämtas.\n";
        }

        var isDocumentWide = IsDocumentWideQuery(userPrompt);
        var ragContext = BuildRetrievedDocumentContext(relevantChunks, options);

        if (metrics is not null)
        {
            metrics.RetrievalStrategy = RetrievalStrategy.SemanticRetrieval;
            metrics.QueryIntent = intent;
            metrics.DocumentCount = relevantChunks.Select(c => c.DocumentId).Distinct().Count();
            metrics.RetrievedChunks = relevantChunks.Count;
            metrics.TotalDocumentChunks = 0;
            metrics.ContextCharacters = ragContext.Length;
            metrics.TopScore     = relevantChunks.Max(c => c.Score);
            metrics.AverageScore = relevantChunks.Average(c => c.Score);
            metrics.LowestScore  = relevantChunks.Min(c => c.Score);
        }

        var hasTableContent = relevantChunks.Any(c =>
            c.Content.Contains("| ") && c.Content.Contains(" |"));

        string instruction;

        if (hasTableContent)
        {
            instruction = isDocumentWide
                ? """


Du svarar på en fråga om ett dokument med tabelldata. Nedan finns utdrag från tabellen.
Läs varje rad noggrant och sammanställ ett komplett svar baserat på alla utdrag.

Dokumentutdrag:
"""
                : """


Dokumentet innehåller tabelldata. Använd utdragen nedan och läs varje rad noggrant.
Om svaret saknas i utdragen, säg det.

Dokumentutdrag:
""";
        }
        else
        {
            instruction = isDocumentWide
                ? """


Du svarar på en fråga om ett helt dokument. Nedan finns utdrag från olika delar av dokumentet.
Använd alla utdragen för att ge ett heltäckande svar. Om du saknar information från ett visst avsnitt, notera det.

Dokumentutdrag:
"""
                : """


Använd endast relevanta dokumentutdrag nedan. Om svaret saknas i utdragen, säg det.

Dokumentutdrag:
""";
        }

        return userPrompt + instruction + "\n" + ragContext;
    }

    /// <summary>
    /// Formats chunks in ChunkIndex order without score-based sorting or filtering.
    /// Used for FullDocumentRetrieval where completeness matters more than relevance ranking.
    /// </summary>
    public static string BuildOrderedDocumentContext(
        IReadOnlyList<RetrievedDocumentChunk> chunks,
        int maxTotalChars,
        out int chunksSent)
    {
        var sb = new StringBuilder(Math.Min(maxTotalChars, chunks.Count * 500));
        chunksSent = 0;
        var remaining = maxTotalChars;
        string? currentDoc = null;

        foreach (var chunk in chunks)
        {
            if (remaining <= 0)
                break;

            if (currentDoc != chunk.DocumentTitle)
            {
                currentDoc = chunk.DocumentTitle;
                var header = $"--- Dokument: {chunk.DocumentTitle} (Fil: {chunk.FileName}) ---\n";

                if (header.Length > remaining)
                    break;

                sb.Append(header);
                remaining -= header.Length;
            }

            var content = chunk.Content.Trim();

            if (content.Length > remaining)
                content = content[..remaining];

            sb.AppendLine(content);
            sb.AppendLine();
            remaining -= content.Length + 2;
            chunksSent++;
        }

        return sb.ToString().Trim();
    }

    private static string BuildDirectDocumentContextPrompt(
        string userPrompt,
        List<(string Title, string DocumentType, string Text)> documents,
        RagOptions? options)
    {
        var maxTotal = options?.MaxContextCharacters ?? 16000;
        var remainingChars = maxTotal;
        var sb = new StringBuilder();

        foreach (var (title, documentType, text) in documents)
        {
            if (remainingChars <= 0)
                break;

            var content = text.Length > remainingChars ? text[..remainingChars] : text;
            remainingChars -= content.Length;

            var isTable = documentType is "Excel" or "Csv";

            sb.AppendLine($"--- Dokument: {title} ---");

            if (isTable)
                sb.AppendLine("Typ: Tabelldata (läs varje rad noggrant, missa ingen rad)");

            sb.AppendLine();
            sb.AppendLine(content.Trim());
            sb.AppendLine();
            sb.AppendLine("--- Slut ---");
            sb.AppendLine();
        }

        var hasTable = documents.Any(d => d.DocumentType is "Excel" or "Csv");

        var instruction = hasTable
            ? """


Nedan finns hela innehållet i det bifogade dokumentet. Dokumentet innehåller tabelldata.
Läs varje rad i tabellen noggrant och inkludera all relevant information i ditt svar.
Svara systematiskt rad för rad om frågan handlar om specifika poster.

Dokumentinnehåll:
"""
            : """


Nedan finns hela innehållet i det bifogade dokumentet. Svara baserat på denna information.

Dokumentinnehåll:
""";

        return userPrompt + instruction + "\n" + sb.ToString().Trim();
    }

    private static string BuildEnumerationDirectContextPrompt(
        string userPrompt,
        List<(string Title, string DocumentType, string Text)> documents,
        QueryIntent intent,
        PatternAnalysisResult? patterns,
        RagOptions? options)
    {
        // Use FullDocumentMaxContextCharacters so the complete document is sent —
        // the same limit used by FullDocumentRetrieval. MaxContextCharacters (16k) is
        // intended for SemanticRetrieval snippets, not full-document enumeration.
        var maxTotal = options?.FullDocumentMaxContextCharacters ?? 48000;
        var remainingChars = maxTotal;
        var sb = new StringBuilder();

        foreach (var (title, documentType, text) in documents)
        {
            if (remainingChars <= 0)
                break;

            var content = text.Length > remainingChars ? text[..remainingChars] : text;
            remainingChars -= content.Length;

            sb.AppendLine($"--- Dokument: {title} ---");

            if (documentType is "Excel" or "Csv")
                sb.AppendLine("Typ: Tabelldata (läs varje rad noggrant, missa ingen rad)");

            sb.AppendLine();
            sb.AppendLine(content.Trim());
            sb.AppendLine();
            sb.AppendLine("--- Slut ---");
            sb.AppendLine();
        }

        var instruction = BuildEnumerationInstruction(intent, patterns);
        return userPrompt + instruction + sb.ToString().Trim();
    }

    private static string BuildEnumerationInstruction(QueryIntent intent, PatternAnalysisResult? patterns)
    {
        var isTableFocus = intent == QueryIntent.TableEnumeration;
        var sb = new StringBuilder();

        sb.Append("""


            Du arbetar med dokumentinventering. Detta är INTE en sammanfattningsuppgift.
            Du måste identifiera varje enskild förekomst som matchar användarens fråga.

            Regler:
            1. Hoppa inte över poster.
            2. Sammanfatta inte.
            3. Slå inte ihop serier (t.ex. "FG-100G till FG-400G" räknas som separata poster).
            4. Om flera objekt förekommer skall varje objekt listas separat.
            5. Utför arbetet i två steg:
               - Steg 1: Skanna dokumentet och extrahera samtliga kandidater
               - Steg 2: Besvara användarens fråga baserat på extraheringen
            6. Om frågan gäller antal, lista objekten först och räkna sedan.
            """);

        if (isTableFocus)
        {
            sb.AppendLine();
            sb.AppendLine("Dokumentet innehåller tabelldata. Läs varje rad noggrant — inga rader är utelämnade.");
        }

        if (patterns is not null && patterns.CandidateModels.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Identifierade kandidater i dokumentet ({patterns.CandidateModels.Count} st):");

            foreach (var model in patterns.CandidateModels.Take(50))
                sb.AppendLine($"- {model}");

            sb.AppendLine();
            sb.AppendLine("Använd dessa som hjälp men verifiera mot dokumentinnehållet innan du svarar.");
        }

        sb.AppendLine();
        sb.AppendLine("Dokumentinnehåll:");

        return sb.ToString();
    }

    public static async Task<string> BuildAiPromptWithCollectionContextAsync(
        IDocumentRetrievalService documentRetrievalService,
        string userPrompt,
        List<Guid> allowedCollectionIds,
        RagOptions? options = null,
        CancellationToken ct = default)
    {
        if (allowedCollectionIds.Count == 0)
            return userPrompt;

        var relevantChunks = await documentRetrievalService.RetrieveFromCollectionsAsync(
            allowedCollectionIds,
            userPrompt,
            top: 0,
            ct);

        if (relevantChunks.Count == 0)
            return userPrompt + "\n\n\nAktiva kunskapssamlingar finns, men ingen relevant dokumentkontext hittades.\n";

        var context = BuildRetrievedDocumentContext(relevantChunks, options);

        return userPrompt + """


Använd endast relevanta dokumentutdrag nedan från kunskapssamlingen. Om svaret saknas i utdragen, säg det.

Dokumentutdrag:
""" + "\n" + context;
    }

    // ── Document-structure utilities ──────────────────────────────────────────

    public static bool IsTextExtractableDocument(string documentType, string extension)
    {
        extension = extension.ToLowerInvariant();

        return string.Equals(documentType, "Txt", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Markdown", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Pdf", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Docx", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Excel", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Csv", StringComparison.OrdinalIgnoreCase)
               || string.Equals(documentType, "Code", StringComparison.OrdinalIgnoreCase)
               || extension is ".txt" or ".md" or ".pdf" or ".docx" or ".xlsx" or ".xlsm" or ".csv"
               || IsPlainTextExtension(extension);
    }

    public static bool IsPlainTextExtension(string extension)
    {
        extension = extension.ToLowerInvariant();
        return extension is
            ".cs" or ".razor" or ".js" or ".ts" or ".jsx" or ".tsx" or
            ".py" or ".java" or ".go" or ".rs" or ".cpp" or ".c" or ".h" or ".hpp" or
            ".json" or ".xml" or ".yaml" or ".yml" or
            ".css" or ".scss" or ".sass" or ".less" or
            ".html" or ".htm" or ".svg" or
            ".sql" or ".sh" or ".bash" or ".zsh" or
            ".ps1" or ".psm1" or ".psd1" or ".bat" or ".cmd" or
            ".tsv" or ".log" or ".ini" or ".toml" or ".conf" or ".config" or
            ".gitignore" or ".editorconfig" or ".env" or
            ".tf" or ".hcl" or ".bicep" or
            ".vue" or ".svelte" or ".dart" or ".kt" or ".swift" or ".rb" or ".php" or
            ".lua" or ".r" or ".m" or ".f90" or ".fs" or ".fsx" or ".vb";
    }

    public static int CountExtractedPages(string text)
    {
        return PageMarkerRegex.Matches(text).Count;
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    public static List<DocumentChunk> CreateDocumentChunks(
        Guid documentId,
        string text,
        int maxChunkLength = 3000,
        int overlapLength = 300,
        int minChunkLength = 150)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        // Excel content uses row-aware chunking: chunks are never cut mid-row and every
        // chunk carries the sheet name + full column headers so it is self-contained.
        if (text.Contains("--- Sheet:", StringComparison.Ordinal))
            return CreateTableAwareChunks(documentId, text, maxChunkLength);

        // Detect PDF content from the original text before ParsePageSegments strips the
        // page markers.  The flag is forwarded to NormalizeExtractedText so that
        // CleanupPdfArtifacts (which breaks structured identifiers) is skipped for Excel/CSV.
        var isPdfContent = PageMarkerRegex.IsMatch(text);

        var segments = ParsePageSegments(text);
        var chunks = new List<DocumentChunk>();
        var chunkIndex = 0;
        string? prevTail = null;

        foreach (var segment in segments)
        {
            var pageText = NormalizeExtractedText(segment.Content, isPdfContent);

            if (string.IsNullOrWhiteSpace(pageText))
                continue;

            // Add overlap from the previous segment
            var workText = prevTail is not null
                ? prevTail + "\n\n" + pageText
                : pageText;

            // For markdown tables (Excel/CSV sheets), extract the header rows so
            // continuation chunks can have column context even when the header row
            // is far back in an earlier chunk.
            var tableHeader = ExtractMarkdownTableHeader(pageText);

            var position = 0;
            var isFirstChunkOfSegment = true;

            while (position < workText.Length)
            {
                var remaining = workText.Length - position;
                var length = Math.Min(maxChunkLength, remaining);
                var chunkText = workText.Substring(position, length);

                if (position + length < workText.Length)
                {
                    var splitAt = FindBestSplitPosition(chunkText, maxChunkLength);

                    if (splitAt > minChunkLength)
                    {
                        chunkText = chunkText[..splitAt].Trim();
                        length = splitAt;
                    }
                }

                chunkText = chunkText.Trim();

                // Prepend column headers to table-continuation chunks so each chunk
                // is self-contained and embeds with full column context.
                if (!isFirstChunkOfSegment &&
                    tableHeader is not null &&
                    chunkText.StartsWith('|') &&
                    !chunkText.StartsWith(tableHeader))
                {
                    chunkText = tableHeader + "\n" + chunkText;
                }

                if (chunkText.Length >= 80)
                {
                    chunks.Add(new DocumentChunk
                    {
                        Id = Guid.NewGuid(),
                        DocumentId = documentId,
                        ChunkIndex = chunkIndex++,
                        Content = chunkText,
                        PageNumber = segment.PageNumber,
                        Heading = DetectHeading(chunkText),
                        CharacterCount = chunkText.Length,
                        TokenCount = EstimateTokenCount(chunkText),
                        CreatedUtc = DateTime.UtcNow
                    });
                    isFirstChunkOfSegment = false;
                }

                if (position + length >= workText.Length)
                    break;

                var step = length - overlapLength;
                if (step <= 0) step = length;
                position += step;
            }

            // Save tail of this segment for the next segment's overlap
            prevTail = workText.Length > overlapLength
                ? workText[^overlapLength..].Trim()
                : workText.Trim();
        }

        return chunks;
    }

    // Chunks Excel-extracted text (which contains "--- Sheet: X ---" markers) by complete
    // table rows.  Each produced chunk starts with the sheet name + full column header row +
    // separator and contains only whole data rows — never a partial row.
    // No character overlap is used: the repeated header makes every chunk self-contained.
    private static List<DocumentChunk> CreateTableAwareChunks(
        Guid documentId, string text, int maxChunkLength)
    {
        var chunks = new List<DocumentChunk>();

        var sheetMatches = SheetMarkerRegex.Matches(text);

        for (var i = 0; i < sheetMatches.Count; i++)
        {
            var m            = sheetMatches[i];
            var sheetName    = m.Groups[1].Value.Trim();
            var contentStart = m.Index + m.Length;
            var contentEnd   = i + 1 < sheetMatches.Count ? sheetMatches[i + 1].Index : text.Length;
            var sheetContent = text[contentStart..contentEnd];

            AddTableChunks(documentId, sheetName, sheetContent, maxChunkLength, chunks);
        }

        return chunks;
    }

    private static readonly Regex SheetMarkerRegex =
        new(@"^---\s*Sheet:\s*(.+?)\s*---", RegexOptions.Compiled | RegexOptions.Multiline);

    // Splits one sheet's markdown text into row-complete chunks, each prefixed with
    // the sheet marker + header + separator.
    private static void AddTableChunks(
        Guid documentId,
        string sheetName,
        string sheetContent,
        int maxChunkLength,
        List<DocumentChunk> chunks)
    {
        var lines = sheetContent.Split('\n');

        string? headerLine    = null;
        string? separatorLine = null;
        var     dataLines     = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r').TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (!line.StartsWith('|'))
                continue;

            if (headerLine is null)
            {
                headerLine = line;
            }
            else if (separatorLine is null && line.All(c => c is '|' or '-' or ' ' or ':'))
            {
                separatorLine = line;
            }
            else
            {
                dataLines.Add(line);
            }
        }

        if (headerLine is null)
            return;

        var sep         = separatorLine ?? BuildSeparatorFromHeader(headerLine);
        var headerBlock = $"--- Sheet: {sheetName} ---\n{headerLine}\n{sep}";

        // Accumulate complete rows into chunks, flushing when the size limit is reached.
        var currentRows   = new List<string>();
        var currentLength = headerBlock.Length;

        void Flush()
        {
            if (currentRows.Count == 0)
                return;

            var content = (headerBlock + "\n" + string.Join("\n", currentRows)).Trim();

            if (content.Length >= 80)
            {
                chunks.Add(new DocumentChunk
                {
                    Id             = Guid.NewGuid(),
                    DocumentId     = documentId,
                    ChunkIndex     = chunks.Count,
                    Content        = content,
                    PageNumber     = null,
                    Heading        = sheetName,
                    CharacterCount = content.Length,
                    TokenCount     = EstimateTokenCount(content),
                    CreatedUtc     = DateTime.UtcNow
                });
            }

            currentRows.Clear();
            currentLength = headerBlock.Length;
        }

        foreach (var row in dataLines)
        {
            var rowLen = row.Length + 1; // +1 for newline

            // Flush current batch before adding a row that would exceed the size limit.
            // Always include at least one row per chunk to avoid infinite loops.
            if (currentRows.Count > 0 && currentLength + rowLen > maxChunkLength)
                Flush();

            currentRows.Add(row);
            currentLength += rowLen;
        }

        Flush();

        // Emit a header-only chunk when the sheet has no data rows (preserves sheet visibility).
        if (dataLines.Count == 0 && headerBlock.Length >= 80)
        {
            chunks.Add(new DocumentChunk
            {
                Id             = Guid.NewGuid(),
                DocumentId     = documentId,
                ChunkIndex     = chunks.Count,
                Content        = headerBlock.Trim(),
                PageNumber     = null,
                Heading        = sheetName,
                CharacterCount = headerBlock.Length,
                TokenCount     = EstimateTokenCount(headerBlock),
                CreatedUtc     = DateTime.UtcNow
            });
        }
    }

    private static string BuildSeparatorFromHeader(string headerLine)
    {
        var parts = headerLine.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return "| " + string.Join(" | ", parts.Select(p => new string('-', Math.Max(3, p.Trim().Length)))) + " |";
    }

    // ── File/document type detection ──────────────────────────────────────────

    public static string DetectDocumentType(string extension, string? contentType)
    {
        extension = extension.ToLowerInvariant();

        if (IsPlainTextExtension(extension))
            return "Code";

        return extension switch
        {
            ".pdf" => "Pdf",
            ".docx" => "Docx",
            ".txt" => "Txt",
            ".md" => "Markdown",
            ".xlsx" or ".xlsm" or ".xls" => "Excel",
            ".csv" => "Csv",
            _ when string.Equals(contentType, "application/pdf",
                StringComparison.OrdinalIgnoreCase) => "Pdf",
            _ when string.Equals(contentType, "text/plain",
                StringComparison.OrdinalIgnoreCase) => "Txt",
            _ when string.Equals(contentType,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase) => "Excel",
            _ when string.Equals(contentType, "application/vnd.ms-excel",
                StringComparison.OrdinalIgnoreCase) => "Excel",
            _ when string.Equals(contentType, "text/csv",
                StringComparison.OrdinalIgnoreCase) => "Csv",
            _ when string.Equals(contentType, "application/csv",
                StringComparison.OrdinalIgnoreCase) => "Csv",
            _ => "Unknown"
        };
    }

    public static string DetectFileType(string extension, string? contentType)
    {
        extension = extension.ToLowerInvariant();

        if (IsPlainTextExtension(extension))
            return "Document";

        return extension switch
        {
            ".pdf" or ".docx" or ".xlsx" or ".xlsm" or ".xls" or ".csv" or ".txt" or ".md" => "Document",

            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" => "Image",

            ".mp3" or ".wav" or ".m4a" or ".ogg" => "Audio",

            _ when contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true => "Image",
            _ when contentType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true => "Audio",
            _ when contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true => "Document",
            _ when string.Equals(contentType, "application/pdf",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType, "application/vnd.ms-excel",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType, "application/csv",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType, "application/json",
                StringComparison.OrdinalIgnoreCase) => "Document",
            _ when string.Equals(contentType, "application/xml",
                StringComparison.OrdinalIgnoreCase) => "Document",

            _ => "Other"
        };
    }

    public static string CreateConversationTitle(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Ny chatt";

        var title = message.Replace("\r", " ").Replace("\n", " ").Trim();

        if (title.Length > 42)
            title = title[..42].Trim() + "...";

        return title;
    }

    public static bool IsDuplicateUserInsert(DbUpdateException ex)
    {
        return ex.InnerException is MySqlConnector.MySqlException mysqlException
               && mysqlException.Number == 1062;
    }

    // ── Context formatting ────────────────────────────────────────────────────

    public static string BuildRetrievedDocumentContext(
        List<RetrievedDocumentChunk> chunks,
        RagOptions? options = null)
    {
        var maxTotal = options?.MaxContextCharacters ?? 16000;
        var maxPerChunk = options?.MaxChunkCharacters ?? 2400;
        var minScore = options?.MinRelevanceScore ?? 0.45;

        var sb = new StringBuilder();

        var selected = chunks
            .Where(x => x.Score >= minScore)
            .OrderByDescending(x => x.Score)
            .ToList();

        if (selected.Count == 0)
        {
            selected = chunks.OrderByDescending(x => x.Score).Take(1).ToList();
        }

        foreach (var chunk in selected)
        {
            if (sb.Length >= maxTotal)
                break;

            var content = chunk.Content ?? "";

            if (content.Length > maxPerChunk)
                content = content[..maxPerChunk];

            var remaining = maxTotal - sb.Length;

            if (content.Length > remaining)
                content = content[..remaining];

            var pageInfo = chunk.PageNumber.HasValue ? $"Sida: {chunk.PageNumber}" : "";
            var headingInfo = !string.IsNullOrWhiteSpace(chunk.Heading) ? $"\nRubrik: {chunk.Heading}" : "";

            sb.AppendLine($"--- Dokument: {chunk.DocumentTitle} ---");
            sb.AppendLine($"Fil: {chunk.FileName}");

            if (!string.IsNullOrWhiteSpace(pageInfo))
                sb.AppendLine(pageInfo);

            if (!string.IsNullOrWhiteSpace(headingInfo))
                sb.Append(headingInfo);

            sb.AppendLine($"Chunk: {chunk.ChunkIndex}  Relevans: {chunk.Score:F4}");
            sb.AppendLine();
            sb.AppendLine(content.Trim());
            sb.AppendLine();
            sb.AppendLine("--- Slut utdrag ---");
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    // ── Text normalisation (preserved structure) ──────────────────────────────

    private static string NormalizeExtractedText(string text, bool isPdfContent = false)
    {
        var normalized = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\t", "    ");

        // Collapse 3+ blank lines to a single blank line
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");

        // Collapse multiple spaces within a line (not leading spaces)
        normalized = Regex.Replace(normalized, @"(?<!\n) {2,}", " ");

        // PDF artifact cleanup (OCR run-togethers like "10Gbps" → "10 Gbps") is applied
        // only when the original full text contained PDF page markers.  It must NOT run
        // for Excel/CSV — the regexes break structured identifiers like FNT002400 or VLAN10A.
        if (isPdfContent)
            normalized = DocumentExtractionService.CleanupPdfArtifacts(normalized);

        return normalized.Trim();
    }

    // ── Page segment parsing ──────────────────────────────────────────────────

    private static readonly Regex PageMarkerRegex =
        new(@"^--- Page (\d+) ---", RegexOptions.Multiline | RegexOptions.Compiled);

    private sealed record TextSegment(int? PageNumber, string Content);

    private static List<TextSegment> ParsePageSegments(string text)
    {
        var segments = new List<TextSegment>();
        var matches = PageMarkerRegex.Matches(text);

        if (matches.Count == 0)
        {
            segments.Add(new TextSegment(null, text));
            return segments;
        }

        // Content before the first page marker
        if (matches[0].Index > 0)
        {
            var pre = text[..matches[0].Index].Trim();
            if (!string.IsNullOrWhiteSpace(pre))
                segments.Add(new TextSegment(null, pre));
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var pageNumber = int.Parse(match.Groups[1].Value);
            var contentStart = match.Index + match.Length;
            var contentEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var content = text[contentStart..contentEnd].Trim();

            if (!string.IsNullOrWhiteSpace(content))
                segments.Add(new TextSegment(pageNumber, content));
        }

        return segments;
    }

    // ── Heading detection ─────────────────────────────────────────────────────

    private static readonly Regex NumberedHeadingRegex =
        new(@"^(\d+\.)+\s*\S", RegexOptions.Compiled);

    private static string? DetectHeading(string chunkText)
    {
        var firstLine = chunkText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?.Trim();

        if (string.IsNullOrWhiteSpace(firstLine) || firstLine.Length > 120)
            return null;

        // Numbered section: "1.", "2.3", "A.1"
        if (NumberedHeadingRegex.IsMatch(firstLine))
            return firstLine.Length > 100 ? firstLine[..100] : firstLine;

        // ALL-CAPS heading (≤ 80 chars, contains letters)
        if (firstLine.Length <= 80 &&
            firstLine.ToUpperInvariant() == firstLine &&
            firstLine.Any(char.IsLetter))
        {
            return firstLine;
        }

        // Line ending with colon (short definition/section label)
        if (firstLine.EndsWith(':') && firstLine.Length <= 80)
            return firstLine.TrimEnd(':');

        return null;
    }

    // ── Document-wide query detection ─────────────────────────────────────────

    private static readonly string[] DocumentWideKeywords =
    [
        "sammanfatta", "summera", "översikt", "vad handlar", "vad innehåller",
        "beskriv dokumentet", "hela dokumentet", "allt i ", "viktigaste kraven",
        "viktigaste krav", "lista alla", "lista samtliga", "alla krav", "samtliga krav",
        "nyckelkrav", "tekniska krav", "sla-krav", "säkerhetskrav",
        "summary", "summarize", "overview", "key requirements", "all requirements",
        "main points", "key points", "what does the document"
    ];

    private static bool IsDocumentWideQuery(string query)
    {
        var lower = query.ToLowerInvariant();
        return DocumentWideKeywords.Any(kw => lower.Contains(kw));
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the column-header line + separator line of a markdown table if the text starts
    /// with one, otherwise null.  Used to propagate headers into table-continuation chunks.
    /// </summary>
    internal static string? ExtractMarkdownTableHeader(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < Math.Min(lines.Length - 1, 8); i++)
        {
            if (!lines[i].StartsWith('|')) continue;

            var sep = lines[i + 1];
            // Separator row contains only |, -, and spaces
            if (sep.StartsWith('|') &&
                sep.All(c => c is '|' or '-' or ' '))
            {
                return lines[i] + "\n" + sep;
            }
        }

        return null;
    }

    private static int FindBestSplitPosition(string text, int maxChunkLength)
    {
        var minPosition = maxChunkLength / 2;

        var paragraphBreak = text.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (paragraphBreak > minPosition)
            return paragraphBreak;

        var sentenceEnd = Math.Max(
            text.LastIndexOf(". ", StringComparison.Ordinal),
            Math.Max(
                text.LastIndexOf("! ", StringComparison.Ordinal),
                text.LastIndexOf("? ", StringComparison.Ordinal)));

        if (sentenceEnd > minPosition)
            return sentenceEnd + 1;

        var lineBreak = text.LastIndexOf('\n');
        if (lineBreak > minPosition)
            return lineBreak;

        var space = text.LastIndexOf(' ');
        if (space > minPosition)
            return space;

        return -1;
    }

    private static int EstimateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }
}
