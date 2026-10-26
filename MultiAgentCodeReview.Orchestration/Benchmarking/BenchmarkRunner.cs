using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

/// <summary>
/// Runs the full review pipeline over each benchmark row and scores its findings against the human
/// ground truth. Single use: <see cref="RunAsync"/> disposes the repo provider (and its temp clones).
/// A row that fails (clone error, unknown SHA, pipeline exception) is recorded and the run continues;
/// failed rows are excluded from the aggregate rather than scored as zero.
/// </summary>
public class BenchmarkRunner
{
    private static readonly Regex UrlUserInfo = new(@"(?<=://)[^/@\s]+@", RegexOptions.Compiled);

    private readonly IBenchmarkRepoProvider _repos;
    private readonly CodeReviewPipeline _pipeline;
    private readonly PipelineConfig _config;

    public BenchmarkRunner(IBenchmarkRepoProvider repos, CodeReviewPipeline pipeline, IOptions<PipelineConfig> config)
    {
        _repos = repos;
        _pipeline = pipeline;
        _config = config.Value;
    }

    /// <param name="log">Human-readable progress lines.</param>
    /// <param name="onRowCompleted">Called after every row with the results so far (lets the caller persist partial output).</param>
    public async Task<BenchmarkResult> RunAsync(
        IReadOnlyList<BenchmarkRow> rows,
        BenchmarkOptions options,
        Action<string>? log = null,
        Action<BenchmarkResult>? onRowCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BenchmarkRowResult>();
        var interrupted = false;

        try
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                log?.Invoke($"[{row.RowNumber}/{rows.Count}] {Redact(row.RepoUrl)} @ {row.CommitSha[..Math.Min(8, row.CommitSha.Length)]} " +
                            $"({row.GroundTruth.Count} ground-truth finding(s))");

                var result = await RunRowAsync(row, options, cancellationToken);
                results.Add(result);

                log?.Invoke(result.Status == "ok"
                    ? $"      {result.PredictedCount} finding(s) in {result.PipelineSeconds:F1}s -> " +
                      $"precision {Pct(result.Precision)}, recall {Pct(result.Recall)}, F1 {Pct(result.F1)}"
                    : $"      FAILED: {result.Error}");

                onRowCompleted?.Invoke(Build(results, options, interrupted: false));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            interrupted = true;
        }
        finally
        {
            await _repos.DisposeAsync();
        }

        return Build(results, options, interrupted);
    }

    private async Task<BenchmarkRowResult> RunRowAsync(BenchmarkRow row, BenchmarkOptions options, CancellationToken cancellationToken)
    {
        var url = Redact(row.RepoUrl);
        try
        {
            var path = await _repos.PrepareAsync(row.RepoUrl, row.CommitSha, cancellationToken);

            var sw = Stopwatch.StartNew();
            var output = await _pipeline.RunReviewAsync(path, row.CommitSha, null, null, cancellationToken);
            sw.Stop();

            return Score(row, url, options, output.Result, sw.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a URL with embedded credentials reach the results file.
            var message = ex.Message.Replace(row.RepoUrl, url);
            return new BenchmarkRowResult(
                row.RowNumber, url, row.CommitSha, "error", message, 0, 0, row.GroundTruth.Count, 0, 0, 0,
                null, null, null, null, new(), new(), new());
        }
    }

    /// <summary>Scores one review. Public so the matching/metrics can be tested without running a pipeline.</summary>
    public static BenchmarkRowResult Score(
        BenchmarkRow row, string displayUrl, BenchmarkOptions options, AgentResult result, double pipelineSeconds)
    {
        var all = result.Findings ?? new List<Finding>();
        var scored = options.IncludeLowConfidence ? all : all.Where(f => f.Evaluation?.LowConfidence != true).ToList();
        var predicted = scored.Select(ToPredicted).ToList();

        var outcome = FindingMatcher.Match(predicted, row.GroundTruth, options.SimilarityThreshold, options.OneToOne);
        var metrics = FindingMatcher.Score(predicted.Count, row.GroundTruth.Count, outcome.MatchedPredictions.Count, outcome.MatchedGroundTruth.Count);

        return new BenchmarkRowResult(
            row.RowNumber, displayUrl, row.CommitSha, "ok", null,
            Math.Round(pipelineSeconds, 2),
            predicted.Count, row.GroundTruth.Count, all.Count - scored.Count,
            outcome.MatchedPredictions.Count, outcome.MatchedGroundTruth.Count,
            metrics.Precision, metrics.Recall, metrics.F1,
            result.Summary,
            outcome.Pairs.Select(p => new MatchedPair(predicted[p.Prediction], row.GroundTruth[p.GroundTruth], Math.Round(p.Similarity, 3))).ToList(),
            predicted.Where((_, i) => !outcome.MatchedPredictions.Contains(i)).ToList(),
            row.GroundTruth.Where((_, i) => !outcome.MatchedGroundTruth.Contains(i)).ToList());
    }

    public static BenchmarkAggregate Aggregate(IReadOnlyList<BenchmarkRowResult> results)
    {
        var ok = results.Where(r => r.Status == "ok").ToList();
        if (ok.Count == 0)
            return new BenchmarkAggregate(results.Count, 0, results.Count, null, null);

        // Macro: every PR counts equally (mean of the per-PR numbers). Micro: pool all findings, then compute once,
        // so PRs with more findings weigh more.
        var macro = new Metrics(ok.Average(r => r.Precision!.Value), ok.Average(r => r.Recall!.Value), ok.Average(r => r.F1!.Value));

        int predicted = ok.Sum(r => r.PredictedCount), truth = ok.Sum(r => r.GroundTruthCount);
        int matchedP = ok.Sum(r => r.MatchedPredictions), matchedG = ok.Sum(r => r.MatchedGroundTruth);
        var m = FindingMatcher.Score(predicted, truth, matchedP, matchedG);

        return new BenchmarkAggregate(
            results.Count, ok.Count, results.Count - ok.Count, macro,
            new MicroMetrics(predicted, truth, matchedP, matchedG, m.Precision, m.Recall, m.F1));
    }

    private BenchmarkResult Build(List<BenchmarkRowResult> results, BenchmarkOptions options, bool interrupted)
    {
        var settings = new BenchmarkSettings(
            options.SimilarityThreshold,
            options.OneToOne ? "one-to-one" : "any-match",
            options.IncludeLowConfidence,
            "Dice coefficient on stop-word-filtered, stemmed, camelCase-split tokens; >= 2 shared tokens required",
            _config.Evaluation.Enabled,
            _config.Evaluation.Threshold,
            _config.Evaluation.ExcludeLowConfidence,
            _config.Models.ToDictionary(kv => kv.Key, kv => kv.Value.ModelId));

        return new BenchmarkResult(DateTime.UtcNow, interrupted, settings, Aggregate(results), results.ToList());
    }

    private static PredictedFinding ToPredicted(Finding f) => new(
        f.Severity.ToString(), f.Category.ToString(), f.File, f.Line, f.Description, f.Recommendation,
        f.Evaluation?.LowConfidence == true, f.Evaluation?.Total);

    /// <summary>Strips "user:token@" from a URL so credentials never appear in output or logs.</summary>
    public static string Redact(string url) => UrlUserInfo.Replace(url, "***@");

    private static string Pct(double? v) => v is { } x ? $"{x:P0}" : "n/a";
}
