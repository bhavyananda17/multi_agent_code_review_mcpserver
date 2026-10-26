using System.Text.RegularExpressions;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

public sealed record MatchOutcome(
    List<(int Prediction, int GroundTruth, double Similarity)> Pairs,
    HashSet<int> MatchedPredictions,
    HashSet<int> MatchedGroundTruth);

/// <summary>
/// Decides whether a pipeline finding "says the same thing" as a human reviewer's comment, using plain
/// lexical overlap: both texts are split into lower-cased, stop-word-filtered, lightly stemmed tokens
/// (camelCase aware) and compared with the Dice coefficient, 2|A∩B| / (|A|+|B|).
/// This is a deliberately simple proxy: it catches shared vocabulary ("SQL injection", "null check"),
/// not true paraphrase, so inspect the matches in the JSON output before trusting a threshold.
/// </summary>
public static class FindingMatcher
{
    public const double DefaultThreshold = 0.25;

    /// <summary>Two texts must share at least this many tokens, so one coincidental word never counts.</summary>
    public const int MinSharedTokens = 2;

    // Splits "SqlCommand" -> Sql, Command; "userId" -> user, Id; "HTTPClient" -> HTTP, Client; keeps digits.
    private static readonly Regex TokenRegex = new(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "an", "and", "or", "but", "if", "then", "else", "of", "to", "in", "on", "at", "by", "for", "with",
        "from", "into", "as", "is", "are", "was", "were", "be", "been", "being", "it", "its", "this", "that",
        "these", "those", "there", "here", "which", "who", "what", "when", "where", "why", "how", "not", "no",
        "can", "could", "should", "would", "may", "might", "must", "will", "shall", "do", "does", "did", "has",
        "have", "had", "also", "just", "only", "very", "more", "most", "some", "any", "all", "each", "per",
        "via", "than", "so", "such", "too", "use", "used", "using", "consider", "instead", "make", "ensure",
        "please", "code", "line", "method", "function", "file", "class", "you", "your", "we", "our",
        "think", "maybe", "probably", "possible", "possibly", "seems", "looks", "like", "nit"
    };

    // Longest suffix first. Enough to unify "injection/injected/injecting", "queries/query",
    // "concatenation/concatenating/concatenated/concatenate"; not a real stemmer. ("-ation" is deliberately
    // not a suffix: stripping only "-ion" leaves "concatenat", which is what "concatenate" stems to.)
    private static readonly string[] Suffixes =
        { "ions", "ion", "ings", "ing", "ied", "ies", "ed", "es", "s" };

    public static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TokenRegex.Matches(text))
        {
            var t = m.Value.ToLowerInvariant();
            if (Stopwords.Contains(t))
                continue;
            t = Stem(t);
            if (t.Length >= 2 && !Stopwords.Contains(t))
                tokens.Add(t);
        }
        return tokens;
    }

    private static string Stem(string t)
    {
        foreach (var suffix in Suffixes)
        {
            if (t.Length - suffix.Length < 3 || !t.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            var stem = t[..^suffix.Length];
            if (suffix is "ies" or "ied")
                return stem + "y";
            return stem.Length > 3 && stem.EndsWith('e') ? stem[..^1] : stem;
        }
        return t.Length > 4 && t.EndsWith('e') ? t[..^1] : t;
    }

    public static double Dice(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;

        var shared = a.Count(b.Contains);
        if (shared < Math.Min(MinSharedTokens, Math.Min(a.Count, b.Count)))
            return 0;

        return 2.0 * shared / (a.Count + b.Count);
    }

    /// <summary>
    /// Similarity of a pipeline finding to a reviewer comment. Reviewers often state the fix as well as the
    /// problem, so the finding is compared both as "category + description" and as
    /// "category + description + recommendation"; the better of the two wins.
    /// </summary>
    public static double Similarity(PredictedFinding finding, string groundTruth)
    {
        var gt = Tokenize(groundTruth);
        var problem = $"{finding.Category} {finding.Description}";
        var withFix = $"{problem} {finding.Recommendation}";
        return Math.Max(Dice(gt, Tokenize(problem)), Dice(gt, Tokenize(withFix)));
    }

    public static MatchOutcome Match(
        IReadOnlyList<PredictedFinding> predicted,
        IReadOnlyList<string> groundTruth,
        double threshold,
        bool oneToOne)
    {
        var pairs = new List<(int Prediction, int GroundTruth, double Similarity)>();
        for (var p = 0; p < predicted.Count; p++)
        {
            for (var g = 0; g < groundTruth.Count; g++)
            {
                var sim = Similarity(predicted[p], groundTruth[g]);
                if (sim >= threshold)
                    pairs.Add((p, g, sim));
            }
        }

        if (oneToOne)
        {
            // Greedy by similarity (ties broken by position) so each finding on either side is paired at most once.
            var usedP = new HashSet<int>();
            var usedG = new HashSet<int>();
            var chosen = new List<(int Prediction, int GroundTruth, double Similarity)>();
            foreach (var pair in pairs.OrderByDescending(x => x.Similarity).ThenBy(x => x.Prediction).ThenBy(x => x.GroundTruth))
            {
                if (usedP.Contains(pair.Prediction) || usedG.Contains(pair.GroundTruth))
                    continue;
                usedP.Add(pair.Prediction);
                usedG.Add(pair.GroundTruth);
                chosen.Add(pair);
            }
            pairs = chosen.OrderBy(x => x.Prediction).ToList();
        }

        return new MatchOutcome(
            pairs,
            pairs.Select(x => x.Prediction).ToHashSet(),
            pairs.Select(x => x.GroundTruth).ToHashSet());
    }

    /// <summary>
    /// Precision = matched predictions / predictions; recall = matched ground truth / ground truth.
    /// Zero-division convention: a run with no predictions has precision 0 (so F1 is 0, never "undefined").
    /// </summary>
    public static Metrics Score(int predicted, int groundTruth, int matchedPredictions, int matchedGroundTruth)
    {
        var precision = predicted == 0 ? 0 : (double)matchedPredictions / predicted;
        var recall = groundTruth == 0 ? 0 : (double)matchedGroundTruth / groundTruth;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        return new Metrics(precision, recall, f1);
    }
}
