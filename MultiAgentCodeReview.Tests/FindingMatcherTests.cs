using MultiAgentCodeReview.Orchestration.Benchmarking;

namespace MultiAgentCodeReview.Tests;

public class FindingMatcherTests
{
    private static readonly PredictedFinding Sql = new("High", "SqlInjection",
        "src/A.cs", 4, "SQL injection via string concatenation of the id parameter into the query",
        "Use parameterized queries instead of string concatenation.", false, null);

    private const string CommentA = "This builds the SQL query by concatenating user input - injection risk";
    private const string CommentB = "Please use parameterized queries here";
    private const string Unrelated = "Add a unit test for the happy path";

    [Fact]
    public void Any_match_lets_a_duplicate_finding_count_twice_for_precision()
    {
        var outcome = FindingMatcher.Match(new[] { Sql, Sql with { Line = 9 } }, new[] { CommentA, Unrelated }, 0.25, oneToOne: false);
        Assert.Equal(2, outcome.MatchedPredictions.Count);
        Assert.Single(outcome.MatchedGroundTruth);
    }

    [Fact]
    public void One_to_one_does_not_let_a_duplicate_double_count()
    {
        var outcome = FindingMatcher.Match(new[] { Sql, Sql with { Line = 9 } }, new[] { CommentA, Unrelated }, 0.25, oneToOne: true);
        Assert.Single(outcome.MatchedPredictions);
        Assert.Single(outcome.MatchedGroundTruth);
        Assert.Single(outcome.Pairs);
    }

    [Fact]
    public void Any_match_lets_one_finding_recall_two_comments_one_to_one_only_one()
    {
        Assert.Equal(2, FindingMatcher.Match(new[] { Sql }, new[] { CommentA, CommentB }, 0.25, false).MatchedGroundTruth.Count);
        Assert.Single(FindingMatcher.Match(new[] { Sql }, new[] { CommentA, CommentB }, 0.25, true).MatchedGroundTruth);
    }

    [Fact]
    public void A_threshold_of_one_matches_only_identical_wording() =>
        Assert.Empty(FindingMatcher.Match(new[] { Sql }, new[] { CommentA }, 1.0, false).Pairs);

    [Fact]
    public void Empty_inputs_produce_no_pairs_and_no_crash()
    {
        Assert.Empty(FindingMatcher.Match(new List<PredictedFinding>(), new[] { CommentA }, 0.25, false).Pairs);
        Assert.Empty(FindingMatcher.Match(new[] { Sql }, new List<string>(), 0.25, false).Pairs);
    }

    [Fact]
    public void Score_computes_precision_recall_and_f1()
    {
        var m = FindingMatcher.Score(10, 4, 3, 2);
        Assert.Equal(0.3, m.Precision, 9);
        Assert.Equal(0.5, m.Recall, 9);
        Assert.Equal(0.375, m.F1, 9);
    }

    [Fact]
    public void No_predictions_scores_zero_everywhere_never_nan()
    {
        var m = FindingMatcher.Score(0, 3, 0, 0);
        Assert.Equal((0.0, 0.0, 0.0), (m.Precision, m.Recall, m.F1));
    }

    [Fact]
    public void A_perfect_run_scores_one() =>
        Assert.Equal((1.0, 1.0, 1.0), (FindingMatcher.Score(3, 3, 3, 3).Precision, FindingMatcher.Score(3, 3, 3, 3).Recall, FindingMatcher.Score(3, 3, 3, 3).F1));
}
