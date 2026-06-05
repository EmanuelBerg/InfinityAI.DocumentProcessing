namespace InfinityAI.Api.Models.ImageIntent;

public sealed class ImageIntentResult
{
    public ImageIntentType Type { get; init; }

    /// <summary>True when the result requires calling image generation (TextToImage or ImageEdit).</summary>
    public bool RequiresImageGeneration =>
        Type is ImageIntentType.TextToImage or ImageIntentType.ImageEdit;

    /// <summary>True when the result requires describing a source image before generating.</summary>
    public bool RequiresImageEdit => Type == ImageIntentType.ImageEdit;

    /// <summary>True when the result should be answered by the vision model.</summary>
    public bool RequiresVisionAnalysis => Type == ImageIntentType.VisionAnalysis;

    /// <summary>The file ID of the source image for editing/reference resolution.</summary>
    public Guid? ReferencedFileId { get; init; }

    /// <summary>Pre-resolved prompt, if the classifier could determine it without an LLM call.</summary>
    public string? ResolvedPrompt { get; init; }

    /// <summary>Message to show the user when clarification is needed.</summary>
    public string? ClarificationQuestion { get; init; }

    /// <summary>Classification confidence in [0, 1].</summary>
    public double Confidence { get; init; }

    /// <summary>Human-readable reason for the classification (used in logs).</summary>
    public string? Reason { get; init; }

    public static ImageIntentResult TextChat(double confidence = 1.0, string? reason = null) =>
        new() { Type = ImageIntentType.TextChat, Confidence = confidence, Reason = reason };

    public static ImageIntentResult VisionAnalysis(Guid? referencedFileId = null, double confidence = 0.95, string? reason = null) =>
        new() { Type = ImageIntentType.VisionAnalysis, ReferencedFileId = referencedFileId, Confidence = confidence, Reason = reason };

    public static ImageIntentResult TextToImage(string? resolvedPrompt = null, double confidence = 0.9, string? reason = null) =>
        new() { Type = ImageIntentType.TextToImage, ResolvedPrompt = resolvedPrompt, Confidence = confidence, Reason = reason };

    public static ImageIntentResult ImageEdit(Guid sourceFileId, string? resolvedPrompt = null, double confidence = 0.9, string? reason = null) =>
        new() { Type = ImageIntentType.ImageEdit, ReferencedFileId = sourceFileId, ResolvedPrompt = resolvedPrompt, Confidence = confidence, Reason = reason };

    public static ImageIntentResult Clarification(string question, string? reason = null) =>
        new() { Type = ImageIntentType.ClarificationRequired, ClarificationQuestion = question, Confidence = 1.0, Reason = reason };
}
