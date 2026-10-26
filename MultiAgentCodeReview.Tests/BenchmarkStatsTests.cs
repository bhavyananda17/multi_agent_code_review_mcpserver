using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Benchmarking;

namespace MultiAgentCodeReview.Tests;

public class BenchmarkStatsTests
{
    private static BenchmarkRowResult Ok(int row, int predicted, int truth, int matchedPredictions, int matchedTruth,
        string url = "https://github.com/a/b", string sha = "abc1234")
    {
        var m = FindingMatcher.Score(predicted, truth, matchedPredictions, matchedTruth);
        return new BenchmarkRowResult(row, url, sha, "ok", null, 1, predicted, truth, 0, matchedPredictions, matchedTruth,
            m.Precision, m.Recall, m.F1, null, new(), new(), new());
    }

    private static BenchmarkRowResult Failed(int row) =>
        new(row, "https://github.com/a/b", "abc1234", "error", "boom", 0, 0, 2, 0, 0, 0, null, null, null, null, new(), new(), new());

    private static BenchmarkSettings Settings() =>
        new(0.25, "any-match", false, "test", true, 5, false, new Dictionary<string, string>());

    private static BenchmarkResult Run(params BenchmarkRowResult[] rows) =>
        new(DateTime.UtcNow, false, Settings(), BenchmarkRunner.Aggregate(rows), rows.ToList());

    [Fact]
    public void Macro_is_the_mean_of_per_pr_numbers_and_micro_pools_the_counts_and_failed_rows_are_left_out()
    {
        var agg = BenchmarkRunner.Aggregate(new[] { Ok(1, 4, 2, 2, 1), Ok(2, 6, 3, 3, 3), Failed(3) });

        Assert.Equal((3, 2, 1), (agg.Rows, agg.Succeeded, agg.Failed));
        Assert.Equal(0.5, agg.Macro!.Precision, 9);
        Assert.Equal(0.75, agg.Macro.Recall, 9);
        Assert.Equal((0.5 + 2.0 / 3) / 2, agg.Macro.F1, 9);
        Assert.Equal((10, 5, 5, 4), (agg.Micro!.Predicted, agg.Micro.GroundTruth, agg.Micro.MatchedPredictions, agg.Micro.MatchedGroundTruth));
        Assert.Equal(0.5, agg.Micro.Precision, 9);
        Assert.Equal(0.8, agg.Micro.Recall, 9);
        Assert.Equal(2 * 0.5 * 0.8 / 1.3, agg.Micro.F1, 9);
    }

    [Fact]
    public void When_every_row_failed_there_are_no_averages_and_no_division_by_zero()
    {
        var agg = BenchmarkRunner.Aggregate(new[] { Failed(1) });
        Assert.Null(agg.Macro); Assert.Null(agg.Micro); Assert.Null(agg.MacroCi95);
        Assert.Equal((0, 1), (agg.Succeeded, agg.Failed));
    }

    [Theory]
    [InlineData("https://user:token@github.com/a/b", "https://***@github.com/a/b")]
    [InlineData("https://github.com/a/b", "https://github.com/a/b")]
    [InlineData("git@github.com:a/b.git", "git@github.com:a/b.git")]
    public void Credentials_embedded_in_a_url_are_redacted(string url, string expected) =>
        Assert.Equal(expected, BenchmarkRunner.Redact(url));

    [Fact]
    public void Flagged_findings_are_not_scored_unless_asked_for()
    {
        var row = new BenchmarkRow(1, "https://github.com/a/b", "abc1234", new List<string> { "SQL injection via string concatenation" });
        Finding F(string desc, bool? grounded = null, bool lowConfidence = false) => new(Severity.High, FindingCategory.SqlInjection, "a.cs", 1,
            desc, "Use parameterized queries.", "", new FixExample("", ""), 0.9, null, Evaluation: lowConfidence ? new FindingEvaluation(1, 1, 1, null, true) : null, Grounded: grounded);
        var result = new AgentResult(new List<Finding>
        {
            F("SQL injection via string concatenation of the id"),
            F("SQL injection via string concatenation, hallucinated quote", grounded: false),
            F("SQL injection via string concatenation, judged weak", lowConfidence: true),
        }, "s");

        var strict = BenchmarkRunner.Score(row, row.RepoUrl, new BenchmarkOptions(), result, 1);
        Assert.Equal((1, 2), (strict.PredictedCount, strict.ExcludedLowConfidence));

        var everything = BenchmarkRunner.Score(row, row.RepoUrl, new BenchmarkOptions { IncludeLowConfidence = true }, result, 1);
        Assert.Equal((3, 0), (everything.PredictedCount, everything.ExcludedLowConfidence));
    }

