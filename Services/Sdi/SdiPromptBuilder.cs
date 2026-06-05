using System.Text;
using InfinityAI.Api.Models.Rag;
using InfinityAI.Api.Models.Sdi;

namespace InfinityAI.Api.Services.Sdi;

/// <summary>
/// Builds the prompt injected into the LLM after a successful SDI calculation.
/// The LLM is asked to explain the deterministic result — not to recalculate it.
///
/// Two modes controlled by <see cref="RagOptions.IncludeStructuredDebugInPrompt"/>:
///   false (default) — Human mode: natural language instruction, no internal details.
///   true            — Debug mode: includes extractor name, method, confidence for traceability.
/// </summary>
public static class SdiPromptBuilder
{
    public static string Build(
        string userPrompt,
        IEnumerable<(string Title, string DocumentType, string Text)> documents,
        StructuredAnswer answer,
        RagOptions? options)
    {
        var sb     = new StringBuilder();
        var debug  = options?.IncludeStructuredDebugInPrompt ?? false;

        if (debug)
            AppendDebugBlock(sb, answer);
        else
            AppendHumanBlock(sb, answer);

        AppendDocumentContext(sb, documents, options);

        return userPrompt + sb.ToString();
    }

    // ── Human mode (default) ──────────────────────────────────────────────────

    private static void AppendHumanBlock(StringBuilder sb, StructuredAnswer answer)
    {
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[Structured analysis result]");
        sb.AppendLine($"Result: {answer.Value}");

        // Provide enough context for the LLM to explain *which* column/sheet was used,
        // so the answer is transparent (e.g., "I interpret this as the count of entries
        // in the Nätbrygga_Vlan column of the Links sheet").
        if (!string.IsNullOrWhiteSpace(answer.ColumnUsed))
            sb.AppendLine($"Source column: {answer.ColumnUsed}");

        sb.AppendLine();
        sb.AppendLine("The result above was calculated directly from the structured data in the document.");
        sb.AppendLine("Write a clear, natural response. Briefly mention which column or sheet the result");
        sb.AppendLine("comes from so the user understands the interpretation (e.g., 'I count X entries");
        sb.AppendLine("in the Nätbrygga_Vlan column of the Links sheet'). Do NOT mention method names,");
        sb.AppendLine("extractor names, or confidence scores. Do NOT recalculate — use the value above.");
    }

    // ── Debug mode ────────────────────────────────────────────────────────────

    private static void AppendDebugBlock(StringBuilder sb, StructuredAnswer answer)
    {
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("--- Structured analysis result [DEBUG MODE] ---");
        sb.AppendLine($"Result:            {answer.Value}");
        sb.AppendLine($"Method:            {answer.Method}");
        sb.AppendLine($"Confidence:        {answer.Confidence:F0}%");

        if (!string.IsNullOrWhiteSpace(answer.ExtractorName))
        {
            sb.AppendLine($"Extractor:         {answer.ExtractorName} v{answer.ExtractorVersion}");
            sb.AppendLine($"ExtractorPriority: {answer.ExtractorPriority}");
            sb.AppendLine($"CandidateCount:    {answer.CandidateCount}");
        }

        sb.AppendLine($"RowsProcessed:     {answer.RowsProcessed}");

        if (!string.IsNullOrWhiteSpace(answer.ColumnUsed))
            sb.AppendLine($"Column:            {answer.ColumnUsed}");

        if (!string.IsNullOrWhiteSpace(answer.FallbackExtractor))
            sb.AppendLine($"FallbackExtractor: {answer.FallbackExtractor}");

        if (answer.ValuesExtracted > 0)
            sb.AppendLine($"ValuesExtracted:   {answer.ValuesExtracted}");

        if (answer.DistinctValues > 0)
            sb.AppendLine($"DistinctValues:    {answer.DistinctValues}");

        sb.AppendLine("--- End ---");
        sb.AppendLine();
        sb.AppendLine("The result is deterministically correct — do NOT recalculate.");
        sb.AppendLine("Debug mode is active: include relevant technical details in your response.");
    }

    // ── Document context snippet ──────────────────────────────────────────────

    private static void AppendDocumentContext(
        StringBuilder sb,
        IEnumerable<(string Title, string DocumentType, string Text)> documents,
        RagOptions? options)
    {
        var maxContext = options?.MaxContextCharacters ?? 16_000;
        var remaining  = maxContext;

        sb.AppendLine();
        sb.AppendLine("Document reference (excerpt for context):");

        foreach (var (title, _, text) in documents)
        {
            if (remaining <= 0) break;

            var snippet = text.Length > remaining ? text[..remaining] : text;
            remaining -= snippet.Length;

            sb.AppendLine($"--- {title} ---");
            sb.AppendLine(snippet.Trim());
            sb.AppendLine("--- End ---");
        }
    }
}
