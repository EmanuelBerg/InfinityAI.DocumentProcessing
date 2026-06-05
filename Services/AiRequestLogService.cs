using InfinityAI.Data;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Llm;

namespace InfinityAI.Api.Services;

public sealed class AiRequestLogService : IAiRequestLogService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<AiRequestLogService> _logger;

    public AiRequestLogService(
        ApplicationDbContext db,
        ILogger<AiRequestLogService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(AiRequestLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (entry.Capability == LlmCapability.ImageGeneration)
                _logger.LogInformation(
                    "ImageGen cost inputs: RequestId={RequestId}, PricingNull={PricingNull}, ImageCostPerGeneration={ImageCostPerGeneration}",
                    entry.RequestId,
                    entry.Pricing is null,
                    entry.Pricing?.ImageCostPerGeneration);

            var cost = CalculateCost(entry);

            var log = new AiRequestLog
            {
                RequestId = entry.RequestId,
                TimestampUtc = DateTime.UtcNow,
                Caller = "InfinityAI.Api",
                UserId = entry.UserId,
                Provider = entry.Provider,
                Model = entry.Model,
                LlmModelId = entry.LlmModelId,
                AiProfileId = entry.AiProfileId,
                Capability = entry.Capability,
                SelectionScore = entry.SelectionScore,
                PromptTokens = entry.PromptTokens,
                CompletionTokens = entry.CompletionTokens,
                TotalTokens = entry.TotalTokens,
                EstimatedCostUsd = cost,
                DurationMs = entry.DurationMs,
                Success = entry.Success,
                StatusCode = entry.StatusCode,
                ErrorMessage = entry.ErrorMessage,
                RequestPreview = entry.RequestPreview,
                ResponsePreview = entry.ResponsePreview,
                FailureType = entry.ImageFailureType?.ToString(),
                ProviderStatusCode = entry.ProviderStatusCode,
                ProviderErrorCode = entry.ProviderErrorCode,
                ProviderErrorMessage = entry.ProviderErrorMessage,
                RetryCount = entry.RetryCount > 0 ? entry.RetryCount : null
            };

            _db.AiRequestLogs.Add(log);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "AI request logged. RequestId={RequestId}, Provider={Provider}, Model={Model}, Capability={Capability}, Success={Success}, DurationMs={DurationMs}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, CostUsd={CostUsd}",
                log.RequestId,
                log.Provider,
                log.Model,
                log.Capability,
                log.Success,
                log.DurationMs,
                log.PromptTokens,
                log.CompletionTokens,
                log.EstimatedCostUsd);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to write AI request log. RequestId={RequestId}, Provider={Provider}, Model={Model}",
                entry.RequestId,
                entry.Provider,
                entry.Model);
        }
    }

    private static decimal CalculateCost(AiRequestLogEntry entry)
    {
        var pricing = entry.Pricing;

        if (pricing is null)
            return 0m;

        return entry.Capability switch
        {
            LlmCapability.Embeddings =>
                entry.PromptTokens / 1_000_000m * (pricing.EmbeddingCostPer1M ?? 0m),

            LlmCapability.ImageGeneration =>
                pricing.ImageCostPerGeneration ?? 0m,

            _ =>
                entry.PromptTokens / 1_000_000m * (pricing.InputCostPer1M ?? 0m) +
                entry.CompletionTokens / 1_000_000m * (pricing.OutputCostPer1M ?? 0m)
        };
    }
}