    [Fact]
    public void Bootstrap_intervals_are_reproducible_and_bracket_the_mean()
    {
        var rows = Enumerable.Range(1, 8).Select(i => Ok(i, 4, 2, i % 3, i % 2)).ToList();
        var first = BenchmarkStats.MacroIntervals(rows)!;
        var second = BenchmarkStats.MacroIntervals(rows)!;
        Assert.Equal(first, second);

        var mean = rows.Average(r => r.F1!.Value);
        Assert.InRange(mean, first.F1.Low, first.F1.High);
        Assert.True(first.F1.Low < first.F1.High);
    }

    [Fact]
    public void Identical_values_give_a_zero_width_interval_and_one_row_gives_none()
    {
        var same = Enumerable.Range(1, 5).Select(i => Ok(i, 2, 2, 1, 1)).ToList();
        var ci = BenchmarkStats.MacroIntervals(same)!;
        Assert.Equal(ci.Precision.Low, ci.Precision.High, 12);
        Assert.Null(BenchmarkStats.MacroIntervals(new[] { Ok(1, 2, 2, 1, 1) }));
    }

    [Fact]
    public void Identical_runs_show_no_difference_and_p_of_one()
    {
        var c = BenchmarkStats.Compare(new[] { (0.2, 0.2), (0.5, 0.5), (0.0, 0.0) });
        Assert.Equal((0.0, 1.0), (c.MeanDifference, c.PValue));
    }

    [Theory]
    [InlineData(3, 1.0, 0.25)]      // 2 of the 2^3 sign assignments are as extreme
    [InlineData(5, 0.1, 0.0625)]    // 2 of 32
    public void Sign_flip_p_value_is_exact_for_small_samples(int n, double improvement, double expectedP)
    {
        var c = BenchmarkStats.Compare(Enumerable.Range(0, n).Select(_ => (0.0, improvement)).ToList());
        Assert.Equal(expectedP, c.PValue, 9);
        Assert.Equal(improvement, c.MeanDifference, 9);
    }

    [Fact]
    public void Symmetric_differences_cannot_be_distinguished_from_noise() =>
        Assert.Equal(1.0, BenchmarkStats.Compare(new[] { (0.0, 1.0), (1.0, 0.0) }).PValue, 9);

    [Fact]
    public void Comparer_pairs_by_repo_and_commit_and_ignores_failed_and_unmatched_rows()
    {
        var a = Run(Ok(1, 4, 2, 1, 1, sha: "aaaaaaa"), Ok(2, 4, 2, 0, 0, sha: "bbbbbbb"), Ok(3, 4, 2, 1, 1, sha: "ccccccc"), Failed(4));
        var b = Run(Ok(1, 4, 2, 2, 2, sha: "aaaaaaa"), Ok(2, 4, 2, 1, 1, sha: "bbbbbbb"), Ok(9, 4, 2, 1, 1, sha: "ddddddd"));

        var c = BenchmarkComparer.Compare(a, b);
        Assert.Equal((2, 1, 1), (c.PairedRows, c.OnlyInA, c.OnlyInB));
        Assert.True(c.F1.MeanDifference > 0);
    }

    [Fact]
    public void The_comparison_refuses_to_call_a_small_difference_an_improvement()
    {
        var a = Run(Ok(1, 4, 2, 1, 1, sha: "aaaaaaa"), Ok(2, 4, 2, 0, 0, sha: "bbbbbbb"));
        var b = Run(Ok(1, 4, 2, 2, 2, sha: "aaaaaaa"), Ok(2, 4, 2, 1, 1, sha: "bbbbbbb"));
        var markdown = BenchmarkComparer.ToMarkdown("a.json", "b.json", a, b, BenchmarkComparer.Compare(a, b));
        Assert.Contains("cannot be told apart from noise", markdown);
        Assert.Contains("| F1 |", markdown);
    }

    [Fact]
    public void Results_survive_a_json_round_trip_so_runs_can_be_compared_later()
    {
        var run = Run(Ok(1, 4, 2, 2, 1), Ok(2, 6, 3, 3, 3), Failed(3));
        var back = BenchmarkComparer.Load(BenchmarkReportWriter.ToJson(run));
        Assert.Equal(run.Aggregate.Macro, back.Aggregate.Macro);
        Assert.Equal(3, back.Results.Count);
        Assert.Equal("error", back.Results[2].Status);
    }

    // ---- rows where infrastructure trouble may have lowered the score ----

    private static BenchmarkRowResult Degraded(BenchmarkRowResult row) => row with { Warnings = new List<string> { "SecurityAgent timed out on all 3 attempts" } };

