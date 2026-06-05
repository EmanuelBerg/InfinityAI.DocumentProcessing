using InfinityAI.Api.Models.ImageGeneration;
using System.Text.Json;

namespace InfinityAI.Api.Services;

public static class ImageGenerationFailureClassifier
{
    public static (ImageGenerationFailureType FailureType, string? ErrorCode, string? ErrorMessage) Classify(
        int statusCode,
        string body,
        string providerType)
    {
        var (errorCode, errorMessage) = ExtractErrorFields(body);

        if (statusCode == 429)
        {
            var isQuota =
                errorCode is "insufficient_quota" or "quota_exceeded" ||
                (errorMessage?.Contains("quota", StringComparison.OrdinalIgnoreCase) == true);

            return isQuota
                ? (ImageGenerationFailureType.QuotaExceeded, errorCode, errorMessage)
                : (ImageGenerationFailureType.RateLimited, errorCode, errorMessage);
        }

        if (statusCode == 401 || statusCode == 403)
            return (ImageGenerationFailureType.AuthenticationFailure, errorCode, errorMessage);

        if (statusCode == 408)
            return (ImageGenerationFailureType.Timeout, errorCode, errorMessage);

        if (statusCode == 400)
        {
            if (IsModerationBlocked(errorCode, errorMessage, body, providerType))
                return (ImageGenerationFailureType.ModerationBlocked, errorCode, errorMessage);

            if (IsInvalidPrompt(errorCode, errorMessage))
                return (ImageGenerationFailureType.InvalidPrompt, errorCode, errorMessage);

            return (ImageGenerationFailureType.Unknown, errorCode, errorMessage);
        }

        if (statusCode is 500 or 502 or 503 or 504)
            return (ImageGenerationFailureType.ProviderUnavailable, errorCode, errorMessage);

        return (ImageGenerationFailureType.Unknown, errorCode, errorMessage);
    }

    public static bool IsRetryable(ImageGenerationFailureType failureType) =>
        failureType is ImageGenerationFailureType.ProviderUnavailable
            or ImageGenerationFailureType.RateLimited
            or ImageGenerationFailureType.Timeout;

    public static string ToUserMessage(ImageGenerationFailureType failureType) => failureType switch
    {
        ImageGenerationFailureType.ModerationBlocked =>
            "Din bildprompt innehåller innehåll som inte är tillåtet. Justera prompten och försök igen.",
        ImageGenerationFailureType.InvalidPrompt =>
            "Bildprompten kunde inte bearbetas. Kontrollera prompten och försök igen.",
        ImageGenerationFailureType.AuthenticationFailure =>
            "Bildgenereringen misslyckades på grund av ett autentiseringsfel. Kontakta administratören.",
        ImageGenerationFailureType.QuotaExceeded =>
            "Bildgenerering är tillfälligt inte tillgänglig eftersom leverantörens kvot är uppnådd. Försök igen senare.",
        ImageGenerationFailureType.RateLimited =>
            "För många bildgenereringsförfrågningar. Vänta en stund och försök igen.",
        ImageGenerationFailureType.ProviderUnavailable =>
            "Bildgenererings­tjänsten är tillfälligt otillgänglig. Försök igen om en stund.",
        ImageGenerationFailureType.Timeout =>
            "Bildgenereringen tog för lång tid. Försök igen om en stund eller prova en annan bildmodell.",
        _ =>
            "Bildgenereringen misslyckades. Försök igen om en stund."
    };

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsModerationBlocked(
        string? errorCode,
        string? errorMessage,
        string body,
        string providerType)
    {
        // OpenAI: code = "moderation_blocked" or "content_policy_violation"
        if (errorCode is "moderation_blocked" or "content_policy_violation")
            return true;

        // Gemini: safety-filter errors surfaced in message text
        if (string.Equals(providerType, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            if (errorMessage?.Contains("safety", StringComparison.OrdinalIgnoreCase) == true ||
                body.Contains("SAFETY", StringComparison.Ordinal) ||
                body.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Generic: message mentions moderation/policy
        if (errorMessage?.Contains("moderation", StringComparison.OrdinalIgnoreCase) == true ||
            errorMessage?.Contains("content policy", StringComparison.OrdinalIgnoreCase) == true ||
            errorMessage?.Contains("safety system", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return false;
    }

    private static bool IsInvalidPrompt(string? errorCode, string? errorMessage) =>
        errorCode is "invalid_prompt" or "prompt_too_long" ||
        errorMessage?.Contains("invalid prompt", StringComparison.OrdinalIgnoreCase) == true;

    private static (string? Code, string? Message) ExtractErrorFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Gateway wraps provider error: { "detail": { "error": { "code": ..., "message": ... } } }
            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    if (detail.TryGetProperty("error", out var detailError))
                    {
                        var code = detailError.TryGetProperty("code", out var c) ? c.GetString() : null;
                        var msg = detailError.TryGetProperty("message", out var m) ? m.GetString() : null;
                        return (code, msg);
                    }
                }

                if (detail.ValueKind == JsonValueKind.String)
                    return (null, detail.GetString());
            }

            // Direct error object: { "error": { "code": ..., "message": ... } }
            if (root.TryGetProperty("error", out var rootError) &&
                rootError.ValueKind == JsonValueKind.Object)
            {
                var code = rootError.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = rootError.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }

            return (null, null);
        }
        catch
        {
            return (null, null);
        }
    }
}
