using InfinityAI.Api.Models.ImageGeneration;
using InfinityAI.Api.Models.Llm;

namespace InfinityAI.Api.Services;

public interface IAiRequestLogService
{
    Task LogAsync(AiRequestLogEntry entry, CancellationToken ct = default);
}

public sealed record AiRequestLogEntry
{
    public required string RequestId { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required LlmCapability Capability { get; init; }
    public string? UserId { get; init; }
    public Guid? LlmModelId { get; init; }
    public Guid? AiProfileId { get; init; }
    public int? SelectionScore { get; init; }
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? RequestPreview { get; init; }
    public string? ResponsePreview { get; init; }
    public LlmPricingContext? Pricing { get; init; }

    // Image generation diagnostics
    public ImageGenerationFailureType? ImageFailureType { get; init; }
    public int? ProviderStatusCode { get; init; }
    public string? ProviderErrorCode { get; init; }
    public string? ProviderErrorMessage { get; init; }
    public int RetryCount { get; init; }
}
