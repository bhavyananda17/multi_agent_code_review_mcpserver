using System.Reflection;
using MultiAgentCodeReview.Orchestration.Benchmarking;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Tests;

public class GitRepoProviderTests
{
    private sealed class StubResolver(Func<string, ResolvedRepository> make) : IRepositorySourceResolver
    {
        public int Calls;
        public Task<ResolvedRepository> ResolveAsync(string repoPathOrUrl, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(make(repoPathOrUrl));
        }
    }

    // ResolvedRepository's constructor is internal on purpose; the test builds one around a local repo.
    private static ResolvedRepository Wrap(string path) =>
        (ResolvedRepository)typeof(ResolvedRepository)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(string), typeof(bool) })!
            .Invoke(new object[] { path, true });

    private static (TempRepo Repo, string Root, string First, string Second) History()
    {
        var repo = new TempRepo();
        repo.Write("root.txt", "root");  var root = repo.Commit("root");
        repo.Write("a.txt", "one");      var first = repo.Commit("one");
        repo.Write("a.txt", "two");      var second = repo.Commit("two");
        return (repo, root, first, second);
    }

    [Fact]
    public async Task Checks_out_the_requested_commit_and_reuses_the_clone()
    {
        var (repo, _, first, second) = History();
        var resolver = new StubResolver(_ => Wrap(repo.Path));
        var provider = new GitRepoProvider(resolver);

        var path = await provider.PrepareAsync("https://github.com/a/b", first);
        Assert.Equal("one", File.ReadAllText(Path.Combine(path, "a.txt")));

        var again = await provider.PrepareAsync("https://github.com/a/b", second);
        Assert.Equal(path, again);
        Assert.Equal("two", File.ReadAllText(Path.Combine(again, "a.txt")));
        Assert.Equal(1, resolver.Calls);

        await provider.DisposeAsync();
        Assert.False(Directory.Exists(repo.Path)); // temp clone is removed on dispose
    }

    [Fact]
    public async Task A_root_commit_is_rejected_instead_of_silently_producing_an_empty_diff()
    {
        var (repo, root, _, _) = History();
        await using var provider = new GitRepoProvider(new StubResolver(_ => Wrap(repo.Path)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync("https://github.com/a/b", root));
        Assert.Contains("no parent", ex.Message);
    }

    [Fact]
    public async Task An_unknown_sha_gives_a_clear_checkout_error()
    {
        var (repo, _, _, _) = History();
        await using var provider = new GitRepoProvider(new StubResolver(_ => Wrap(repo.Path)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync("https://github.com/a/b", "deadbeefdeadbeef"));
        Assert.Contains("checkout", ex.Message);
    }

    [Fact]
    public async Task Local_paths_and_option_like_shas_are_refused()
    {
        var (repo, _, first, _) = History();
        await using var provider = new GitRepoProvider(new StubResolver(_ => Wrap(repo.Path)));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync(repo.Path, first));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.PrepareAsync("https://github.com/a/b", "--orphan"));
    }

    private static readonly TimeSpan[] NoDelay = { TimeSpan.Zero, TimeSpan.Zero };

    [Fact]
    public async Task A_clone_that_keeps_failing_is_retried_a_bounded_number_of_times_then_remembered()
    {
        var resolver = new StubResolver(_ => throw new InvalidOperationException("RPC failed; curl 56 Recv failure: Connection reset by peer"));
        await using var provider = new GitRepoProvider(resolver, NoDelay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync("https://github.com/x/y", "abcdef0"));
        Assert.Equal(3, resolver.Calls);                 // the first attempt plus two retries

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync("https://github.com/x/y", "abcdef1"));
        Assert.Equal(3, resolver.Calls);                 // remembered: later rows for the same repo do not clone again
    }

    [Fact]
    public async Task A_dropped_connection_is_survived_by_retrying()
    {
        var (repo, _, first, _) = History();
        var attempts = 0;
        var resolver = new StubResolver(_ => ++attempts < 3
            ? throw new InvalidOperationException("fatal: early EOF; Connection reset by peer")
            : Wrap(repo.Path));
        await using var provider = new GitRepoProvider(resolver, NoDelay);

        var path = await provider.PrepareAsync("https://github.com/a/b", first);
        Assert.Equal("one", File.ReadAllText(Path.Combine(path, "a.txt")));
        Assert.Equal(3, resolver.Calls);
    }

    [Fact]
    public async Task A_permanent_failure_such_as_a_missing_repo_is_not_retried()
    {
        var resolver = new StubResolver(_ => throw new InvalidOperationException("remote: Repository not found."));
        await using var provider = new GitRepoProvider(resolver, NoDelay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync("https://github.com/x/missing", "abcdef0"));
        Assert.Equal(1, resolver.Calls);
    }
}
