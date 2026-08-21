using System.Text.Json;
using Microsoft.ML.Tokenizers;
using YouAreLlm.Core;

namespace YouAreLlm.Web.Api;

public sealed class TokenUsageEstimator
{
    private readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
    private readonly object _tokenizerGate = new();

    public TokenUsage Estimate(
        IReadOnlyList<ChatMessage> messages,
        string? toolsJson,
        HumanCompletion completion)
    {
        var output = completion switch
        {
            TextCompletion text => text.Text,
            ToolCompletion tool => JsonSerializer.Serialize(tool.ToolCall, tool.ToolCall.GetType()),
            _ => string.Empty,
        };

        return Estimate(messages, toolsJson, output);
    }

    public TokenUsage Estimate(
        IReadOnlyList<ChatMessage> messages,
        string? toolsJson,
        string output)
        => new(EstimateInput(messages, toolsJson), CountTokens(output));

    public int EstimateInput(IReadOnlyList<ChatMessage> messages, string? toolsJson)
    {
        var messageTokens = messages.Sum(message => CountTokens(message.Content));
        return messageTokens + CountTokens(toolsJson);
    }

    private int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        lock (_tokenizerGate)
        {
            return _tokenizer.CountTokens(text);
        }
    }
}

public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    public static TokenUsage Empty { get; } = new(0, 0);

    public int TotalTokens => InputTokens + OutputTokens;
}
