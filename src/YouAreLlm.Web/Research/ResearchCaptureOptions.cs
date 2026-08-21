namespace YouAreLlm.Web.Research;

/// <summary>
/// 研究用の未加工リクエスト保存先を定義する設定。
/// </summary>
public sealed class ResearchCaptureOptions
{
    /// <summary>
    /// 設定セクション名。
    /// </summary>
    public const string SectionName = "ResearchCapture";

    /// <summary>
    /// 未加工リクエストを保存するディレクトリ。
    /// 相対パスは Web プロジェクトのコンテンツルートを基準に解決する。
    /// </summary>
    public string Directory { get; init; } = @"..\..\research-data\self-authored-raw-prompts";

    /// <summary>
    /// 保存対象が自己作成の研究用 payload であることを示す HTTP ヘッダー名。
    /// </summary>
    public const string SelfAuthoredRequestHeader = "X-YouAreLlm-Self-Authored";
}
