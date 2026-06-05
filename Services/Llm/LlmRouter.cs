using InfinityAI.Data;
using InfinityAI.Models.Database;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Gateway;
using InfinityAI.Api.Models.ImageGeneration;
using InfinityAI.Api.Models.Llm;
using InfinityAI.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace InfinityAI.Api.Services.Llm;

public sealed class LlmRouter : ILlmRouter
{
    private readonly IModelSelectionService _modelSelectionService;
    private readonly IAIChatService _chatService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IImageGenerationService _imageGenerationService;
    private readonly IAiRequestLogService _aiRequestLogService;
    private readonly ImageGenerationOptions _imageGenerationOptions;
    private readonly ApplicationDbContext _db;
    private readonly ILogger<LlmRouter> _logger;

    public LlmRouter(
        IModelSelectionService modelSelectionService,
        IAIChatService chatService,
        IEmbeddingService embeddingService,
        IImageGenerationService imageGenerationService,
        IAiRequestLogService aiRequestLogService,
        IOptions<ImageGenerationOptions> imageGenerationOptions,
        ApplicationDbContext db,
        ILogger<LlmRouter> logger)
    {
        _modelSelectionService = modelSelectionService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _imageGenerationService = imageGenerationService;
        _aiRequestLogService = aiRequestLogService;
        _imageGenerationOptions = imageGenerationOptions.Value;
        _db = db;
        _logger = logger;
    }

    public async Task<string> SendChatAsync(
        Guid userId,
        List<ChatMessage> messages,
        List<ApplicationFile> imageFiles,
        CancellationToken ct,
        string? correlationId = null)
    {
        var capability = imageFiles.Count > 0
            ? LlmCapability.Vision
            : LlmCapability.Chat;

        var selection = await SelectModelAsync(capability, userId, ct);

        _logger.LogWarning(
            "LLM router selected {Capability} model {Provider}/{Model} for UserId={UserId}",
            capability,
            selection.ProviderType,
            selection.ModelId,
            userId);

        var requestId = correlationId ?? Guid.NewGuid().ToString();

        var baseEntry = new AiRequestLogEntry
        {
            RequestId = requestId,
            UserId = userId.ToString(),
            Provider = selection.ProviderType,
            Model = selection.ModelId,
            Capability = capability,
            LlmModelId = selection.Model.Id,
            AiProfileId = selection.Profile.Id,
            SelectionScore = selection.SelectionScore,
            Pricing = BuildPricingContext(selection.Model),
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            RequestPreview = ExtractLastUserText(messages)
        };

        var stopwatch = Stopwatch.StartNew();
        InfinityAI.Api.Models.Gateway.GatewayExecutionResult result;

        try
        {
            result = await _chatService.SendAsync(messages, imageFiles, selection.ProviderType, selection.ModelId, ct);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            await _aiRequestLogService.LogAsync(baseEntry with
            {
                DurationMs = stopwatch.ElapsedMilliseconds,
                Success = false,
                StatusCode = 500,
                ErrorMessage = ex.Message
            }, CancellationToken.None);

            throw;
        }

        stopwatch.Stop();

        var promptTokens      = result.Usage?.PromptTokens      ?? 0;
        var completionTokens  = result.Usage?.CompletionTokens  ?? 0;
        var totalTokens       = result.Usage?.TotalTokens        ?? 0;

        _logger.LogWarning(
            "DIAG [F] LlmRouter pre-log: RequestId={RequestId} TokensNull={TokensNull} Prompt={Prompt} Completion={Completion} Total={Total}",
            requestId,
            result.Usage is null,
            promptTokens,
            completionTokens,
            totalTokens);

        await _aiRequestLogService.LogAsync(baseEntry with
        {
            DurationMs        = stopwatch.ElapsedMilliseconds,
            Success           = !result.IsError,
            StatusCode        = result.IsError ? (result.ProviderStatusCode ?? 500) : 200,
            ResponsePreview   = Truncate(result.Content, 1000),
            PromptTokens      = promptTokens,
            CompletionTokens  = completionTokens,
            TotalTokens       = totalTokens,
            ErrorMessage      = result.IsError ? result.Content : null,
            ProviderStatusCode = result.ProviderStatusCode,
            ProviderErrorCode  = result.ErrorCode
        }, CancellationToken.None);

        if (result.IsError)
            throw new HttpRequestException(
                result.Content,
                inner: null,
                statusCode: (System.Net.HttpStatusCode)(result.ProviderStatusCode ?? 502));

        return result.Content;
    }

