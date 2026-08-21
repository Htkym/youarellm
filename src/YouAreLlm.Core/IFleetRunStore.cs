namespace YouAreLlm.Core;

/// <summary>
/// 自己作成の親子タスク実験を管理するストア。
/// </summary>
public interface IFleetRunStore
{
    /// <summary>
    /// タスク状態が変化したときに発生するイベント。
    /// </summary>
    event Func<Task>? Changed;

    /// <summary>
    /// 現在の実験実行を取得する。
    /// </summary>
    /// <returns>開始順の実行スナップショット。</returns>
    IReadOnlyList<FleetRunSnapshot> GetRuns();

    /// <summary>
    /// 2 個の worker task を並列に開始する。
    /// </summary>
    /// <param name="goal">親が統合する自己作成の作業目標。</param>
    /// <returns>作成した実行のスナップショット。</returns>
    FleetRunSnapshot StartRun(string goal);
}
