using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

public record BenchmarkComparison(
    int PairedRows,
    int OnlyInA,
    int OnlyInB,
    PairedComparison Precision,
    PairedComparison Recall,
    PairedComparison F1,
    int DegradedPairs = 0,
    bool ExcludedDegraded = false);

/// <summary>
/// Compares two benchmark result files (for example before and after a change, or two judge models) on the PRs
/// they have in common. A difference that the paired test and interval cannot tell from noise is reported as such.
/// </summary>
public static class BenchmarkComparer
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static BenchmarkResult Load(string json) =>
        JsonSerializer.Deserialize<BenchmarkResult>(json, ReadOptions)
        ?? throw new FormatException("Not a benchmark results file.");

    public static BenchmarkComparison Compare(BenchmarkResult a, BenchmarkResult b, bool excludeDegraded = false)
    {
        static Dictionary<(string, string), BenchmarkRowResult> Scored(BenchmarkResult r) =>
            r.Results.Where(x => x.Status == "ok").ToDictionary(x => (x.RepoUrl, x.CommitSha));

        var rowsA = Scored(a);
        var rowsB = Scored(b);
        var shared = rowsA.Keys.Where(rowsB.ContainsKey).OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToList();

        // A row where a stage timed out or the judge failed measures the infrastructure, not the pipeline.
        bool Degraded((string, string) k) => rowsA[k].Warnings is { Count: > 0 } || rowsB[k].Warnings is { Count: > 0 };
        var degradedPairs = shared.Count(Degraded);
        if (excludeDegraded)
            shared = shared.Where(k => !Degraded(k)).ToList();

        PairedComparison Of(Func<BenchmarkRowResult, double> metric) =>
            BenchmarkStats.Compare(shared.Select(k => (metric(rowsA[k]), metric(rowsB[k]))).ToList());

        return new BenchmarkComparison(
            shared.Count,
            rowsA.Keys.Count(k => !rowsB.ContainsKey(k)),
            rowsB.Keys.Count(k => !rowsA.ContainsKey(k)),
            Of(r => r.Precision!.Value),
            Of(r => r.Recall!.Value),
            Of(r => r.F1!.Value),
            degradedPairs,
            excludeDegraded);
    }

    public static string ToMarkdown(string nameA, string nameB, BenchmarkResult a, BenchmarkResult b, BenchmarkComparison c)
    {
        string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        string Signed(double v) => (v >= 0 ? "+" : "") + F(v);

        var sb = new StringBuilder();
        sb.AppendLine("# Benchmark comparison");
        sb.AppendLine();
        sb.AppendLine($"- **A:** `{nameA}`  ({Describe(a.Settings)})");
        sb.AppendLine($"- **B:** `{nameB}`  ({Describe(b.Settings)})");
        sb.AppendLine($"- **Paired PRs:** {c.PairedRows} scored in both runs" +
                      (c.OnlyInA + c.OnlyInB > 0 ? $" ({c.OnlyInA} only in A, {c.OnlyInB} only in B, ignored)" : ""));
        if (c.DegradedPairs > 0)
            sb.AppendLine($"- **Pairs where a pipeline stage degraded in A or B:** {c.DegradedPairs}" +
                          (c.ExcludedDegraded ? " (excluded from this comparison)" : " (included; rerun with `--exclude-degraded` to leave them out)"));
        sb.AppendLine();
        sb.AppendLine("| Metric | A (mean) | B (mean) | B − A | 95% interval | p (sign-flip) |");
        sb.AppendLine("|--------|---------:|---------:|------:|--------------|--------------:|");
        foreach (var (name, m) in new[] { ("Precision", c.Precision), ("Recall", c.Recall), ("F1", c.F1) })
            sb.AppendLine($"| {name} | {F(m.MeanA)} | {F(m.MeanB)} | **{Signed(m.MeanDifference)}** | [{Signed(m.CiLow)}, {Signed(m.CiHigh)}] | {F(m.PValue)} |");
        sb.AppendLine();

        var excludesZero = c.F1.CiLow > 0 || c.F1.CiHigh < 0;
        sb.AppendLine(c.PairedRows < 2
            ? "**Reading:** fewer than two paired PRs, so nothing can be concluded."
            : excludesZero && c.F1.PValue < 0.05
                ? $"**Reading:** the F1 difference is unlikely to be noise on these {c.PairedRows} PRs (interval excludes 0, p < 0.05). That is evidence about these PRs, not a general accuracy claim."
                : $"**Reading:** on {c.PairedRows} PRs the F1 difference cannot be told apart from noise (interval includes 0 or p ≥ 0.05). Do not claim an improvement or a regression from this alone.");
        sb.AppendLine();
        sb.AppendLine("Method: paired by (repo, commit); interval is a percentile bootstrap over PRs (10,000 resamples, fixed seed); p-value is an exact two-sided sign-flip permutation test on the per-PR differences. Both runs use LLMs at non-zero temperature, so a single run of each also carries run-to-run variance this comparison cannot see.");
        return sb.ToString();
    }

    private static string Describe(BenchmarkSettings s) =>
        $"static analysis {(s.StaticAnalysisEnabled ? "on" : "off")}, grounding {(s.GroundingEnabled ? "on" : "off")}, " +
        $"self-eval {(s.EvaluationEnabled ? "on" : "off")}" + (s.JudgeModel != null && s.EvaluationEnabled ? $" by {s.JudgeModel} ({s.JudgeRelation})" : "") +
        (s.DiffCharBudget > 0 ? $", diff window {s.DiffCharBudget}" : "") + $", match {s.MatchMode} >= {s.SimilarityThreshold.ToString("0.00", CultureInfo.InvariantCulture)}";
}