    [Fact]
    public void Rows_with_pipeline_warnings_are_counted_and_called_out_in_the_summary()
    {
        var run = Run(Ok(1, 4, 2, 2, 1, sha: "aaaaaaa"), Degraded(Ok(2, 4, 2, 0, 0, sha: "bbbbbbb")));
        Assert.Equal(1, run.Aggregate.DegradedRows);

        var markdown = BenchmarkReportWriter.ToMarkdown(run);
        Assert.Contains("1 scored with pipeline warnings", markdown);
        Assert.Contains("## ⚠ Rows with pipeline warnings", markdown);
        Assert.Contains("SecurityAgent timed out", markdown);
        Assert.Contains("⚠ |", markdown);   // the marker in the table row
    }

    [Fact]
    public void A_run_without_warnings_mentions_none()
    {
        var markdown = BenchmarkReportWriter.ToMarkdown(Run(Ok(1, 4, 2, 2, 1)));
        Assert.DoesNotContain("pipeline warnings", markdown);
    }

    [Fact]
    public void Warnings_from_the_pipeline_reach_the_scored_row()
    {
        var row = new BenchmarkRow(1, "https://github.com/a/b", "abc1234", new List<string> { "x y z" });
        var result = BenchmarkRunner.Score(row, row.RepoUrl, new BenchmarkOptions(), new AgentResult(new List<Finding>(), "s"), 1, new[] { "judge failed" });
        Assert.Equal(new[] { "judge failed" }, result.Warnings);
        Assert.Null(BenchmarkRunner.Score(row, row.RepoUrl, new BenchmarkOptions(), new AgentResult(new List<Finding>(), "s"), 1).Warnings);
    }

    [Fact]
    public void A_comparison_can_leave_out_pairs_where_either_side_degraded()
    {
        var a = Run(Ok(1, 4, 2, 1, 1, sha: "aaaaaaa"), Degraded(Ok(2, 4, 2, 0, 0, sha: "bbbbbbb")), Ok(3, 4, 2, 1, 1, sha: "ccccccc"));
        var b = Run(Ok(1, 4, 2, 2, 2, sha: "aaaaaaa"), Ok(2, 4, 2, 0, 0, sha: "bbbbbbb"), Ok(3, 4, 2, 2, 2, sha: "ccccccc"));

        var all = BenchmarkComparer.Compare(a, b);
        Assert.Equal((3, 1, false), (all.PairedRows, all.DegradedPairs, all.ExcludedDegraded));

        var clean = BenchmarkComparer.Compare(a, b, excludeDegraded: true);
        Assert.Equal((2, 1, true), (clean.PairedRows, clean.DegradedPairs, clean.ExcludedDegraded));

        var markdown = BenchmarkComparer.ToMarkdown("a.json", "b.json", a, b, clean);
        Assert.Contains("excluded from this comparison", markdown);
        Assert.Contains("--exclude-degraded", BenchmarkComparer.ToMarkdown("a.json", "b.json", a, b, all));
    }

    // ---- token accounting in the benchmark output ----

    [Fact]
    public void Tokens_are_shown_per_row_totalled_and_averaged_in_the_summary()
    {
        var usage = new TokenUsage(4, 9000, 3000, new() { ["m"] = new ModelTokenUsage(4, 9000, 3000) });
        var run = Run(Ok(1, 4, 2, 2, 1, sha: "aaaaaaa") with { Tokens = usage }, Ok(2, 4, 2, 1, 1, sha: "bbbbbbb") with { Tokens = usage });

        Assert.Equal(24000, run.Aggregate.TotalTokens);
        var markdown = BenchmarkReportWriter.ToMarkdown(run);
        Assert.Contains("| Tokens (k) |", markdown);
        Assert.Contains("12.0 |", markdown);                  // 12,000 tokens in one row
        Assert.Contains("24,000 in total, about 12,000 per scored PR", markdown);
    }

    [Fact]
    public void A_run_with_no_token_data_shows_dashes_and_no_total()
    {
        var markdown = BenchmarkReportWriter.ToMarkdown(Run(Ok(1, 4, 2, 2, 1)));
        Assert.DoesNotContain("in total, about", markdown);
    }

    [Fact]
    public void A_replay_run_says_it_only_covers_verification()
    {
        var settings = Settings() with { Mode = "replay" };
        var run = new BenchmarkResult(DateTime.UtcNow, false, settings, BenchmarkRunner.Aggregate(new[] { Ok(1, 4, 2, 2, 1) }), new List<BenchmarkRowResult> { Ok(1, 4, 2, 2, 1) });
        Assert.Contains("replay", BenchmarkReportWriter.ToMarkdown(run));
        Assert.Contains("only grounding and the judge ran", BenchmarkReportWriter.ToMarkdown(run));
    }
}
