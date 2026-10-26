using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Tests;

/// <summary>
/// The window of diff each specialist sees. It used to be the first 4,000 characters, cut mid-line (which once made a
/// model report "is not nul" as a typo) and dominated by whichever file came first.
/// </summary>
public class DiffWindowTests
{
    private sealed class Probe : BaseSpecialistAgent
    {
        public Probe() : base(null!, "Probe") { }
        public override List<string> TriggerCategories { get; } = new();
        protected override (string prompt, Dictionary<string, HashSet<int>> validLines) BuildPrompt(PipelineContext context) => ("", new());
        public static (string Content, Dictionary<string, HashSet<int>> Valid) Window(PipelineContext context, int max) => BuildDiffContent(context, max);
    }

    private static FileDiff AddedFile(string path, int lines, Func<int, string>? text = null)
    {
        text ??= i => $"        var value{i} = Compute({i}); // {path}";
        var body = string.Concat(Enumerable.Range(1, lines).Select(i => "+" + text(i) + "\n"));
        return new FileDiff(path, path, ChangeType.Added, new List<Hunk> { new(0, 0, 1, lines, body) });
    }

    private static PipelineContext Context(params FileDiff[] files) =>
        new("repo", "HEAD", null, new List<ChangedFile>(), new GitDiff($"{files.Length} files", files.ToList()), null);

    private static List<string> RenderedLines(string content) =>
        content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("[Line ")).ToList();

    [Fact]
    public void A_diff_that_fits_is_shown_completely_with_no_omission_notes()
    {
        var (content, valid) = Probe.Window(Context(AddedFile("src/A.cs", 5), AddedFile("src/B.cs", 5)), 6000);
        Assert.DoesNotContain("not shown", content);
        Assert.Equal(10, RenderedLines(content).Count);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, valid["src/A.cs"].OrderBy(x => x));
    }

    [Fact]
    public void Every_file_gets_a_share_so_a_big_first_file_cannot_hide_the_rest()
    {
        var files = Enumerable.Range(1, 5).Select(i => AddedFile($"src/File{i}.cs", 300)).ToArray();
        var (content, valid) = Probe.Window(Context(files), 6000);

        foreach (var f in files)
        {
            Assert.Contains($"--- {f.Path} ---", content);
            Assert.True(valid[f.Path].Count > 0, $"{f.Path} showed no lines");
        }
        Assert.Contains("more line(s) of this file not shown", content);
        Assert.InRange(content.Length, 1000, 6000 + 800); // the budget plus headers/notes
    }

    [Fact]
    public void Unused_share_rolls_over_so_a_small_file_is_shown_in_full_next_to_a_huge_one()
    {
        var (content, valid) = Probe.Window(Context(AddedFile("src/Huge.cs", 500), AddedFile("src/Small.cs", 8)), 6000);
        Assert.Equal(8, valid["src/Small.cs"].Count);
        Assert.True(valid["src/Huge.cs"].Count < 500);
    }

    [Fact]
    public void Only_whole_lines_are_ever_shown_never_a_line_cut_mid_word()
    {
        string Original(int i) => $"        return partitionFilter{i} is not null && other{i}.HasValue; // marker {i}";
        var (content, _) = Probe.Window(Context(AddedFile("src/Cut.cs", 400, Original)), 3000);

        var rendered = RenderedLines(content);
        Assert.NotEmpty(rendered);
        foreach (var line in rendered)
        {
            var text = line[(line.IndexOf("] +", StringComparison.Ordinal) + 3)..];
            Assert.Contains(Enumerable.Range(1, 400).Select(Original), o => o == text);
        }
        Assert.DoesNotMatch(@"is not nul\s*(\r?\n|$)", content); // the artefact the old window produced
    }

    [Fact]
    public void Only_lines_that_are_actually_shown_may_be_cited_by_a_finding()
    {
        var (content, valid) = Probe.Window(Context(AddedFile("src/Long.cs", 400)), 2500);
        var shown = RenderedLines(content).Select(l => int.Parse(l[6..l.IndexOf(']')])).ToHashSet();
        Assert.Equal(shown, valid["src/Long.cs"]);
        Assert.DoesNotContain(400, valid["src/Long.cs"]);
    }

    [Fact]
    public void When_the_budget_runs_out_the_remaining_files_are_named_and_counted()
    {
        var files = Enumerable.Range(1, 40).Select(i => AddedFile($"src/File{i:00}.cs", 30)).ToArray();
        var (content, _) = Probe.Window(Context(files), 3000);

        var shown = files.Where(f => content.Contains($"--- {f.Path} ---")).Select(f => f.Path).ToList();
        var skipped = files.Select(f => f.Path).Except(shown).ToList();
        Assert.NotEmpty(shown);
        Assert.NotEmpty(skipped);
        Assert.Equal(files.Take(shown.Count).Select(f => f.Path), shown); // omitted files are the LAST ones, in order

        var note = content.Split('\n').Single(l => l.Contains("more file(s) not shown"));
        Assert.StartsWith($"... [{skipped.Count} more file(s) not shown: {skipped[0]}", note);
        if (skipped.Count > 8)
            Assert.Contains($"and {skipped.Count - 8} more", note);
    }

    [Fact]
    public void A_pathologically_long_line_is_clipped_with_a_visible_marker()
    {
        var (content, _) = Probe.Window(Context(AddedFile("src/Min.cs", 1, _ => new string('x', 5000))), 6000);
        Assert.Contains("[line truncated]", content);
    }

    [Fact]
    public void No_diff_means_no_window()
    {
        var (content, valid) = Probe.Window(new PipelineContext("repo", "HEAD", null, new List<ChangedFile>(), null, null), 6000);
        Assert.Equal("", content);
        Assert.Empty(valid);
    }
}
