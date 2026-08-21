namespace YouAreLlm.Web.Research;

/// <summary>
/// 受信した未加工プロンプトを研究用ストレージへ保存する。
/// </summary>
public interface IRawPromptArchive
{
    /// <summary>
    /// 指定したリクエストの JSON ペイロードを保存する。
    /// </summary>
    /// <param name="requestId">保存対象を識別するリクエスト ID。</param>
    /// <param name="rawPrompt">受信した JSON ペイロード。</param>
    /// <param name="cancellationToken">非同期操作を中止するトークン。</param>
    /// <returns>保存操作を表すタスク。</returns>
    Task ArchiveAsync(string requestId, string rawPrompt, CancellationToken cancellationToken);
}
