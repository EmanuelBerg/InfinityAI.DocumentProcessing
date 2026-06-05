using InfinityAI.Data;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Llm;
using Microsoft.EntityFrameworkCore;

namespace InfinityAI.Api.Services;

public sealed class ModelSelectionService : IModelSelectionService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<ModelSelectionService> _logger;

    public ModelSelectionService(
        ApplicationDbContext db,
        ILogger<ModelSelectionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ModelSelectionResult> SelectBestModelAsync(
        LlmCapability capability,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "AI model selection started. Capability={Capability}, RequestedProfileId={RequestedProfileId}",
            capability,
            profileId);

        var profile = await ResolveProfileAsync(profileId, ct);

        _logger.LogWarning(
            "AI model selection profile resolved. ProfileId={ProfileId}, Profile={Profile}, CostWeight={CostWeight}, SpeedWeight={SpeedWeight}, QualityWeight={QualityWeight}, IsDefault={IsDefault}",
            profile.Id,
            profile.Name,
            profile.CostWeight,
            profile.SpeedWeight,
            profile.QualityWeight,
            profile.IsDefault);

        var rawMetrics = await _db.LlmModelCapabilityMetrics
            .Include(x => x.LlmModel)
                .ThenInclude(x => x.LlmProvider)
            .Where(x => x.Capability == capability)
            .ToListAsync(ct);

        _logger.LogWarning(
            "AI model selection metrics loaded. Capability={Capability}, RawMetricCount={RawMetricCount}",
            capability,
            rawMetrics.Count);

        foreach (var metric in rawMetrics
                     .OrderBy(x => x.LlmModel?.LlmProvider?.SortOrder ?? int.MaxValue)
                     .ThenBy(x => x.LlmModel?.SortOrder ?? int.MaxValue)
                     .ThenBy(x => x.LlmModel?.DisplayName))
        {
            var model = metric.LlmModel;
            var provider = model?.LlmProvider;

            var isCandidate =
                metric.IsEnabled &&
                model is not null &&
                model.IsEnabled &&
                provider is not null &&
                provider.IsEnabled;

            _logger.LogWarning(
                "AI model metric inspected. IsCandidate={IsCandidate}, ExclusionReason={ExclusionReason}, Capability={Capability}, MetricId={MetricId}, MetricEnabled={MetricEnabled}, ProviderId={ProviderId}, Provider={Provider}, ProviderType={ProviderType}, ProviderEnabled={ProviderEnabled}, ProviderSortOrder={ProviderSortOrder}, ModelDbId={ModelDbId}, Model={Model}, ModelId={ModelId}, ModelEnabled={ModelEnabled}, ModelSortOrder={ModelSortOrder}, CostScore={CostScore}, SpeedScore={SpeedScore}, QualityScore={QualityScore}",
                isCandidate,
                GetExclusionReason(metric),
                capability,
                metric.Id,
                metric.IsEnabled,
                provider?.Id,
                provider?.Name,
                provider?.ProviderType,
                provider?.IsEnabled,
                provider?.SortOrder,
                model?.Id,
                model?.DisplayName,
                model?.ModelId,
                model?.IsEnabled,
                model?.SortOrder,
                metric.CostScore,
                metric.SpeedScore,
                metric.QualityScore);
        }

        var candidates = rawMetrics
            .Where(x =>
                x.IsEnabled &&
                x.LlmModel.IsEnabled &&
                x.LlmModel.LlmProvider != null &&
                x.LlmModel.LlmProvider.IsEnabled)
            .ToList();

        _logger.LogWarning(
            "AI model selection candidates filtered. Capability={Capability}, CandidateCount={CandidateCount}, RawMetricCount={RawMetricCount}",
            capability,
            candidates.Count,
            rawMetrics.Count);

        // Apply profile provider restriction if the profile defines allowed providers.
        // Profile-independent capabilities (Embeddings, Audio) are infrastructure-level:
        // they must succeed globally regardless of which chat profile is active.
        // Skipping the filter here means PDF indexing and RAG retrieval work even when
        // the active profile (e.g. Anthropic) has no embedding provider.
        var allowedProviderIds = await _db.AiProfileProviders
            .Where(x => x.AiProfileId == profile.Id)
            .Select(x => x.LlmProviderId)
            .ToListAsync(ct);

        var profileFilterBypassed = IsProfileIndependentCapability(capability);

        if (profileFilterBypassed && allowedProviderIds.Count > 0)
        {
            _logger.LogInformation(
                "AI profile provider filter bypassed. Capability={Capability} is profile-independent — " +
                "profile '{Profile}' defines {ProviderCount} provider restriction(s) but they do not " +
                "apply to infrastructure capabilities. All globally enabled providers remain eligible.",
                capability,
                profile.Name,
                allowedProviderIds.Count);
        }
        else if (!profileFilterBypassed && allowedProviderIds.Count > 0)
        {
            var beforeCount = candidates.Count;

            candidates = candidates
                .Where(x => allowedProviderIds.Contains(x.LlmModel.LlmProvider!.Id))
                .ToList();

            var allowedProviderTypes = candidates
                .Select(x => x.LlmModel.LlmProvider!.ProviderType)
                .Distinct()
                .ToList();

            _logger.LogWarning(
                "AI profile provider filter applied. Profile={Profile}, AllowedProviders={AllowedProviders}, CandidatesBefore={Before}, CandidatesAfter={After}",
                profile.Name,
                string.Join(", ", allowedProviderTypes),
                beforeCount,
                candidates.Count);
        }
        else
        {
            _logger.LogWarning(
                "AI profile has no provider restrictions. Profile={Profile}, Candidates={Candidates}",
                profile.Name,
                candidates.Count);
        }

        if (candidates.Count == 0)
        {
            var filterWasActive = !profileFilterBypassed && allowedProviderIds.Count > 0;

            var providerRestriction = filterWasActive
                ? $" Profile '{profile.Name}' is restricted to {allowedProviderIds.Count} provider(s) but none have enabled {capability} models."
                : "";

            _logger.LogError(
                "AI model selection failed. No enabled candidates found. Capability={Capability}, ProfileId={ProfileId}, Profile={Profile}, RawMetricCount={RawMetricCount}, ProviderFilterActive={ProviderFilterActive}, ProfileFilterBypassed={ProfileFilterBypassed}",
                capability,
                profile.Id,
                profile.Name,
                rawMetrics.Count,
                filterWasActive,
                profileFilterBypassed);

            throw new InvalidOperationException(
                $"No enabled model found for capability '{capability}'.{providerRestriction}");
        }

        var scoredCandidates = candidates
            .Select(x =>
            {
                var weightedCost = x.CostScore * profile.CostWeight;
                var weightedSpeed = x.SpeedScore * profile.SpeedWeight;
                var weightedQuality = x.QualityScore * profile.QualityWeight;
                var totalScore = weightedCost + weightedSpeed + weightedQuality;

                return new ModelCandidateScore
                {
                    Metric = x,
                    WeightedCost = weightedCost,
                    WeightedSpeed = weightedSpeed,
                    WeightedQuality = weightedQuality,
                    Score = totalScore
                };
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Metric.LlmModel.SortOrder)
            .ThenBy(x => x.Metric.LlmModel.LlmProvider.SortOrder)
            .ToList();

        foreach (var candidate in scoredCandidates)
        {
            var metric = candidate.Metric;
            var model = metric.LlmModel;
            var provider = model.LlmProvider;

            _logger.LogWarning(
                "AI model candidate scored. Rank={Rank}, Profile={Profile}, Capability={Capability}, ProviderId={ProviderId}, Provider={Provider}, ProviderType={ProviderType}, ProviderSortOrder={ProviderSortOrder}, ModelDbId={ModelDbId}, Model={Model}, ModelId={ModelId}, ModelSortOrder={ModelSortOrder}, CostScore={CostScore}, SpeedScore={SpeedScore}, QualityScore={QualityScore}, CostWeight={CostWeight}, SpeedWeight={SpeedWeight}, QualityWeight={QualityWeight}, WeightedCost={WeightedCost}, WeightedSpeed={WeightedSpeed}, WeightedQuality={WeightedQuality}, TotalScore={TotalScore}, NormalizedScore={NormalizedScore}",
                scoredCandidates.IndexOf(candidate) + 1,
                profile.Name,
                capability,
                provider.Id,
                provider.Name,
                provider.ProviderType,
                provider.SortOrder,
                model.Id,
                model.DisplayName,
                model.ModelId,
                model.SortOrder,
                metric.CostScore,
                metric.SpeedScore,
                metric.QualityScore,
                profile.CostWeight,
                profile.SpeedWeight,
                profile.QualityWeight,
                candidate.WeightedCost,
                candidate.WeightedSpeed,
                candidate.WeightedQuality,
                candidate.Score,
                candidate.Score / 100.0);
        }

        var best = scoredCandidates.First();

        _logger.LogWarning(
            "AI model selected. ProfileId={ProfileId}, Profile={Profile}, Capability={Capability}, ProviderId={ProviderId}, Provider={Provider}, ProviderType={ProviderType}, ModelDbId={ModelDbId}, Model={Model}, ModelId={ModelId}, Score={Score}, NormalizedScore={NormalizedScore}",
            profile.Id,
            profile.Name,
            capability,
            best.Metric.LlmModel.LlmProvider.Id,
            best.Metric.LlmModel.LlmProvider.Name,
            best.Metric.LlmModel.LlmProvider.ProviderType,
            best.Metric.LlmModel.Id,
            best.Metric.LlmModel.DisplayName,
            best.Metric.LlmModel.ModelId,
            best.Score,
            best.Score / 100.0);

        return new ModelSelectionResult
        {
            Model = best.Metric.LlmModel,
            Profile = profile,
            SelectionScore = best.Score
        };
    }

    public async Task<ModelSelectionResult?> SelectBestModelExcludingProviderAsync(
        LlmCapability capability,
        string excludedProviderType,
        Guid? profileId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "AI model selection (excluding provider) started. Capability={Capability}, ExcludedProvider={ExcludedProvider}",
            capability,
            excludedProviderType);

        var profile = await ResolveProfileAsync(profileId, ct);

        var candidates = await _db.LlmModelCapabilityMetrics
            .Include(x => x.LlmModel)
                .ThenInclude(x => x.LlmProvider)
            .Where(x =>
                x.Capability == capability &&
                x.IsEnabled &&
                x.LlmModel.IsEnabled &&
                x.LlmModel.LlmProvider != null &&
                x.LlmModel.LlmProvider.IsEnabled &&
                x.LlmModel.LlmProvider.ProviderType != excludedProviderType)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogWarning(
                "AI model selection (excluding provider) found no candidates. Capability={Capability}, ExcludedProvider={ExcludedProvider}",
                capability,
                excludedProviderType);

            return null;
        }

        var best = candidates
            .Select(x => new
            {
                Metric = x,
                Score = x.CostScore * profile.CostWeight +
                        x.SpeedScore * profile.SpeedWeight +
                        x.QualityScore * profile.QualityWeight
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Metric.LlmModel.SortOrder)
            .First();

        _logger.LogInformation(
            "AI model selection (excluding provider) selected. Provider={Provider}, Model={Model}, Score={Score}",
            best.Metric.LlmModel.LlmProvider!.ProviderType,
            best.Metric.LlmModel.ModelId,
            best.Score);

        return new ModelSelectionResult
        {
            Model = best.Metric.LlmModel,
            Profile = profile,
            SelectionScore = best.Score
        };
    }

    private async Task<AiProfile> ResolveProfileAsync(
        Guid? profileId,
        CancellationToken ct)
    {
        if (profileId.HasValue)
        {
            _logger.LogWarning(
                "Resolving AI profile by explicit profile id. ProfileId={ProfileId}",
                profileId.Value);

            var explicitProfile = await _db.AiProfiles
                .FirstOrDefaultAsync(x =>
                    x.Id == profileId.Value &&
                    x.IsEnabled,
                    ct);

            if (explicitProfile is not null)
            {
                _logger.LogWarning(
                    "AI profile resolved from explicit profile id. ProfileId={ProfileId}, Profile={Profile}",
                    explicitProfile.Id,
                    explicitProfile.Name);

                return explicitProfile;
            }

            _logger.LogError(
                "Explicit AI profile was not found or not enabled. ProfileId={ProfileId}",
                profileId.Value);
        }

        _logger.LogWarning("Resolving AI profile from active AI settings.");

        var settingsProfile = await _db.AiSettings
            .Include(x => x.ActiveAiProfile)
            .Where(x =>
                x.ActiveAiProfile != null &&
                x.ActiveAiProfile.IsEnabled)
            .Select(x => x.ActiveAiProfile!)
            .FirstOrDefaultAsync(ct);

        if (settingsProfile is not null)
        {
            _logger.LogWarning(
                "AI profile resolved from active AI settings. ProfileId={ProfileId}, Profile={Profile}",
                settingsProfile.Id,
                settingsProfile.Name);

            return settingsProfile;
        }

        _logger.LogError(
            "No active AI settings profile found. Falling back to default profile.");

        var defaultProfile = await _db.AiProfiles
            .FirstOrDefaultAsync(x =>
                x.IsDefault &&
                x.IsEnabled,
                ct);

        if (defaultProfile is not null)
        {
            _logger.LogWarning(
                "AI profile resolved from default profile. ProfileId={ProfileId}, Profile={Profile}",
                defaultProfile.Id,
                defaultProfile.Name);

            return defaultProfile;
        }

        _logger.LogError("No active AI profile found.");

        throw new InvalidOperationException("No active AI profile found.");
    }

    /// <summary>
    /// Returns true for capabilities that are infrastructure-level and must select globally,
    /// bypassing any per-profile provider restrictions. Chat and Vision respect the active
    /// profile's provider filter; Embeddings and Audio do not.
    /// </summary>
    private static bool IsProfileIndependentCapability(LlmCapability capability) =>
        capability is LlmCapability.Embeddings or LlmCapability.Audio;

    private static string GetExclusionReason(LlmModelCapabilityMetric metric)
    {
        if (!metric.IsEnabled)
            return "MetricDisabled";

        if (metric.LlmModel is null)
            return "ModelMissing";

        if (!metric.LlmModel.IsEnabled)
            return "ModelDisabled";

        if (metric.LlmModel.LlmProvider is null)
            return "ProviderMissing";

        if (!metric.LlmModel.LlmProvider.IsEnabled)
            return "ProviderDisabled";

        return "None";
    }

    private sealed class ModelCandidateScore
    {
        public required LlmModelCapabilityMetric Metric { get; init; }

        public int WeightedCost { get; init; }
        public int WeightedSpeed { get; init; }
        public int WeightedQuality { get; init; }

        public int Score { get; init; }
    }
}