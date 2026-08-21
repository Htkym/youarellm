using System.Text.Json;
using YouAreLlm.Core;

namespace YouAreLlm.Web.Api;

public static class ChatCompletionMapper
{
    public static IReadOnlyList<ChatMessage> ToCoreMessages(IReadOnlyList<OpenAiChatMessage> messages)
        => messages
            .Select(message => new ChatMessage(
                NormalizeRole(message.Role),
                NormalizeMessageContent(message),
                GetItemType(message)))
            .ToArray();

    private static string NormalizeRole(string role)
        => role is "system" or "user" or "assistant" or "tool" ? role : role == "developer" ? "system" : "user";

    private static string GetItemType(OpenAiChatMessage message)
        => message.Role == "tool"
            ? "tool_result"
            : message.ToolCalls is { ValueKind: JsonValueKind.Array } ? "tool_calls" : "message";

    private static string NormalizeMessageContent(OpenAiChatMessage message)
    {
        var content = NormalizeContent(message.Content);
        if (message.ToolCalls is { ValueKind: JsonValueKind.Array } toolCalls)
        {
            var toolCallText = string.Join(Environment.NewLine, toolCalls.EnumerateArray().Select(DescribeToolCall));
            content = string.IsNullOrWhiteSpace(content) ? toolCallText : content + Environment.NewLine + toolCallText;
        }

        if (message.Role == "tool" && !string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            content = $"[tool_call_id: {message.ToolCallId}]{Environment.NewLine}{content}";
        }

        return content;
    }

    private static string DescribeToolCall(JsonElement toolCall)
    {
        var id = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (toolCall.TryGetProperty("function", out var function))
        {
            var name = function.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var arguments = function.TryGetProperty("arguments", out var argsElement) ? argsElement.GetString() : null;
            return $"[tool_call: {name ?? "function"} id={id ?? "unknown"}]{Environment.NewLine}{arguments}";
        }

        return $"[tool_call id={id ?? "unknown"}]{Environment.NewLine}{toolCall}";
    }

    private static string NormalizeContent(JsonElement? content)
    {
        if (content is null)
        {
            return string.Empty;
        }

        var value = content.Value;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(NormalizeContentPart)),
            JsonValueKind.Null => string.Empty,
            _ => value.ToString(),
        };
    }

    private static string NormalizeContentPart(JsonElement part)
    {
        if (part.ValueKind == JsonValueKind.Object &&
            part.TryGetProperty("text", out var text) &&
            text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        if (part.ValueKind == JsonValueKind.Object &&
            part.TryGetProperty("type", out var type) &&
            type.GetString() is { } partType)
        {
            return $"[{partType}]";
        }

        return part.ValueKind == JsonValueKind.String ? part.GetString() ?? string.Empty : part.ToString();
    }
}
