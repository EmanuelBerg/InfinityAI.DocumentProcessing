using InfinityAI.Api.Models.Gateway;

namespace InfinityAI.Api.Services
{
    public interface IEmbeddingService
    {
        Task<EmbeddingExecutionResult> CreateEmbeddingAsync(
            string input,
            string providerType,
            string modelId,
            CancellationToken ct);
    }
}
