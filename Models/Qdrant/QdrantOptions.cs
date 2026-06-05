namespace InfinityAI.Api.Models.Qdrant;

public sealed class QdrantOptions
{
    public string BaseUrl { get; set; } = "http://infinityai-qdrant:6333";
    public string CollectionPrefix { get; set; } = "chunks";
    public string Distance { get; set; } = "Cosine";
    public bool IsEnabled { get; set; } = true;
    public bool DualWriteEnabled { get; set; } = true;
    public bool SearchEnabled { get; set; } = true;
}
