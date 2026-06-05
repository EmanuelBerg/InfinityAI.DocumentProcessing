using InfinityAI.Api.Models.Llm;

namespace InfinityAI.Api.Services
{
    public interface IModelSelectionService
    {
        Task<ModelSelectionResult> SelectBestModelAsync(
            LlmCapability capability,
            Guid? profileId = null,
            CancellationToken ct = default);

        Task<ModelSelectionResult?> SelectBestModelExcludingProviderAsync(
            LlmCapability capability,
            string excludedProviderType,
            Guid? profileId = null,
            CancellationToken ct = default);
    }
}
