using System.Text.Json;
using YouAreLlm.Core;

namespace YouAreLlm.Web.Api;

public static class ResponsesMapper
{
    public static IReadOnlyList<ChatMessage> ToCoreMessages(JsonElement? input, string? instructions)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new ChatMessage("system", instructions));
        }

        if (input is null)
        {
            return messages;
        }

        var value = input.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                messages.Add(new ChatMessage("user", value.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    AddInputItem(messages, item);
                }

                break;
            case JsonValueKind.Object:
                AddInputItem(messages, value);
                break;
        }

        return messages;
    }

    private static void AddInputItem(List<ChatMessage> messages, JsonElement item)
    {
        var type = GetStringProperty(item, "type");
        switch (type)
        {
            case "function_call":
                messages.Add(new ChatMessage(
                    "assistant",
                    $"[function_call: {GetStringProperty(item, "name") ?? "function"} id={GetStringProperty(item, "call_id") ?? "unknown"}]{Environment.NewLine}{GetStringProperty(item, "arguments") ?? string.Empty}",
                    "function_call"));
                return;
            case "function_call_output":
                messages.Add(new ChatMessage(
                    "tool",
                    $"[tool_call_id: {GetStringProperty(item, "call_id") ?? "unknown"}]{Environment.NewLine}{NormalizeContent(item, "output")}",
                    "function_call_output"));
                return;
            case "message":
            case null:
                messages.Add(new ChatMessage(
                    NormalizeRole(GetStringProperty(item, "role")),
                    NormalizeContent(item, "content"),
                    "message"));
                return;
            default:
                messages.Add(new ChatMessage("user", $"[{type}]{Environment.NewLine}{item}", type));
                return;
        }
    }

    private static string NormalizeRole(string? role)
        => role is "system" or "user" or "assistant" or "tool" ? role : role == "developer" ? "system" : "user";

    private static string NormalizeContent(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(content.EnumerateArray().Select(NormalizeContentPart)),
            JsonValueKind.Null => string.Empty,
            _ => content.ToString(),
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

    private static string? GetStringProperty(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
