using Microsoft.Extensions.Options;

namespace YouAreLlm.Web.Research;

/// <summary>
/// 未加工プロンプトをローカルの研究用ディレクトリへ JSON として保存する。
/// </summary>
public sealed class RawPromptArchive(
    IOptions<ResearchCaptureOptions> options,
    IHostEnvironment environment) : IRawPromptArchive
{
    private readonly string _directory = ResolveDirectory(options.Value.Directory, environment.ContentRootPath);

    /// <inheritdoc />
    public async Task ArchiveAsync(string requestId, string rawPrompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(rawPrompt);

        Directory.CreateDirectory(_directory);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{requestId}.json";
        var path = Path.Combine(_directory, fileName);

        await File.WriteAllTextAsync(path, rawPrompt, cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveDirectory(string directory, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        return Path.GetFullPath(directory, contentRootPath);
    }
}
