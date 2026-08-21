using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YouAreLlm.Web.Api;

public sealed record ChatCompletionRequest
{
    [JsonPropertyName("model")]
    [Description("Model identifier. Defaults to human.")]
    public string? Model { get; init; }

    [JsonPropertyName("messages")]
    [Description("OpenAI-compatible chat messages.")]
    public IReadOnlyList<OpenAiChatMessage> Messages { get; init; } = [];

    [JsonPropertyName("stream")]
    [Description("Whether to stream chat completion chunks as SSE.")]
    public bool Stream { get; init; }

    [JsonPropertyName("stream_options")]
    public ChatCompletionStreamOptions? StreamOptions { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; init; }
}

public sealed record ChatCompletionStreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; init; }
}

public sealed record OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = "user";

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public JsonElement? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }
}

public sealed record ResponsesRequest
{
    [JsonPropertyName("model")]
    [Description("Model identifier. Defaults to human.")]
    public string? Model { get; init; }

    [JsonPropertyName("instructions")]
    [Description("System or developer instructions for the response.")]
    public string? Instructions { get; init; }

    [JsonPropertyName("input")]
    [Description("Response input items, including messages and function call outputs.")]
    public JsonElement? Input { get; init; }

    [JsonPropertyName("stream")]
    [Description("Whether to stream response events as SSE.")]
    public bool Stream { get; init; }

    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; init; }

    [JsonPropertyName("previous_response_id")]
    public string? PreviousResponseId { get; init; }
}

public sealed record ModelsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<ModelInfo> Data);

public sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);
