namespace InfinityAI.Api.Models.Gateway;

public sealed class ChatCompletionRequest
{
    public string? Provider { get; set; }

    public string Model { get; set; } = "";

    public List<object> Messages { get; set; } = [];

    public bool Stream { get; set; }
}