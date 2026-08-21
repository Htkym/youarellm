using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using YouAreLlm.Core;

namespace YouAreLlm.Web.Mcp;

/// <summary>
/// 人間の operator へ自己完結した作業を委譲する MCP tool を提供する。
/// </summary>
[McpServerToolType]
public sealed class HumanDelegationTools(
    PendingRequestStore requestStore,
    ILogger<HumanDelegationTools> logger)
{
    private const int MaximumTaskLength = 4_000;

    /// <summary>
    /// Operator UI に作業を提示し、人間の最終応答を待機して返す。
    /// </summary>
    /// <param name="task">人間へ渡す自己完結した作業内容。</param>
    /// <param name="cancellationToken">呼び出し元が tool 実行を中止するトークン。</param>
    /// <returns>人間が返した最終テキスト、または選択した function call の要約。</returns>
    [McpServerTool(Name = "delegate_to_human")]
    [Description("Delegates one bounded task to the local human operator and returns the final response.")]
    public async Task<string> DelegateToHumanAsync(
        [Description("A bounded, self-contained task for the human operator.")] string task,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        if (task.Length > MaximumTaskLength)
        {
            throw new ArgumentOutOfRangeException(nameof(task), $"Task must not exceed {MaximumTaskLength} characters.");
        }

        var pending = requestStore.Add(
            [
                new ChatMessage(
                    "system",
                    "MCP human delegation. Answer only the bounded task supplied by the tool caller."),
                new ChatMessage("user", task.Trim()),
            ],
            "human");
        logger.LogInformation("Created human delegation request {RequestId}.", pending.RequestId);

        using var cancellationRegistration = cancellationToken.Register(() =>
            requestStore.TryCancel(
                pending.RequestId,
                new OperationCanceledException("MCP client cancelled the human delegation request.")));

        var completion = await pending.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        return completion switch
        {
            TextCompletion text => text.Text,
            ToolCompletion tool => DescribeToolCall(tool.ToolCall),
            _ => throw new InvalidOperationException("Unsupported human completion."),
        };
    }

    private static string DescribeToolCall(ToolCallItem toolCall)
        => toolCall switch
        {
            FunctionCallItem function => $"Human selected function {function.Name} with arguments {function.Arguments}.",
            _ => "Human selected an unsupported tool call.",
        };
}
