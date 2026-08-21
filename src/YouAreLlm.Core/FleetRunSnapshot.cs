namespace YouAreLlm.Core;

/// <summary>
/// 親子タスク実験の公開状態を表す。
/// </summary>
public sealed record FleetRunSnapshot(
    string RunId,
    string Goal,
    FleetRunStatus Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<FleetWorkerSnapshot> Workers,
    string? ParentRequestId,
    string? ParentResponse);

/// <summary>
/// 1 つの worker task の公開状態を表す。
/// </summary>
public sealed record FleetWorkerSnapshot(
    string WorkerId,
    string Name,
    string Assignment,
    FleetWorkerStatus Status,
    string? RequestId,
    string? Response);

/// <summary>
/// 親子タスク実験全体の進行状態を表す。
/// </summary>
public enum FleetRunStatus
{
    /// <summary>worker の応答を待機している。</summary>
    AwaitingWorkers,

    /// <summary>親の統合応答を待機している。</summary>
    AwaitingParent,

    /// <summary>親の統合応答まで完了した。</summary>
    Completed,
}

/// <summary>
/// worker task の進行状態を表す。
/// </summary>
public enum FleetWorkerStatus
{
    /// <summary>worker の応答を待機している。</summary>
    AwaitingResponse,

    /// <summary>worker の応答を受信した。</summary>
    Completed,
}
