namespace YouAreLlm.Core;

/// <summary>
/// 自己作成の 2 worker と親統合の進行を、既存の人間 provider request 上で管理する。
/// </summary>
public sealed class FleetRunStore : IFleetRunStore
{
    private const string WorkerToolsJson = """
        [
          {
            "type": "function",
            "function": {
              "name": "read_file",
              "description": "Read one self-authored greeting fixture file.",
              "parameters": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "start_line": { "type": "integer", "minimum": 1 },
                  "end_line": { "type": "integer", "minimum": 1 }
                },
                "required": ["path", "start_line", "end_line"],
                "additionalProperties": false
              }
            }
          }
        ]
        """;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly List<FleetRunState> _runs = [];
    private readonly PendingRequestStore _requestStore;

    /// <summary>
    /// 新しい親子タスクストアを初期化する。
    /// </summary>
    /// <param name="requestStore">人間 provider の request を管理するストア。</param>
    public FleetRunStore(PendingRequestStore requestStore)
    {
        _requestStore = requestStore ?? throw new ArgumentNullException(nameof(requestStore));
        _requestStore.Changed += ReconcileAsync;
    }

    /// <inheritdoc />
    public event Func<Task>? Changed;

    /// <summary>
    /// 新しい実行を初期化し、仕様確認と実装確認の worker task を同時に追加する。
    /// </summary>
    /// <param name="goal">親が統合する自己作成の作業目標。</param>
    /// <returns>作成した実行のスナップショット。</returns>
    public FleetRunSnapshot StartRun(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var workers = new[]
        {
            CreateWorker(
                "Specification worker",
                "Read README.md, then return the whitespace-only requirement as concise evidence.",
                "Use one read_file call for README.md, lines 1 through 20."),
            CreateWorker(
                "Implementation worker",
                "Read src/GreetingService.cs, then report whether the implementation follows the requirement.",
                "Use one read_file call for src/GreetingService.cs, lines 1 through 20."),
        };
        var run = new FleetRunState(Guid.NewGuid().ToString("N"), goal.Trim(), DateTimeOffset.UtcNow, workers);

        lock (_gate)
        {
            _runs.Insert(0, run);
        }

        _ = NotifyChangedAsync();
        return run.ToSnapshot();
    }

    /// <inheritdoc />
    public IReadOnlyList<FleetRunSnapshot> GetRuns()
    {
        lock (_gate)
        {
            return _runs.Select(run => run.ToSnapshot()).ToArray();
        }
    }

    private FleetWorkerState CreateWorker(string name, string assignment, string action)
    {
        var messages = new[]
        {
            new ChatMessage(
                "system",
                "Self-authored fleet simulation worker. Work only on the assigned safe greeting fixture task."),
            new ChatMessage(
                "user",
                $"{assignment}{Environment.NewLine}{Environment.NewLine}{action}"),
        };
        var request = _requestStore.Add(messages, "human", WorkerToolsJson);
        return new FleetWorkerState(Guid.NewGuid().ToString("N"), name, assignment, request.RequestId);
    }

    private async Task ReconcileAsync()
    {
        await _reconciliationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var completedRequests = _requestStore.GetHistory()
                .ToDictionary(request => request.RequestId, StringComparer.Ordinal);
            var parentRequestsToCreate = new List<FleetRunState>();
            var changed = false;

            lock (_gate)
            {
                foreach (var run in _runs)
                {
                    foreach (var worker in run.Workers.Where(worker => worker.Response is null))
                    {
                        if (completedRequests.TryGetValue(worker.RequestId, out var completed))
                        {
                            worker.Response = completed.Response;
                            changed = true;
                        }
                    }

                    if (run.Status == FleetRunStatus.AwaitingWorkers && run.Workers.All(worker => worker.Response is not null))
                    {
                        run.Status = FleetRunStatus.AwaitingParent;
                        parentRequestsToCreate.Add(run);
                        changed = true;
                    }

                    if (run.Status == FleetRunStatus.AwaitingParent &&
                        run.ParentRequestId is not null &&
                        completedRequests.TryGetValue(run.ParentRequestId, out var parentCompletion))
                    {
                        run.ParentResponse = parentCompletion.Response;
                        run.Status = FleetRunStatus.Completed;
                        changed = true;
                    }
                }
            }

            foreach (var run in parentRequestsToCreate)
            {
                var parentRequest = _requestStore.Add(CreateParentMessages(run), "human");
                lock (_gate)
                {
                    run.ParentRequestId = parentRequest.RequestId;
                }
            }

            if (changed || parentRequestsToCreate.Count > 0)
            {
                await NotifyChangedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _reconciliationGate.Release();
        }
    }

    private static IReadOnlyList<ChatMessage> CreateParentMessages(FleetRunState run)
    {
        var workerResults = string.Join(
            Environment.NewLine + Environment.NewLine,
            run.Workers.Select(worker => $"{worker.Name}:{Environment.NewLine}{worker.Response}"));
        return
        [
            new ChatMessage(
                "system",
                "Self-authored fleet simulation parent. Synthesize the worker results without adding unrelated evidence."),
            new ChatMessage(
                "user",
                $"Parent goal: {run.Goal}{Environment.NewLine}{Environment.NewLine}Worker results:{Environment.NewLine}{workerResults}{Environment.NewLine}{Environment.NewLine}Return evidence, action, and verification."),
        ];
    }

    private async Task NotifyChangedAsync()
    {
        var changed = Changed;
        if (changed is not null)
        {
            await changed().ConfigureAwait(false);
        }
    }

    private sealed class FleetRunState(
        string runId,
        string goal,
        DateTimeOffset createdAt,
        IReadOnlyList<FleetWorkerState> workers)
    {
        public string RunId { get; } = runId;

        public string Goal { get; } = goal;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public IReadOnlyList<FleetWorkerState> Workers { get; } = workers;

        public FleetRunStatus Status { get; set; } = FleetRunStatus.AwaitingWorkers;

        public string? ParentRequestId { get; set; }

        public string? ParentResponse { get; set; }

        public FleetRunSnapshot ToSnapshot() => new(
            RunId,
            Goal,
            Status,
            CreatedAt,
            Workers.Select(worker => worker.ToSnapshot()).ToArray(),
            ParentRequestId,
            ParentResponse);
    }

    private sealed class FleetWorkerState(
        string workerId,
        string name,
        string assignment,
        string requestId)
    {
        public string WorkerId { get; } = workerId;

        public string Name { get; } = name;

        public string Assignment { get; } = assignment;

        public string RequestId { get; } = requestId;

        public string? Response { get; set; }

        public FleetWorkerSnapshot ToSnapshot() => new(
            WorkerId,
            Name,
            Assignment,
            Response is null ? FleetWorkerStatus.AwaitingResponse : FleetWorkerStatus.Completed,
            RequestId,
            Response);
    }
}
