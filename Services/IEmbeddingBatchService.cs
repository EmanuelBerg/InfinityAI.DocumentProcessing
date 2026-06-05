namespace InfinityAI.Api.Services;

/// <summary>
/// Result of a single embedding request within a batch.
/// </summary>
public sealed record EmbeddingResult(int Index, float[] Vector);

/// <summary>
/// Batches embedding requests to reduce latency and control concurrency.
/// Phase 1: wraps single-embedding calls; Phase 2: replace with true batch API.
/// </summary>
public interface IEmbeddingBatchService
{
    Task<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(
        IReadOnlyList<string>  texts,
        string                 providerType,
        string                 modelId,
        IProgress<int>?        progress = null,
        CancellationToken      ct       = default);
}
