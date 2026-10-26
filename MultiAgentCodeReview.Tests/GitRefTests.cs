using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Tests;

public class GitRefTests
{
    [Theory]
    [InlineData("HEAD")]
    [InlineData("HEAD~1")]
    [InlineData("main")]
    [InlineData("feature/some-branch")]
    [InlineData("v1.2.3")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("HEAD^")]
    [InlineData("@{u}")]
    public void Ordinary_refs_are_accepted(string value) => Assert.True(GitOperationsTool.IsSafeRef(value));

    [Theory]
    [InlineData("--output=/tmp/x")]
    [InlineData("-p")]
    [InlineData("HEAD --output=/tmp/x")]
    [InlineData("HEAD;rm -rf /")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("  ")]
    public void Anything_that_could_become_a_git_option_or_break_the_argument_is_refused(string value) =>
        Assert.False(GitOperationsTool.IsSafeRef(value));

    [Fact]
    public async Task An_option_smuggled_in_as_a_ref_is_rejected_before_git_runs_and_writes_nothing()
    {
        using var repo = new TempRepo();
        repo.Write("a.txt", "one"); repo.Commit("one");
        repo.Write("a.txt", "two"); repo.Commit("two");
        var target = Path.Combine(Path.GetTempPath(), "mar-should-not-exist-" + Guid.NewGuid());

        var git = new GitOperationsTool(repo.Path);
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetDiffAsync("HEAD~1", $"--output={target}"));
        await Assert.ThrowsAsync<ArgumentException>(() => git.GetChangedFilesAsync($"--output={target}", "HEAD"));
        Assert.False(File.Exists(target));
    }
}
