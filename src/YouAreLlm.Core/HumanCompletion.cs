namespace YouAreLlm.Core;

public abstract record HumanCompletion;

public sealed record TextCompletion(string Text) : HumanCompletion;

public sealed record ToolCompletion(ToolCallItem ToolCall) : HumanCompletion;
