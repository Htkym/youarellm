using System.Threading.Channels;

namespace YouAreLlm.Core;

public sealed class PendingRequest
{
    private readonly Channel<string> _deltas = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly TaskCompletionSource<HumanCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private string _accumulated = string.Empty;

    public PendingRequest(
        string requestId,
        IReadOnlyList<ChatMessage> messages,
        string model,
        DateTimeOffset createdAt,
        string? toolsJson,
        string protocol,
        string? previousResponseId,
        string rawRequestJson)
    {
        RequestId = requestId;
        Messages = messages;
        Model = model;
        CreatedAt = createdAt;
        ToolsJson = toolsJson;
        Protocol = protocol;
        PreviousResponseId = previousResponseId;
        RawRequestJson = rawRequestJson;
    }

    public string RequestId { get; }

    public IReadOnlyList<ChatMessage> Messages { get; }

    public string Model { get; }

    public DateTimeOffset CreatedAt { get; }

    public string? ToolsJson { get; }

    public string Protocol { get; }

    public string? PreviousResponseId { get; }

    public string RawRequestJson { get; }

    public Task<HumanCompletion> Completion => _completion.Task;

    public ChannelReader<string> DeltaReader => _deltas.Reader;

    public string Accumulated
    {
        get
        {
            lock (_gate)
            {
                return _accumulated;
            }
        }
    }

    public void AddDelta(string text)
    {
        lock (_gate)
        {
            _accumulated += text;
        }

        _deltas.Writer.TryWrite(text);
    }

    public bool TryCompleteText(string finalText)
    {
        if (!string.IsNullOrEmpty(finalText))
        {
            AddDelta(finalText);
        }

        var fullText = Accumulated;
        var completed = _completion.TrySetResult(new TextCompletion(fullText));
        if (completed)
        {
            _deltas.Writer.TryComplete();
        }

        return completed;
    }

    public bool TryCompleteTool(ToolCallItem toolCall)
    {
        var completed = _completion.TrySetResult(new ToolCompletion(toolCall));
        if (completed)
        {
            _deltas.Writer.TryComplete();
        }

        return completed;
    }

    public bool TryCancel(Exception exception)
    {
        var completed = _completion.TrySetException(exception);
        if (completed)
        {
            _deltas.Writer.TryComplete();
        }

        return completed;
    }

    public PendingRequestSnapshot ToSnapshot()
        => new(RequestId, Messages, Model, CreatedAt, ToolsJson, Protocol, PreviousResponseId, RawRequestJson);
}
