using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Tests;

public class GroundingTests
{
    [Fact]
    public void Diff_decoration_models_copy_along_is_stripped_from_quotes() =>
        Assert.Equal(new[] { "return value is not null;" },
            GroundingStage.ExtractSegments("[Line 97] +                    return   value is not null;"));

    [Fact]
    public void Removed_line_markers_and_plain_diff_prefixes_are_stripped()
    {
        Assert.Equal(new[] { "RunQuery(userInput);" }, GroundingStage.ExtractSegments("[-]         -     RunQuery(userInput);"));
        Assert.Equal(new[] { "var total = Compute(x);" }, GroundingStage.ExtractSegments("+ var total = Compute(x);"));
    }

    [Fact]
    public void Ellipses_split_a_quote_and_trivial_fragments_are_ignored() =>
        Assert.Equal(new[] { "var first = Load(path);", "return first.Value;" },
            GroundingStage.ExtractSegments("var first = Load(path); ... return first.Value; ..."));

    [Fact]
    public void Braces_and_empty_quotes_leave_nothing_to_check()
    {
        Assert.Empty(GroundingStage.ExtractSegments("}"));
        Assert.Empty(GroundingStage.ExtractSegments(""));
        Assert.Empty(GroundingStage.ExtractSegments(null));
    }

    [Theory]
    [InlineData("return value is not null;", "return value is not null;", true)]
    [InlineData("return value is not nul", "return value is not null;", false)]      // cut off mid-token: not a match
    [InlineData("turn value is", "return value is not null;", false)]               // starts mid-token
    [InlineData("value is not null", "x; return value is not null; y", true)]
    [InlineData("Compute(1)", "var a = Compute(1);", true)]
    public void A_quote_must_match_whole_tokens(string segment, string text, bool expected) =>
        Assert.Equal(expected, GroundingStage.ContainsToken(text, segment));

    private static string Source(bool withOld) => string.Join("\n",
        "namespace N;",
        "public class A",
        "{",
        "    public bool Try(string value)",
        "    {",
        "        return value is not null;",
        "    }",
        withOld ? "    public int Old() { return 1; }" : "    public int Fresh() { return 2; }",
        "}") + "\n";

    private static async Task<(AgentResult Result, string Commit)> Run(Func<string, Finding[]> findings, bool enabled = true, bool reviewOlderCommit = false)
    {
        using var repo = new TempRepo();
        repo.Write("src/A.cs", Source(withOld: true));  repo.Commit("one");
        repo.Write("src/A.cs", Source(withOld: false)); var second = repo.Commit("two");

        var commit = reviewOlderCommit ? "HEAD~1" : second;
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var context = new PipelineContext(repo.Path, commit, null, diff.Files.Select(f => new ChangedFile(f.Path, 0, 0, ChangeType.Modified)).ToList(), diff, null);

        var config = new PipelineConfig();
        config.Grounding.Enabled = enabled;
        var stage = new GroundingStage(Options.Create(config));
        var result = await stage.ExecuteAsync(new AgentResult(findings(second).ToList(), "base summary."), context);
        return (result, commit);
    }

    private static Finding F(string snippet, string file = "src/A.cs") =>
        new(Severity.High, FindingCategory.CodeSmell, file, 6, "claim", "fix", snippet, new FixExample("", ""), 0.9);

    [Fact]
    public async Task Real_fabricated_missing_and_unquoted_findings_get_the_right_verdicts()
    {
        var (result, _) = await Run(_ => new[]
        {
            F("return value is not null;"),                         // exists
            F("return   value  is   not  null;"),                   // same code, different spacing
            F("[Line 6] +        return value is not null;"),      // copied from the numbered diff
            F("return value is not nul"),                           // truncated token: the old false-Critical
            F("var x = Fabricated.Call();"),                        // invented
            F("public int Old() { return 1; }"),                    // a REMOVED line, present only in the diff
            F("var y = Real.Call();", file: "src/Nope.cs"),         // file that does not exist
            F("", file: "src/A.cs"),                                // quoted nothing
            F("return value is not null;", file: "A.cs"),           // shortened path resolves to src/A.cs
        });

        Assert.Equal(new bool?[] { true, true, true, false, false, true, false, null, true }, result.Findings.Select(f => f.Grounded));
    }

    [Fact]
    public async Task The_summary_reports_what_was_flagged()
    {
        var (result, _) = await Run(_ => new[] { F("return value is not null;"), F("var x = Fabricated.Call();"), F("") });
        Assert.StartsWith("base summary.", result.Summary);
        Assert.Contains("Grounding check: 1 of 3 finding(s) quote code that could not be found", result.Summary);
        Assert.Contains("1 quoted no code", result.Summary);
        Assert.Equal(1, result.Findings.Count(f => f.IsFlagged()));
    }

    [Fact]
    public async Task Verification_reads_the_reviewed_commit_not_the_working_tree()
    {
        // HEAD no longer has Old(), but the commit under review (HEAD~1) does.
        var (result, _) = await Run(_ => new[] { F("public int Old() { return 1; }") }, reviewOlderCommit: true);
        Assert.True(result.Findings[0].Grounded);
    }

    [Fact]
    public async Task A_commit_that_looks_like_a_git_option_is_not_passed_to_git_and_is_left_unchecked()
    {
        using var repo = new TempRepo();
        repo.Write("src/A.cs", Source(true)); repo.Commit("one");
        var context = new PipelineContext(repo.Path, "--output=/tmp/x", null, new List<ChangedFile>(), null, null);
        var stage = new GroundingStage(Options.Create(new PipelineConfig()));

        var result = await stage.ExecuteAsync(new AgentResult(new List<Finding> { F("return value is not null;") }, ""), context);
        // No readable file -> the quoted file does not exist for this commit.
        Assert.False(result.Findings[0].Grounded ?? false);
    }

    [Fact]
    public async Task When_disabled_findings_pass_through_untouched()
    {
        var (result, _) = await Run(_ => new[] { F("var x = Fabricated.Call();") }, enabled: false);
        Assert.Null(result.Findings[0].Grounded);
        Assert.Equal("base summary.", result.Summary);
    }

    [Fact]
    public void Flagged_means_low_judge_score_or_unverified_quote()
    {
        var plain = F("x");
        Assert.False(plain.IsFlagged());
        Assert.True((plain with { Grounded = false }).IsFlagged());
        Assert.False((plain with { Grounded = true }).IsFlagged());
        Assert.True((plain with { Evaluation = new FindingEvaluation(1, 1, 1, null, true) }).IsFlagged());
    }
}
