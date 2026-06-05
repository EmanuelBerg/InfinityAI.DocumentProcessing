using InfinityAI.Models.Database;
using InfinityAI.Api.Models.Database;

namespace InfinityAI.Api.Services.Llm;

public interface ILlmRouter
{
    Task<string> SendChatAsync(
        Guid userId,
        List<ChatMessage> messages,
        List<ApplicationFile> imageFiles,
        CancellationToken ct,
        string? correlationId = null);

    Task<ApplicationFile> GenerateImageAsync(
        Guid userId,
        Guid conversationId,
        string prompt,
        CancellationToken ct);

    /// <summary>
    /// Generates an embedding using the model selected by ModelSelectionService.
    /// Prefer <see cref="CreateEmbeddingWithModelAsync"/> when the caller has already
    /// selected the model, to avoid a redundant selection round-trip.
    /// </summary>
    Task<float[]> CreateEmbeddingAsync(
        string input,
        CancellationToken ct);

    /// <summary>
    /// Generates an embedding using an explicitly specified provider and model.
    /// Use this during document indexing and retrieval so both paths are guaranteed
    /// to use the exact same model without a second round of model selection.
    /// </summary>
    Task<float[]> CreateEmbeddingWithModelAsync(
        string input,
        string providerType,
        string modelId,
        CancellationToken ct);
}
