namespace InfinityAI.Api.Models.Qdrant;

public sealed class QdrantSearchResult
{
    public Guid DocumentChunkId { get; set; }
    public Guid DocumentId { get; set; }

    /// <summary>Cosine similarity score in [0, 1], where 1 = identical.</summary>
    public double Score { get; set; }

    public QdrantChunkPayload Payload { get; set; } = new();
}
