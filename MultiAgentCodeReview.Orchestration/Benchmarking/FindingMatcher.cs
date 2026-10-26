using MultiAgentCodeReview.Orchestration.Text;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

public sealed record MatchOutcome(
    List<(int Prediction, int GroundTruth, double Similarity)> Pairs,
    HashSet<int> MatchedPredictions,
    HashSet<int> MatchedGroundTruth);

/// <summary>
/// Decides whether a pipeline finding "says the same thing" as a human reviewer's comment, using the plain
/// lexical overlap in <see cref="TextSimilarity"/>. This is a deliberately simple proxy: it catches shared
/// vocabulary ("SQL injection", "null check"), not true paraphrase, so inspect the matches in the JSON output
/// before trusting a threshold.
/// </summary>
public static class FindingMatcher
{
    public const double DefaultThreshold = 0.25;

    public const int MinSharedTokens = TextSimilarity.MinSharedTokens;

    public static HashSet<string> Tokenize(string text) => TextSimilarity.Tokenize(text);

    public static double Dice(HashSet<string> a, HashSet<string> b) => TextSimilarity.Dice(a, b);

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
