using InfinityAI.Models.Database;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Services.Llm;
using Microsoft.Extensions.Logging;

namespace InfinityAI.Api.Services;

public interface IDocumentAggregationService
{
    /// <summary>
    /// Splits <paramref name="allChunks"/> into batches that fit within the context limit,
    /// runs an extraction LLM call per batch, then aggregates the partial results into one
    /// final answer. Used for ListAll / TableEnumeration when the document is too large to
    /// fit in a single FullDocumentRetrieval pass.
    /// </summary>
    Task<AggregationResult> AggregateAsync(
        Guid userId,
        string userQuery,
        QueryIntent intent,
        List<RetrievedDocumentChunk> allChunks,
        RagOptions options,
        CancellationToken ct = default);
}

public sealed class DocumentAggregationService : IDocumentAggregationService
{
    private readonly ILlmRouter _llmRouter;
    private readonly ILogger<DocumentAggregationService> _logger;

    public DocumentAggregationService(ILlmRouter llmRouter, ILogger<DocumentAggregationService> logger)
    {
        _llmRouter = llmRouter;
        _logger = logger;
    }

    public async Task<AggregationResult> AggregateAsync(
        Guid userId,
        string userQuery,
        QueryIntent intent,
        List<RetrievedDocumentChunk> allChunks,
        RagOptions options,
        CancellationToken ct = default)
    {
        var charsPerPass = options.PassMaxContextCharacters > 0
            ? options.PassMaxContextCharacters
            : options.FullDocumentMaxContextCharacters;

        var batches = SplitIntoBatches(allChunks, charsPerPass, options.MaxRecoveryPasses);

        _logger.LogInformation(
            "[AGGREGATION] Starting multi-pass. TotalChunks={Total}, Batches={Batches}, CharsPerPass={Chars}",
            allChunks.Count, batches.Count, charsPerPass);

        // ── Extraction passes ─────────────────────────────────────────────────
        var partialResults = new List<string>(batches.Count);

        for (var i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var batchContext = BuildBatchContext(batches[i]);
            var extractionPrompt = BuildExtractionPrompt(userQuery, intent, batchContext, i + 1, batches.Count);

            var messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = extractionPrompt }
            };

            var partial = await _llmRouter.SendChatAsync(userId, messages, [], ct) ?? "";
            partialResults.Add(partial);

