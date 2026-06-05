using System.Text.RegularExpressions;

namespace InfinityAI.Api.Services;

public sealed class PatternAnalysisResult
{
    public List<string> CandidateModels { get; init; } = [];
    public List<string> CandidateProducts { get; init; } = [];
}

/// <summary>
/// Identifies product model codes and series names in document text via regex.
/// Results are used as supporting context hints in enumeration prompts.
/// </summary>
public static class DocumentPatternAnalyzer
{
    // Matches generic product/model codes: 2-8 uppercase letters (optionally a slash-combined second
    // prefix like FG/FWF), then hyphen and alphanumeric segments.
    // Examples: FG-120G, FG/FWF-30G, SW-200F, AP-430F, FGT-1000D, WF-100, ABC-1234X-5
    private static readonly Regex ModelCodeRegex = new(
        @"\b[A-Z]{2,8}(?:/[A-Z]{2,8})?-[A-Z0-9][A-Z0-9-]*\b",
        RegexOptions.Compiled);

    public static PatternAnalysisResult Analyze(string documentText)
    {
        if (string.IsNullOrWhiteSpace(documentText))
            return new PatternAnalysisResult();

        var models = ModelCodeRegex.Matches(documentText)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PatternAnalysisResult
        {
            CandidateModels = models,
            CandidateProducts = []
        };
    }
}
