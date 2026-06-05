using InfinityAI.Api.Models.Database;

namespace InfinityAI.Api.Services
{
    public interface IDocumentExtractionService
    {
        Task<string> ExtractTextAsync(
            ApplicationFile file,
            CancellationToken ct);
    }
}