    public async Task<float[]> CreateEmbeddingAsync(
        string input,
        CancellationToken ct)
    {
        var selection = await SelectModelAsync(LlmCapability.Embeddings, userId: null, ct);

        _logger.LogInformation(
            "LLM router selected embedding model {Provider}/{Model}",
            selection.ProviderType,
            selection.ModelId);

        var baseEntry = new AiRequestLogEntry
        {
            RequestId = Guid.NewGuid().ToString(),
            Provider = selection.ProviderType,
            Model = selection.ModelId,
            Capability = LlmCapability.Embeddings,
            LlmModelId = selection.Model.Id,
            AiProfileId = selection.Profile.Id,
            SelectionScore = selection.SelectionScore,
            Pricing = BuildPricingContext(selection.Model),
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            RequestPreview = Truncate(input, 1000)
        };

        var result = await ExecuteAndLogAsync(
            baseEntry,
            () => _embeddingService.CreateEmbeddingAsync(input, selection.ProviderType, selection.ModelId, ct),
            r => $"[{r.Embedding.Length} dimensions]",
            r => (r.InputTokens, 0, r.InputTokens));

        return result.Embedding;
    }

    public async Task<float[]> CreateEmbeddingWithModelAsync(
        string input,
        string providerType,
        string modelId,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "LLM router creating embedding with explicit model. Provider={Provider}, Model={Model}",
            providerType,
            modelId);

        // Resolve the LlmModel for pricing/logging.  During batch indexing this method is
        // called sequentially (EmbeddingBatchService guarantees sequential execution), so
        // the single DbContext is never accessed concurrently.  The cache key is
        // (providerType, modelId) — constant for every chunk in a batch — so we only hit
        // the database once per unique model across the lifetime of this LlmRouter instance.
        var llmModel = await GetCachedLlmModelAsync(providerType, modelId, ct);

        var baseEntry = new AiRequestLogEntry
        {
            RequestId = Guid.NewGuid().ToString(),
            Provider = providerType,
            Model = modelId,
            Capability = LlmCapability.Embeddings,
            LlmModelId = llmModel?.Id,
            AiProfileId = null,
            SelectionScore = 0,
            Pricing = llmModel is null ? new LlmPricingContext() : BuildPricingContext(llmModel),
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            RequestPreview = Truncate(input, 1000)
        };

        var result = await ExecuteAndLogAsync(
            baseEntry,
            () => _embeddingService.CreateEmbeddingAsync(input, providerType, modelId, ct),
            r => $"[{r.Embedding.Length} dimensions]",
            r => (r.InputTokens, 0, r.InputTokens));