            _logger.LogInformation(
                "[AGGREGATION] Pass {Pass}/{Total} complete. ResponseLength={Len}",
                i + 1, batches.Count, partial.Length);
        }

        // ── Aggregation pass (skip if only one batch) ─────────────────────────
        string finalAnswer;
        var totalPasses = batches.Count;

        if (partialResults.Count == 1)
        {
            finalAnswer = partialResults[0];
        }
        else
        {
            totalPasses++;
            var aggregationPrompt = BuildAggregationPrompt(userQuery, partialResults);
            var aggMessages = new List<ChatMessage>
            {
                new() { Role = "user", Content = aggregationPrompt }
            };

            finalAnswer = await _llmRouter.SendChatAsync(userId, aggMessages, [], ct) ?? "";

            _logger.LogInformation(
                "[AGGREGATION] Aggregation pass complete. FinalLength={Len}", finalAnswer.Length);
        }

        // ── Verification pass (optional) ──────────────────────────────────────
        if (options.EnableVerificationPass && !string.IsNullOrWhiteSpace(finalAnswer))
        {
            totalPasses++;
            finalAnswer = await RunVerificationPassAsync(
                userId, userQuery, allChunks, finalAnswer, ct);
        }

        // ── Coverage metrics ──────────────────────────────────────────────────
        var totalChars = allChunks.Sum(c => c.Content.Length);
        var sentChars = batches.Sum(b => b.Sum(c => c.Content.Length));
        var coverage = DocumentCoverageResult.Compute(
            totalChunks: allChunks.Count,
            chunksSent: allChunks.Count,        // recovery sends all
            totalChars: totalChars,
            charsSent: Math.Min(sentChars, totalChars));

        _logger.LogInformation(
            "[AGGREGATION] Completed. TotalPasses={Passes}, FinalLength={Len}, TotalChunks={Chunks}",
            totalPasses, finalAnswer.Length, allChunks.Count);

        return new AggregationResult
        {
            FinalAnswer = finalAnswer,
            PassCount = totalPasses,
            Recovered = true,
            Coverage = coverage
        };
    }

    // ── Batch splitting ───────────────────────────────────────────────────────

    private static List<List<RetrievedDocumentChunk>> SplitIntoBatches(
        List<RetrievedDocumentChunk> chunks,
        int maxCharsPerBatch,
        int maxBatches)
    {
        var batches = new List<List<RetrievedDocumentChunk>>();
        var current = new List<RetrievedDocumentChunk>();
        var currentChars = 0;

        foreach (var chunk in chunks)
        {
            // At max capacity: append overflow to the last batch to avoid losing data
            if (batches.Count >= maxBatches)
            {
                batches[^1].Add(chunk);
                continue;
            }

            if (currentChars + chunk.Content.Length > maxCharsPerBatch && current.Count > 0)
            {
                batches.Add(current);
                current = [];
                currentChars = 0;

                // Now at capacity: drain this chunk directly into the last batch
                if (batches.Count >= maxBatches)
                {
                    batches[^1].Add(chunk);
                    continue;
                }
            }

            current.Add(chunk);
            currentChars += chunk.Content.Length;
        }

        if (current.Count > 0)
        {
            if (batches.Count >= maxBatches)
                batches[^1].AddRange(current);
            else
                batches.Add(current);
        }

        return batches;
    }

    private static string BuildBatchContext(List<RetrievedDocumentChunk> batch)
    {
        var sb = new System.Text.StringBuilder();
        string? currentDoc = null;

        foreach (var chunk in batch)
        {
            if (currentDoc != chunk.DocumentTitle)
            {
                currentDoc = chunk.DocumentTitle;
                sb.AppendLine($"--- {chunk.DocumentTitle} ---");
            }

            sb.AppendLine(chunk.Content.Trim());
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    private static string BuildExtractionPrompt(
        string userQuery,
        QueryIntent intent,
        string batchContext,
        int passNumber,
        int totalPasses)
    {
        var taskDescription = intent == QueryIntent.TableEnumeration
            ? "Extrahera ALLA rader och poster från tabellen i detta dokumentavsnitt."
            : "Identifiera och lista SAMTLIGA objekt som matchar frågan i detta dokumentavsnitt.";

        return $"""
            Du är en precis informationsextraktor. Din uppgift är att extrahera information från ett dokumentavsnitt.

            Originalfrågan: {userQuery}

            Dokumentavsnitt {passNumber} av {totalPasses}:
            {batchContext}

            Instruktion:
            {taskDescription}
            Lista varje objekt på en separat rad med "- " prefix.
            Hoppa inte över någon rad eller post — fullständighet är viktigast.
            Inkludera ALLA objekt du hittar i detta avsnitt, även om listan blir lång.
            """;
    }

    private static string BuildAggregationPrompt(string userQuery, List<string> partialResults)
    {
        var combined = string.Join("\n\n--- Nästa avsnitt ---\n\n",
            partialResults.Select((r, i) => $"Avsnitt {i + 1}:\n{r}"));

        return $"""
            Du ska kombinera extraherade listor från olika delar av ett dokument till en fullständig lista.

            Originalfrågan: {userQuery}

            Extraherade listor:
            {combined}

            Instruktion:
            1. Slå ihop alla listor till en enda komplett lista.
            2. Ta bort exakta dubbletter (behåll en kopia).
            3. Behåll den logiska ordningen (t.ex. modellnummer i stigande ordning).
            4. Returnera den fullständiga kombinerade listan, ett objekt per rad med "- " prefix.
            5. Lägg inte till förklaringar eller kommentarer — bara listan.
            """;
    }

    private async Task<string> RunVerificationPassAsync(
        Guid userId,
        string userQuery,
        List<RetrievedDocumentChunk> allChunks,
        string currentAnswer,
        CancellationToken ct)
    {
        var sourceCount = Services.DocumentCoverageValidator.CountSourceRows(allChunks);
        var answerCount = Services.DocumentCoverageValidator.CountResponseItems(currentAnswer);

        if (sourceCount == 0 || answerCount >= sourceCount)
            return currentAnswer;

        _logger.LogWarning(
            "[AGGREGATION][VERIFY] Potential gap detected. SourceRows={Source}, AnswerItems={Answer}",
            sourceCount, answerCount);

        var verificationPrompt = $"""
            Dokumentet verkar innehålla ca {sourceCount} poster.
            Det nuvarande svaret innehåller ca {answerCount} poster.

            Nuvarande svar:
            {currentAnswer}

            Originalfrågan: {userQuery}

            Instruktion:
            Granska svaret och komplettera med eventuella saknade poster.
            Returnera hela den kompletta listan inklusive eventuella tillägg.
            """;

        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = verificationPrompt }
        };

        var verified = await _llmRouter.SendChatAsync(userId, messages, [], ct);

        _logger.LogInformation(
            "[AGGREGATION][VERIFY] Verification complete. OriginalLength={Orig}, VerifiedLength={New}",
            currentAnswer.Length, verified?.Length ?? 0);

        return string.IsNullOrWhiteSpace(verified) ? currentAnswer : verified;
    }
}
