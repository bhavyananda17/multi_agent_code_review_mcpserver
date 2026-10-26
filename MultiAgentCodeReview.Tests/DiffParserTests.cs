using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Tests;

/// <summary>Regression tests for the diff parser: it used to drop each file's last hunk and mangle paths.</summary>
public class DiffParserTests
{
    private static string Lines(int n, Func<int, string> f) => string.Join("\n", Enumerable.Range(1, n).Select(f)) + "\n";

    private static TempRepo RepoWithTrickyChanges()
    {
        var repo = new TempRepo();
        repo.Write("app/Alpha.cs", Lines(8, i => $"line {i} alpha"));
        repo.Write("backend/Beta.cs", Lines(40, i => $"line {i} beta"));
        repo.Write("bin/Gamma.cs", Lines(8, i => $"line {i} gamma"));
        repo.Write("src/my file.cs", Lines(8, i => $"line {i} spaced"));
        repo.Write("src/ToDelete.cs", Lines(5, i => $"line {i} doomed"));
        repo.WriteBytes("src/Binary.bin", new byte[] { 0, 1, 2, 3, 0, 255 });
        repo.Write("src/NoNewline.cs", Lines(6, i => $"line {i} nonl").TrimEnd('\n'));
        repo.Write("src/Renamed.cs", Lines(20, i => $"line {i} rename-me"));
        repo.Commit("base");

        repo.Write("app/Alpha.cs", Lines(8, i => i == 2 ? "line 2 alpha CHANGED" : $"line {i} alpha"));
        repo.Write("backend/Beta.cs", Lines(40, i => i is 3 or 35 ? $"line {i} beta CHANGED" : $"line {i} beta"));
        repo.Write("bin/Gamma.cs", Lines(8, i => i == 5 ? "line 5 gamma CHANGED" : $"line {i} gamma"));
        repo.Write("src/my file.cs", Lines(8, i => i == 4 ? "line 4 spaced CHANGED" : $"line {i} spaced"));
        repo.Delete("src/ToDelete.cs");
        repo.Write("src/New.cs", Lines(4, i => $"line {i} brand new"));
        repo.WriteBytes("src/Binary.bin", new byte[] { 0, 9, 9, 9, 0, 255 });
        repo.Write("src/NoNewline.cs", Lines(6, i => i == 6 ? "line 6 nonl CHANGED" : $"line {i} nonl").TrimEnd('\n'));
        repo.Delete("src/Renamed.cs");
        repo.Write("src/RenamedTo.cs", Lines(20, i => i == 10 ? "line 10 rename-me CHANGED" : $"line {i} rename-me"));
        repo.Write("zeta/Last.cs", Lines(3, i => $"line {i} last"));
        repo.Commit("change");
        return repo;
    }

    [Fact]
    public async Task Paths_match_git_exactly_including_prefixes_that_start_with_a_or_b_and_spaces()
    {
        using var repo = RepoWithTrickyChanges();
        var git = new GitOperationsTool(repo.Path);
        var diff = await git.GetDiffAsync("HEAD~1", "HEAD");
        var names = (await git.GetChangedFilesAsync("HEAD~1", "HEAD")).OrderBy(x => x);

        Assert.Equal(names, diff.Files.Select(f => f.Path).OrderBy(x => x));
        Assert.Contains("app/Alpha.cs", diff.Files.Select(f => f.Path));      // was "pp/Alpha.cs"
        Assert.Contains("bin/Gamma.cs", diff.Files.Select(f => f.Path));      // was "in/Gamma.cs"
        Assert.Contains("src/my file.cs", diff.Files.Select(f => f.Path));    // was "file.cs"
    }

    [Fact]
    public async Task Every_files_hunks_match_gits_own_section_for_that_file()
    {
        using var repo = RepoWithTrickyChanges();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");

        var sections = new List<List<string>>();
        foreach (var line in repo.Git("diff", "HEAD~1", "HEAD", "--no-color").Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("diff --git ")) sections.Add(new List<string>());
            if (sections.Count > 0) sections[^1].Add(line);
        }
        Assert.Equal(sections.Count, diff.Files.Count);

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var firstHunk = section.FindIndex(l => l.StartsWith("@@"));
            var body = firstHunk < 0 ? new List<string>() : section.Skip(firstHunk).ToList();
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);

            var file = diff.Files[i];
            Assert.Equal(body.Count(l => l.StartsWith("@@")), file.Hunks.Count);
            Assert.Equal(
                body.Where(l => !l.StartsWith("@@")),
                file.Hunks.SelectMany(h => h.Content.Replace("\r", "").TrimEnd('\n').Split('\n')));
        }
    }

    [Fact]
    public async Task A_multi_hunk_file_keeps_both_hunks_at_their_own_positions()
    {
        using var repo = RepoWithTrickyChanges();
        var beta = (await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD")).Files.Single(f => f.Path == "backend/Beta.cs");
        Assert.Equal(2, beta.Hunks.Count);
        Assert.True(beta.Hunks[0].NewStart < 10 && beta.Hunks[1].NewStart >= 30);
    }

    [Fact]
    public async Task No_file_receives_another_files_lines()
    {
        using var repo = RepoWithTrickyChanges();
        var alpha = (await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD")).Files.Single(f => f.Path == "app/Alpha.cs");
        var text = string.Concat(alpha.Hunks.Select(h => h.Content));
        Assert.Contains("alpha", text);
        Assert.DoesNotContain("beta", text);
        Assert.DoesNotContain("gamma", text);
        Assert.DoesNotContain("spaced", text);
    }

    [Fact]
    public async Task Added_deleted_and_renamed_files_are_labelled_as_such_not_all_Modified()
    {
        using var repo = RepoWithTrickyChanges();
        var files = (await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD")).Files.ToDictionary(f => f.Path);

        Assert.Equal(ChangeType.Added, files["src/New.cs"].ChangeType);
        Assert.Equal(ChangeType.Added, files["zeta/Last.cs"].ChangeType);
        Assert.Equal(ChangeType.Deleted, files["src/ToDelete.cs"].ChangeType);
        Assert.Equal(ChangeType.Renamed, files["src/RenamedTo.cs"].ChangeType);
        Assert.Equal("src/Renamed.cs", files["src/RenamedTo.cs"].OldPath);
        Assert.Equal(ChangeType.Modified, files["app/Alpha.cs"].ChangeType);
        Assert.Equal("app/Alpha.cs", files["app/Alpha.cs"].OldPath);
    }

    [Fact]
    public async Task The_added_and_removed_line_counts_shown_to_the_agents_match_git_numstat()
    {
        using var repo = RepoWithTrickyChanges();
        var stage = new MultiAgentCodeReview.Orchestration.Pipeline.FilterStage(new GitOperationsTool(repo.Path), new CodeAnalysisTool());
        var context = await stage.ExecuteAsync(repo.Path, "HEAD", "HEAD~1", 30, 5, default, null, runStaticAnalysis: false);

        var numstat = repo.Git("diff", "--numstat", "HEAD~1", "HEAD").Split('\n')
            .Select(l => l.Split('\t')).Where(p => p.Length == 3 && !p[2].Contains("=>") && p[0] != "-")
            .ToDictionary(p => p[2], p => (Added: int.Parse(p[0]), Deleted: int.Parse(p[1])));

        foreach (var (path, expected) in numstat.Where(kv => kv.Key.EndsWith(".cs")))
        {
            var file = context.ChangedFiles.Single(f => f.Path == path);
            Assert.Equal((expected.Added, expected.Deleted), (file.Additions, file.Deletions));
        }
        Assert.True(numstat.Count >= 6);
    }
}
