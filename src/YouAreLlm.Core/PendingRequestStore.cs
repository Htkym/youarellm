using System.Collections.Concurrent;
using System.Text.Json;

namespace YouAreLlm.Core;

public sealed class PendingRequestStore
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
    private readonly List<CompletedRequestSnapshot> _history = [];
    private readonly object _historyGate = new();

    public event Func<Task>? Changed;

    public PendingRequest Add(
        IReadOnlyList<ChatMessage> messages,
        string model,
        string? toolsJson = null,
        TimeSpan? timeout = null,
        string protocol = "Chat Completions",
        string? previousResponseId = null,
        string rawRequestJson = "")
    {
        var request = new PendingRequest(
            Guid.NewGuid().ToString("N"),
            messages,
            model,
            DateTimeOffset.UtcNow,
            toolsJson,
            protocol,
            previousResponseId,
            rawRequestJson);

        if (!_pending.TryAdd(request.RequestId, request))
        {
            throw new InvalidOperationException($"Request ID collision: {request.RequestId}");
        }

        _ = NotifyChangedAsync();
        _ = TimeoutAsync(request.RequestId, timeout ?? DefaultTimeout);
        return request;
    }

    public IReadOnlyList<PendingRequestSnapshot> GetPending()
        => _pending.Values
            .OrderBy(request => request.CreatedAt)
            .Select(request => request.ToSnapshot())
            .ToArray();

    public IReadOnlyList<CompletedRequestSnapshot> GetHistory()
    {
        lock (_historyGate)
        {
            return _history.ToArray();
        }
    }

    public bool TryGetCompletedRequest(string requestId, out CompletedRequestSnapshot? request)
    {
        lock (_historyGate)
        {
            request = _history.FirstOrDefault(item => item.RequestId == requestId);
            return request is not null;
        }
    }

    public bool TryAddDelta(string requestId, string text)
    {
        if (!_pending.TryGetValue(requestId, out var request))
        {
            return false;
        }

        request.AddDelta(text);
        _ = NotifyChangedAsync();
        return true;
    }

    public bool TryCompleteText(string requestId, string text)
    {
        if (!_pending.TryRemove(requestId, out var request))
        {
            return false;
        }

        var completed = request.TryCompleteText(text);
        if (completed)
        {
            AddHistory(request, request.Accumulated, request.Accumulated);
        }

        _ = NotifyChangedAsync();
        return completed;
    }

    public bool TryCompleteTool(string requestId, ToolCallItem toolCall)
    {
        if (!_pending.TryRemove(requestId, out var request))
        {
            return false;
        }

        var completed = request.TryCompleteTool(toolCall);
        if (completed)
        {
            AddHistory(
                request,
                DescribeToolCall(toolCall),
                JsonSerializer.Serialize(toolCall, toolCall.GetType()));
        }

        _ = NotifyChangedAsync();
        return completed;
    }

    public bool TryCancel(string requestId, Exception exception)
    {
        if (!_pending.TryRemove(requestId, out var request))
        {
            return false;
        }

        var cancelled = request.TryCancel(exception);
        _ = NotifyChangedAsync();
        return cancelled;
    }

    private async Task TimeoutAsync(string requestId, TimeSpan timeout)
    {
        await Task.Delay(timeout).ConfigureAwait(false);
        if (_pending.TryRemove(requestId, out var request))
        {
            request.TryCancel(new TimeoutException($"Request {requestId} timed out."));
            await NotifyChangedAsync().ConfigureAwait(false);
        }
    }

    private void AddHistory(PendingRequest request, string response, string usageOutput)
    {
        lock (_historyGate)
        {
            _history.Insert(0, new CompletedRequestSnapshot(
                request.RequestId,
                request.Messages,
                request.Model,
                request.CreatedAt,
                DateTimeOffset.UtcNow,
                response,
                request.ToolsJson,
                request.Protocol,
                request.PreviousResponseId,
                request.RawRequestJson,
                usageOutput));
        }
    }

    private async Task NotifyChangedAsync()
    {
        var changed = Changed;
        if (changed is null)
        {
            return;
        }

        await changed().ConfigureAwait(false);
    }

    private static string DescribeToolCall(ToolCallItem toolCall)
        => toolCall switch
        {
            FunctionCallItem function => $"[function_call: {function.Name}]\n{function.Arguments}",
            _ => "[tool_call]",
        };
}
