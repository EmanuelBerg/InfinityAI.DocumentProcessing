namespace InfinityAI.Api.Models.Gateway;

public sealed class GatewayExecutionResult
{
    public string Content { get; init; } = "";
    public UsageInfo? Usage { get; init; }

    public bool IsError { get; init; }
    public int? ProviderStatusCode { get; init; }
    public string? ErrorCode { get; init; }
}
