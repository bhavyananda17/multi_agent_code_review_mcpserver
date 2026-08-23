using System.Diagnostics;

namespace MultiAgentCodeReview.Orchestration.Tools;

public interface IRepositorySourceResolver
{
    Task<ResolvedRepository> ResolveAsync(string repoPathOrUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// A repository location resolved from either a local path (used as-is) or a git
/// URL (cloned to a temp dir). Disposing deletes the temp dir only if one was created —
/// local-path resolutions are a no-op on dispose so stdio/local usage is byte-for-byte
/// unchanged from before this type existed.
/// </summary>
public sealed class ResolvedRepository : IAsyncDisposable
{
    public string Path { get; }
    private readonly bool _isTemp;

    internal ResolvedRepository(string path, bool isTemp)
    {
        Path = path;
        _isTemp = isTemp;
    }

    public ValueTask DisposeAsync()
    {
        if (_isTemp && Directory.Exists(Path))
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — never let temp-dir deletion mask the caller's actual result.
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Lets MCP tools accept either a local repo_path (stdio, same-machine usage) or a git
/// URL (remote/HTTP usage, where the caller has no shared filesystem with the server).
/// </summary>
public class RepositorySourceResolver : IRepositorySourceResolver
{
    public async Task<ResolvedRepository> ResolveAsync(string repoPathOrUrl, CancellationToken cancellationToken = default)
    {
        if (!LooksLikeGitUrl(repoPathOrUrl))
        {
            return new ResolvedRepository(repoPathOrUrl, isTemp: false);
        }

        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString());

        // Full clone on purpose: GitOperationsTool resolves diffs via `git merge-base`,
        // which needs real commit history — a shallow clone would break it.
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("clone");
        psi.ArgumentList.Add(repoPathOrUrl);
        psi.ArgumentList.Add(tempDir);

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git clone failed for '{repoPathOrUrl}': {stderr}");
        }

        return new ResolvedRepository(tempDir, isTemp: true);
    }

    private static bool LooksLikeGitUrl(string value)
    {
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("git@", StringComparison.OrdinalIgnoreCase);
    }
}
