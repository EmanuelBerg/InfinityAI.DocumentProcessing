using InfinityAI.Api.Models.ImageGeneration;

namespace InfinityAI.Api.Services;

public interface IImageGenerationService
{
    Task<ImageGenerationAttemptResult> TryGenerateAsync(
        Guid userId,
        Guid conversationId,
        string prompt,
        string providerType,
        string modelId,
        CancellationToken ct);
}