        return result.Embedding;
    }

    // Per-instance cache so that batch-embedding jobs don't issue N identical DB queries.
    // Key: (providerType, modelId). LlmRouter is scoped, so this lives for one request/job.
    private readonly Dictionary<(string, string), LlmModel?> _llmModelCache = [];

    private async Task<LlmModel?> GetCachedLlmModelAsync(
        string providerType,
        string modelId,
        CancellationToken ct)
    {
        var key = (providerType, modelId);
        if (_llmModelCache.TryGetValue(key, out var cached))
            return cached;

        var model = await _db.LlmModels
            .AsNoTracking()
            .Include(x => x.LlmProvider)
            .FirstOrDefaultAsync(
                x => x.ModelId == modelId &&
                     x.LlmProvider != null &&
                     x.LlmProvider.ProviderType == providerType,
                ct);

        _llmModelCache[key] = model;
        return model;
    }

    public async Task<ApplicationFile> GenerateImageAsync(
        Guid userId,
        Guid conversationId,
        string prompt,
        CancellationToken ct)
    {
        var selection = await SelectModelAsync(LlmCapability.ImageGeneration, userId, ct);

        _logger.LogInformation(
            "[IMAGEGEN] Router selected primary model. Provider={Provider}, Model={Model}, UserId={UserId}",
            selection.ProviderType, selection.ModelId, userId);

        var result = await TryGenerateWithLoggingAsync(
            userId, conversationId, prompt, selection, ct);

        if (result.Success)
            return result.File!;

        // Provider fallback: on moderation block try a different provider
        if (result.FailureType == ImageGenerationFailureType.ModerationBlocked &&
            _imageGenerationOptions.EnableProviderFallback)
        {
            _logger.LogWarning(
                "[IMAGEGEN] Primary provider returned ModerationBlocked — attempting provider fallback. ExcludedProvider={ExcludedProvider}",
                selection.ProviderType);

            var fallbackSelection = await _modelSelectionService.SelectBestModelExcludingProviderAsync(
                LlmCapability.ImageGeneration,
                selection.ProviderType,
                profileId: null,
                ct);

            if (fallbackSelection is not null)
            {
                _logger.LogInformation(
                    "[IMAGEGEN] Fallback provider selected. Provider={Provider}, Model={Model}",
                    fallbackSelection.Model.LlmProvider!.ProviderType, fallbackSelection.Model.ModelId);

                var fallbackResult = await TryGenerateWithLoggingAsync(
                    userId, conversationId, prompt,
                    new SelectedLlmModel(
                        fallbackSelection.Model.LlmProvider!.ProviderType,
                        fallbackSelection.Model.ModelId,
                        fallbackSelection.Model,
                        fallbackSelection.Profile,
                        fallbackSelection.SelectionScore),
                    ct);

                if (fallbackResult.Success)
                    return fallbackResult.File!;

                throw new InvalidOperationException(
                    ImageGenerationFailureClassifier.ToUserMessage(fallbackResult.FailureType));
            }

            _logger.LogWarning("[IMAGEGEN] No fallback provider available — returning moderation error.");
        }

        throw new InvalidOperationException(
            ImageGenerationFailureClassifier.ToUserMessage(result.FailureType));
    }

    private async Task<ImageGenerationAttemptResult> TryGenerateWithLoggingAsync(
        Guid userId,
        Guid conversationId,
        string prompt,
        SelectedLlmModel selection,
        CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString();
        var pricing = BuildPricingContext(selection.Model);
        var stopwatch = Stopwatch.StartNew();

        var result = await _imageGenerationService.TryGenerateAsync(
            userId, conversationId, prompt, selection.ProviderType, selection.ModelId, ct);

        stopwatch.Stop();

        await _aiRequestLogService.LogAsync(new AiRequestLogEntry
        {
            RequestId = requestId,
            UserId = userId.ToString(),
            Provider = selection.ProviderType,
            Model = selection.ModelId,
            Capability = LlmCapability.ImageGeneration,
            LlmModelId = selection.Model.Id,
            AiProfileId = selection.Profile.Id,
            SelectionScore = selection.SelectionScore,
            Pricing = pricing,
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            DurationMs = stopwatch.ElapsedMilliseconds,
            Success = result.Success,
            StatusCode = result.Success ? 200 : result.ProviderStatusCode ?? 500,
            ErrorMessage = result.Success ? null : result.ProviderErrorMessage ?? result.UserMessage,
            RequestPreview = Truncate(prompt, 1000),
            ResponsePreview = result.Success ? "[generated image]" : null,
            ImageFailureType = result.Success ? null : result.FailureType,
            ProviderStatusCode = result.ProviderStatusCode,
            ProviderErrorCode = result.ProviderErrorCode,
            ProviderErrorMessage = result.ProviderErrorMessage,
            RetryCount = result.AttemptCount > 1 ? result.AttemptCount - 1 : 0
        }, CancellationToken.None);

        return result;
    }

    private async Task<TResult> ExecuteAndLogAsync<TResult>(
        AiRequestLogEntry baseEntry,
        Func<Task<TResult>> execute,
        Func<TResult, string?> getResponsePreview,
        Func<TResult, (int PromptTokens, int CompletionTokens, int TotalTokens)>? getTokenCounts = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await execute();

            stopwatch.Stop();

            var tokens = getTokenCounts?.Invoke(result);

            var finalPrompt = tokens?.PromptTokens ?? baseEntry.PromptTokens;
            var finalCompletion = tokens?.CompletionTokens ?? baseEntry.CompletionTokens;
            var finalTotal = tokens?.TotalTokens ?? baseEntry.TotalTokens;

            _logger.LogWarning(
                "DIAG [F] LlmRouter pre-log: RequestId={RequestId} TokensNull={TokensNull} Prompt={Prompt} Completion={Completion} Total={Total}",
                baseEntry.RequestId,
                tokens is null,
                finalPrompt,
                finalCompletion,
                finalTotal);

            await _aiRequestLogService.LogAsync(baseEntry with
            {
                DurationMs = stopwatch.ElapsedMilliseconds,
                Success = true,
                StatusCode = 200,
                ResponsePreview = getResponsePreview(result),
                PromptTokens = finalPrompt,
                CompletionTokens = finalCompletion,
                TotalTokens = finalTotal
            }, CancellationToken.None);

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            await _aiRequestLogService.LogAsync(baseEntry with
            {
                DurationMs = stopwatch.ElapsedMilliseconds,
                Success = false,
                StatusCode = 500,
                ErrorMessage = ex.Message
            }, CancellationToken.None);

            throw;
        }
    }

    private async Task<SelectedLlmModel> SelectModelAsync(
        LlmCapability capability,
        Guid? userId,
        CancellationToken ct)
    {
        var profileId = await ResolveProfileIdAsync(userId, ct);
        var profileName = await ResolveProfileNameAsync(profileId, ct);

        _logger.LogWarning(
            "LLM router selecting model. Capability={Capability}, UserId={UserId}, ProfileId={ProfileId}, Profile={Profile}",
            capability,
            userId,
            profileId,
            profileName ?? "none");

        var result = await _modelSelectionService.SelectBestModelAsync(capability, profileId, ct);

        var model = result.Model;

        var providerType =
            model.LlmProvider?.ProviderType
            ?? throw new InvalidOperationException(
                $"Provider missing for model '{model.ModelId}'.");

        if (string.IsNullOrWhiteSpace(model.ModelId))
            throw new InvalidOperationException("Selected model has no ModelId.");

        return new SelectedLlmModel(
            providerType,
            model.ModelId,
            model,
            result.Profile,
            result.SelectionScore);
    }

    private async Task<Guid?> ResolveProfileIdAsync(Guid? userId, CancellationToken ct)
    {
        if (userId.HasValue)
        {
            var userProfileId = await _db.Users
                .AsNoTracking()
                .Where(x => x.Id == userId.Value)
                .Select(x => x.AiProfileId)
                .FirstOrDefaultAsync(ct);

            if (userProfileId.HasValue)
                return userProfileId;
        }

        return await _db.AiSettings
            .AsNoTracking()
            .Select(x => x.ActiveAiProfileId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<string?> ResolveProfileNameAsync(Guid? profileId, CancellationToken ct)
    {
        if (!profileId.HasValue)
            return null;

        return await _db.AiProfiles
            .AsNoTracking()
            .Where(x => x.Id == profileId.Value)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(ct);
    }

    private static LlmPricingContext BuildPricingContext(LlmModel model) =>
        new()
        {
            InputCostPer1M = model.InputCostPer1M,
            OutputCostPer1M = model.OutputCostPer1M,
            EmbeddingCostPer1M = model.EmbeddingCostPer1M,
            ImageCostPerGeneration = model.ImageCostPerGeneration
        };

    private static string? ExtractLastUserText(List<ChatMessage> messages)
    {
        var last = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase));

        return Truncate(last?.Content, 1000);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private sealed record SelectedLlmModel(
        string ProviderType,
        string ModelId,
        LlmModel Model,
        AiProfile Profile,
        int SelectionScore);
}
