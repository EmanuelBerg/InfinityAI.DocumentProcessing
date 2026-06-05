using InfinityAI.Api.Models.Ingestion;

namespace InfinityAI.Api.Services;

public interface IDocumentIngestionPublisher
{
    Task PublishAsync(DocumentIngestionJob job, CancellationToken ct = default);
}
