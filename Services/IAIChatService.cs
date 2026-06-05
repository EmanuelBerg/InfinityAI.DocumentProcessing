using InfinityAI.Models.Database;
using InfinityAI.Api.Models.Database;
using InfinityAI.Api.Models.Gateway;

namespace InfinityAI.Api.Services;

public interface IAIChatService
{
    Task<string> SendAsync(
        List<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<string> SendAsync(
        List<ChatMessage> messages,
        IEnumerable<ApplicationFile> files,
        CancellationToken cancellationToken = default);

    Task<GatewayExecutionResult> SendAsync(
        List<ChatMessage> messages,
        List<ApplicationFile> imageFiles,
        string providerType,
        string modelId,
        CancellationToken ct);
}