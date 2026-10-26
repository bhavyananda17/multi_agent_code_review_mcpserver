namespace MultiAgentCodeReview.Orchestration.Benchmarking;

public record Interval(double Low, double High);

public record MetricIntervals(Interval Precision, Interval Recall, Interval F1);

/// <summary>Paired comparison of two runs over the same PRs (B minus A).</summary>
public record PairedComparison(int N, double MeanA, double MeanB, double MeanDifference, double CiLow, double CiHigh, double PValue);

/// <summary>
/// Small, dependency-free resampling statistics. A benchmark of a dozen PRs says very little on its own, so every
/// aggregate gets an interval and every before/after claim gets a paired test. Seeds are fixed, so the same
/// input always yields the same numbers.
/// </summary>
public static class BenchmarkStats
{
    public const int DefaultSeed = 12345;

    /// <summary>95% percentile-bootstrap intervals for the macro averages, resampling PRs with replacement.</summary>
    public static MetricIntervals? MacroIntervals(IReadOnlyList<BenchmarkRowResult> okRows, int resamples = 2000, int seed = DefaultSeed)
    {
        if (okRows.Count < 2)
            return null;

        return new MetricIntervals(
            Bootstrap(okRows.Select(r => r.Precision!.Value).ToArray(), resamples, seed),
            Bootstrap(okRows.Select(r => r.Recall!.Value).ToArray(), resamples, seed + 1),
            Bootstrap(okRows.Select(r => r.F1!.Value).ToArray(), resamples, seed + 2));
    }

    public static Interval Bootstrap(double[] values, int resamples, int seed)
    {
        var rng = new Random(seed);
        var means = new double[resamples];
        for (var i = 0; i < resamples; i++)
        {
            double sum = 0;
            for (var j = 0; j < values.Length; j++)
                sum += values[rng.Next(values.Length)];
            means[i] = sum / values.Length;
        }
        Array.Sort(means);
        return new Interval(Quantile(means, 0.025), Quantile(means, 0.975));
    }

    /// <summary>
    /// Mean difference B-A with a percentile-bootstrap 95% interval (resampling pairs) and a two-sided
    /// sign-flip permutation p-value (exact when n is at most 20, otherwise Monte Carlo).
    /// </summary>
    public static PairedComparison Compare(IReadOnlyList<(double A, double B)> pairs, int resamples = 10000, int seed = DefaultSeed)
    {
        var n = pairs.Count;
        if (n == 0)
            return new PairedComparison(0, 0, 0, 0, 0, 0, 1);

        var diffs = pairs.Select(p => p.B - p.A).ToArray();
        var observed = diffs.Average();

        var rng = new Random(seed);
        var means = new double[resamples];
        for (var i = 0; i < resamples; i++)
        {
            double sum = 0;
            for (var j = 0; j < n; j++)
                sum += diffs[rng.Next(n)];
            means[i] = sum / n;
        }
        Array.Sort(means);

        return new PairedComparison(
            n, pairs.Average(p => p.A), pairs.Average(p => p.B), observed,
            Quantile(means, 0.025), Quantile(means, 0.975),
            SignFlipPValue(diffs, observed, seed));
    }

    private static double SignFlipPValue(double[] diffs, double observed, int seed)
    {
        const double tolerance = 1e-12;
        var n = diffs.Length;
        var target = Math.Abs(observed) - tolerance;

        if (n <= 20)
        {
            var total = 1L << n;
            long atLeastAsExtreme = 0;
            for (long mask = 0; mask < total; mask++)
            {
                double sum = 0;
                for (var i = 0; i < n; i++)
                    sum += (mask & (1L << i)) != 0 ? -diffs[i] : diffs[i];
                if (Math.Abs(sum / n) >= target)
                    atLeastAsExtreme++;
            }
            return (double)atLeastAsExtreme / total;
        }

        var rng = new Random(seed + 7);
        const int draws = 100_000;
        var count = 0;
        for (var d = 0; d < draws; d++)
        {
            double sum = 0;
            for (var i = 0; i < n; i++)
                sum += rng.Next(2) == 0 ? diffs[i] : -diffs[i];
            if (Math.Abs(sum / n) >= target)
                count++;
        }
        return (count + 1.0) / (draws + 1.0); // +1: a Monte Carlo p-value is never exactly zero
    }

    private static double Quantile(double[] sorted, double q)
    {
        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}
