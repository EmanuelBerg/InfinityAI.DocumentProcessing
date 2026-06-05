namespace InfinityAI.Api.Models.ImageGeneration;

public enum ImageGenerationFailureType
{
    None = 0,
    ModerationBlocked = 1,
    InvalidPrompt = 2,
    AuthenticationFailure = 3,
    QuotaExceeded = 4,
    RateLimited = 5,
    ProviderUnavailable = 6,
    Timeout = 7,
    Unknown = 8
}
