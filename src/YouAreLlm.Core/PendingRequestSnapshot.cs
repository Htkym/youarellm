namespace YouAreLlm.Core;

public sealed record PendingRequestSnapshot(
    string RequestId,
    IReadOnlyList<ChatMessage> Messages,
    string Model,
    DateTimeOffset CreatedAt,
    string? ToolsJson,
    string Protocol = "Chat Completions",
    string? PreviousResponseId = null,
    string RawRequestJson = "")
{
    public bool IsBackgroundRequest =>
        HasNoAdvertisedTools &&
        Messages.Count == 2 &&
        string.Equals(Messages[0].Role, "system", StringComparison.OrdinalIgnoreCase) &&
        Messages[0].Content.StartsWith("Task:", StringComparison.Ordinal) &&
        string.Equals(Messages[1].Role, "user", StringComparison.OrdinalIgnoreCase);

    private bool HasNoAdvertisedTools
        => string.IsNullOrWhiteSpace(ToolsJson) || string.Equals(ToolsJson.Trim(), "[]", StringComparison.Ordinal);
}

public sealed record CompletedRequestSnapshot(
    string RequestId,
    IReadOnlyList<ChatMessage> Messages,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset CompletedAt,
    string Response,
    string? ToolsJson,
    string Protocol = "Chat Completions",
    string? PreviousResponseId = null,
    string RawRequestJson = "",
    string UsageOutput = "");
