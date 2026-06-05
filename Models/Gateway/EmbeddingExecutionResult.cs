namespace InfinityAI.Api.Models.Gateway;

public sealed class EmbeddingExecutionResult
{
    public float[] Embedding { get; init; } = [];
    public int InputTokens { get; init; }
}
