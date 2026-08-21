namespace YouAreLlm.Core;

public abstract record ToolCallItem(string CallId);

public sealed record FunctionCallItem(string CallId, string Name, string Arguments) : ToolCallItem(CallId);
