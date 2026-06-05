using System.Text.Json.Serialization;

namespace InfinityAI.Api.Models.Gateway;

public sealed class ChatCompletionResponse
{
    public List<ChatChoice> Choices { get; set; } = [];
    public UsageInfo? Usage { get; set; }
}

public sealed class ChatChoice
{
    public ChatMessageResponse Message { get; set; } = new();
}

public sealed class ChatMessageResponse
{
    public string Role { get; set; } = "";
    public object? Content { get; set; }
}

public sealed class UsageInfo
{
    // Gateway serialises these with snake_case [JsonPropertyName] attributes; match them here.
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}