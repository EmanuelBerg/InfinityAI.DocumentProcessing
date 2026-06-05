namespace InfinityAI.Api.Models.Qdrant;

/// <summary>
/// Metadata stored alongside each vector in Qdrant.
/// Enables server-side filtering and debug inspection without hitting MySQL.
/// </summary>
public sealed class QdrantChunkPayload
{
    public Guid DocumentChunkId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? FileId { get; set; }

    public string FileName { get; set; } = "";
    public string DocumentTitle { get; set; } = "";

    public int ChunkIndex { get; set; }
    public int? PageNumber { get; set; }
    public string? Heading { get; set; }

    public string EmbeddingModel { get; set; } = "";
    public string EmbeddingProvider { get; set; } = "";

    // First 500 chars for debug inspection — full content lives in MySQL.
    public string ContentPreview { get; set; } = "";

    public DateTime CreatedUtc { get; set; }

    // Populated when document belongs to KnowledgeCollections.
    // A document may appear in multiple collections simultaneously.
    public List<string>? CollectionIds { get; set; }

    // Group IDs that have access; enables Qdrant-side filtering for shared collections.
    public List<string>? AllowedGroupIds { get; set; }
}
