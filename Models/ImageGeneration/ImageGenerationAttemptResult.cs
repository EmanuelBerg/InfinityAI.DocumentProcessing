using InfinityAI.Api.Models.Database;

namespace InfinityAI.Api.Models.ImageGeneration;

public sealed class ImageGenerationAttemptResult
{
    public bool Success { get; init; }
    public ApplicationFile? File { get; init; }

    public ImageGenerationFailureType FailureType { get; init; }
    public int? ProviderStatusCode { get; init; }
    public string? ProviderErrorCode { get; init; }
    public string? ProviderErrorMessage { get; init; }
    public string? UserMessage { get; init; }

    public int AttemptCount { get; init; }

    public static ImageGenerationAttemptResult Succeeded(ApplicationFile file, int attemptCount) =>
        new() { Success = true, File = file, FailureType = ImageGenerationFailureType.None, AttemptCount = attemptCount };

    public static ImageGenerationAttemptResult Failed(
        ImageGenerationFailureType failureType,
        string userMessage,
        int attemptCount,
        int? providerStatusCode = null,
        string? providerErrorCode = null,
        string? providerErrorMessage = null) =>
        new()
        {
            Success = false,
            FailureType = failureType,
            UserMessage = userMessage,
            AttemptCount = attemptCount,
            ProviderStatusCode = providerStatusCode,
            ProviderErrorCode = providerErrorCode,
            ProviderErrorMessage = providerErrorMessage
        };
}
