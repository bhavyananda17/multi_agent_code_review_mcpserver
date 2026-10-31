using System.Diagnostics;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

/// <summary>Supplies a local checkout of a repo at a given commit. Disposing releases everything it created.</summary>
public interface IBenchmarkRepoProvider : IAsyncDisposable
{
    /// <returns>Path of a local clone whose working tree is at <paramref name="commitSha"/>.</returns>
    Task<string> PrepareAsync(string repoUrl, string commitSha, CancellationToken cancellationToken = default);
}

/// <summary>
/// Clones each distinct repo URL once (full clone, into a temp dir) and checks the requested commit out
/// before each review. Checking out matters: the pipeline reads file contents and the Roslyn dependency
/// graph from the working tree, which must be the commit under test, not whatever HEAD is today.
/// Only ever operates on its own temp clones - it refuses local paths so it can't touch a real checkout.
/// </summary>
public sealed class GitRepoProvider : IBenchmarkRepoProvider
{
    // A clone of a large repo over a flaky connection can drop ("Connection reset by peer"); one dropped
    // connection should not cost a whole benchmark row.
    private static readonly TimeSpan[] DefaultCloneRetryDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

    // Failures that retrying cannot fix.
    private static readonly string[] PermanentCloneErrors =
        { "not found", "Authentication failed", "could not read Username", "Permission denied", "does not appear to be a git repository", "Repository not found" };

    private readonly IRepositorySourceResolver _resolver;
    private readonly TimeSpan[] _retryDelays;
    private readonly Dictionary<string, ResolvedRepository> _clones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _cloneFailures = new(StringComparer.Ordinal);

    public GitRepoProvider(IRepositorySourceResolver resolver, TimeSpan[]? retryDelays = null)
    {
        _resolver = resolver;
        _retryDelays = retryDelays ?? DefaultCloneRetryDelays;
    }

    public async Task<string> PrepareAsync(string repoUrl, string commitSha, CancellationToken cancellationToken = default)
    {
        if (!BenchmarkCsv.IsRepoUrl(repoUrl))
            throw new ArgumentException("Benchmark repos must be git URLs; local paths are refused so a real checkout is never modified.");
        if (!BenchmarkCsv.IsCommitSha(commitSha)) // also blocks option-injection like "--orphan"
            throw new ArgumentException($"'{commitSha}' is not a commit SHA.");

        if (_cloneFailures.TryGetValue(repoUrl, out var earlier))
            throw new InvalidOperationException(earlier);

        if (!_clones.TryGetValue(repoUrl, out var repo))
        {
            try
            {
                repo = await CloneWithRetryAsync(repoUrl, cancellationToken);
                _clones[repoUrl] = repo;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _cloneFailures[repoUrl] = ex.Message; // don't re-clone for every remaining row of a repo that failed
                throw;
            }
        }

        var checkout = await RunGitAsync(repo.Path, cancellationToken, "checkout", "--quiet", "--detach", commitSha);
        if (checkout.ExitCode != 0)
            throw new InvalidOperationException($"git checkout {commitSha} failed (is the SHA in this repo?): {checkout.Error}");

        // The pipeline diffs <sha>~1..<sha>. A root commit has no parent, git prints an error the pipeline
        // doesn't see, and the row would silently score as "pipeline found nothing". Fail it loudly instead.
        var parent = await RunGitAsync(repo.Path, cancellationToken, "rev-parse", "--verify", "--quiet", $"{commitSha}~1^{{commit}}");
        if (parent.ExitCode != 0)
            throw new InvalidOperationException($"commit {commitSha} has no parent, so there is no diff to review.");

        return repo.Path;
    }

    private async Task<ResolvedRepository> CloneWithRetryAsync(string repoUrl, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _resolver.ResolveAsync(repoUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                                       && attempt < _retryDelays.Length
                                       && !PermanentCloneErrors.Any(p => ex.Message.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                await Task.Delay(_retryDelays[attempt], cancellationToken);
            }
        }
    }

    private static async Task<(int ExitCode, string Error)> RunGitAsync(
        string repoPath, CancellationToken cancellationToken, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await stdout;
        return (process.ExitCode, (await stderr).Trim());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var clone in _clones.Values)
            await clone.DisposeAsync();
        _clones.Clear();
    }
}
