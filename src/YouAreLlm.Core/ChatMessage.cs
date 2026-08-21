namespace YouAreLlm.Core;

public sealed record ChatMessage(string Role, string Content, string ItemType = "message");
