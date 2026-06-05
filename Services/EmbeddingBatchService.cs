using InfinityAI.Api.Models.Ingestion;
using InfinityAI.Api.Services.Llm;
using Microsoft.Extensions.Options;

namespace InfinityAI.Api.Services;

/// <summary>
/// Wraps single-embedding calls with sequential execution.
///
/// Sequential (not parallel) by design: <see cref="LlmRouter"/> depends on a scoped
/// <see cref="InfinityAI.Data.ApplicationDbContext"/> that EF Core prohibits from concurrent use.
/// Running embedding calls in parallel on the same scope would cause
/// "A second operation was started on this context instance before a previous operation completed."
///
/// Future: if true parallelism is needed, each concurrent task must resolve its own
/// IServiceScope (and therefore its own DbContext) rather than sharing the caller's scope.
/// See DocumentIngestionOptions.MaxConcurrentEmbeddingRequests for the configuration knob
/// — keep it at 1 until the scope-per-task pattern is implemented.
/// </summary>
public sealed class EmbeddingBatchService(
    ILlmRouter llmRouter,
    IOptions<DocumentIngestionOptions> options,
    ILogger<EmbeddingBatchService> logger) : IEmbeddingBatchService
{
    public async Task<IReadOnlyList<EmbeddingResult>> CreateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        string                providerType,
        string                modelId,
        IProgress<int>?       progress = null,
        CancellationToken     ct       = default)
    {
        var batchSize = options.Value.EmbeddingBatchSize;
        var results   = new EmbeddingResult[texts.Count];
        var completed = 0;

        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var text = texts[i];

            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = new EmbeddingResult(i, []);
                continue;
            }

            var vector = await llmRouter.CreateEmbeddingWithModelAsync(text, providerType, modelId, ct);
            results[i] = new EmbeddingResult(i, vector);

            completed++;

            if (completed % batchSize == 0 || completed == texts.Count)
            {
                logger.LogInformation(
                    "[DOC-INGEST] Embedding progress: {Done}/{Total} chunks",
                    completed, texts.Count);
            }

            progress?.Report(completed);
        }

        return results;
    }
}
