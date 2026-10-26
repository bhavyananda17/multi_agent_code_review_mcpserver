using MultiAgentCodeReview.Orchestration.Benchmarking;
using MultiAgentCodeReview.Orchestration.Text;

namespace MultiAgentCodeReview.Tests;

public class TextSimilarityTests
{
    private static string Tokens(string text) => string.Join(",", TextSimilarity.Tokenize(text).OrderBy(t => t));

    [Theory]
    [InlineData("injection", "injected", "injecting", "injections")]
    [InlineData("concatenation", "concatenating", "concatenated", "concatenate")]
    [InlineData("queries", "query", "querying", "queried")]
    [InlineData("validation", "validate", "validated")]
    public void Word_forms_stem_to_the_same_token(params string[] words) =>
        Assert.Single(words.Select(Tokens).Distinct());

    [Fact]
    public void Identifiers_are_split_and_single_characters_dropped() =>
        Assert.True(TextSimilarity.Tokenize("SqlCommand userId HTTPClient v2 port8080")
            .SetEquals(new[] { "sql", "command", "user", "id", "http", "client", "port", "8080" }));

    [Fact]
    public void Stopwords_are_ignored() => Assert.Empty(TextSimilarity.Tokenize("This should not be used here"));

    [Fact]
    public void One_shared_word_is_not_enough_when_both_sides_have_several_tokens() =>
        Assert.Equal(0, TextSimilarity.Dice(TextSimilarity.Tokenize("query cache"), TextSimilarity.Tokenize("query index")));

    [Fact]
    public void A_one_token_text_can_match_on_its_single_word() =>
        Assert.True(TextSimilarity.Dice(TextSimilarity.Tokenize("deadlock"), TextSimilarity.Tokenize("deadlock risk")) > 0);

    [Fact]
    public void Identical_vocabulary_scores_one() =>
        Assert.Equal(1.0, TextSimilarity.Dice(TextSimilarity.Tokenize("null check missing"), TextSimilarity.Tokenize("missing null check")), 9);

    [Fact]
    public void An_empty_side_scores_zero() =>
        Assert.Equal(0, TextSimilarity.Dice(new HashSet<string>(), TextSimilarity.Tokenize("a thing")));

    private static PredictedFinding Finding(string category, string description, string recommendation) =>
        new("High", category, "src/A.cs", 1, description, recommendation, false, null);

    [Fact]
    public void Related_pairs_clear_the_default_threshold_and_unrelated_ones_do_not()
    {
        var findings = new Dictionary<string, PredictedFinding>
        {
            ["sql"] = Finding("SqlInjection", "SQL injection via string concatenation of the id parameter into the query", "Use parameterized queries instead of string concatenation."),
            ["block"] = Finding("BlockingAsyncCall", "Calls .Result on an async method which blocks the thread and risks deadlock", "Use await instead of .Result."),
            ["name"] = Finding("Naming", "Variable name x is not descriptive", "Rename x to something meaningful."),
            ["swallow"] = Finding("ErrorHandling", "Exception is swallowed in an empty catch block", "Log or rethrow the exception."),
        };
        var comments = new (string Text, string Expect)[]
        {
            ("This builds the SQL query by concatenating user input - injection risk", "sql"),
            ("Please use parameterized queries here", "sql"),
            ("Don't call .Result, it can deadlock - await it", "block"),
            ("Rename this variable, the name isn't clear", "name"),
            ("Exceptions are being swallowed silently", "swallow"),
            ("Add a unit test for the happy path", ""),
            ("The query should be cached", ""),   // shares a single word with the SQL finding
            ("Typo in the README", ""),
        };

        foreach (var (key, finding) in findings)
            foreach (var (text, expect) in comments)
                Assert.True((expect == key) == (FindingMatcher.Similarity(finding, text) >= FindingMatcher.DefaultThreshold),
                    $"{key} vs \"{text}\" scored {FindingMatcher.Similarity(finding, text):0.00}");
    }
}
