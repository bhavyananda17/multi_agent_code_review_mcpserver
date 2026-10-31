using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

public static class BenchmarkReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A data file, not HTML: keep ' and >= readable instead of \u0027 / \u003E.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson(BenchmarkResult result) => JsonSerializer.Serialize(result, JsonOptions);

    public static string ToMarkdown(BenchmarkResult result)
    {
        var s = result.Settings;
        var a = result.Aggregate;
        var sb = new StringBuilder();

        sb.AppendLine("# Benchmark summary");
        sb.AppendLine();
        sb.AppendLine($"- **Generated:** {result.GeneratedAtUtc:yyyy-MM-dd HH:mm:ss} UTC" + (result.Interrupted ? " - **interrupted, partial results**" : ""));
        sb.AppendLine($"- **Rows:** {a.Rows} ({a.Succeeded} scored, {a.Failed} failed" + (a.DegradedRows > 0 ? $", {a.DegradedRows} scored with pipeline warnings" : "") + ")");
        sb.AppendLine($"- **Matching:** text similarity ≥ {F(s.SimilarityThreshold)} ({s.Similarity}); mode `{s.MatchMode}`");
        sb.AppendLine($"- **Findings scored:** {(s.IncludeLowConfidence ? "all, including flagged ones" : "reported findings only (flagged ones excluded)")}");
        sb.AppendLine($"- **Pipeline:** static analysis {(s.StaticAnalysisEnabled ? "on" : "off")}; grounding check {(s.GroundingEnabled ? "on" : "off")}; " +
                      $"self-evaluation {(s.EvaluationEnabled ? $"on (threshold {s.EvaluationThreshold}/9{(s.EvaluationExcludesLowConfidence ? ", flagged findings dropped" : "")})" : "off")}" +
                      (s.DiffCharBudget > 0 ? $"; diff window {s.DiffCharBudget} characters" : "") +
                      (s.AgentTimeoutSeconds > 0 ? $"; agent timeout {s.AgentTimeoutSeconds}s per attempt" : ""));
        if (s.Mode == "replay")
            sb.AppendLine("- **Mode:** replay - findings come from an earlier recorded run; only grounding and the judge ran, so times and tokens below cover verification only");
        else if (s.Mode == "record")
            sb.AppendLine("- **Mode:** record - generations were saved so verification settings can later be replayed on identical findings");
        if (a.TotalTokens > 0)
            sb.AppendLine($"- **Tokens:** {a.TotalTokens:N0} in total, about {a.TotalTokens / Math.Max(1, a.Succeeded):N0} per scored PR (counted from each response's own usage field)");
        if (s.EvaluationEnabled && s.JudgeModel != null)
            sb.AppendLine($"- **Judge:** `{s.JudgeModel}` ({s.JudgeRelation})");
        if (s.ModelOverrides.Count > 0)
            sb.AppendLine($"- **Model overrides:** {string.Join(", ", s.ModelOverrides.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"))}");
        sb.AppendLine();

        sb.AppendLine("| # | Repo | Commit | Findings | Ground truth | Precision | Recall | F1 | Time (s) | Tokens (k) |");
        sb.AppendLine("|--:|------|--------|---------:|-------------:|----------:|-------:|---:|---------:|-----------:|");
        foreach (var r in result.Results)
        {
            if (r.Status == "ok")
            {
                sb.AppendLine($"| {r.Row} | {Cell(Short(r.RepoUrl))} | `{r.CommitSha[..Math.Min(8, r.CommitSha.Length)]}` | {r.PredictedCount}{(r.Warnings is { Count: > 0 } ? " ⚠" : "")} | {r.GroundTruthCount} | " +
                              $"{F(r.Precision)} | {F(r.Recall)} | {F(r.F1)} | {r.PipelineSeconds.ToString("F1", CultureInfo.InvariantCulture)} | {(r.Tokens is { } t ? (t.Total / 1000.0).ToString("F1", CultureInfo.InvariantCulture) : "-")} |");
            }
            else
            {
                sb.AppendLine($"| {r.Row} | {Cell(Short(r.RepoUrl))} | `{r.CommitSha[..Math.Min(8, r.CommitSha.Length)]}` | - | {r.GroundTruthCount} | - | - | - | failed | - |");
            }
        }

        if (a.Macro is { } macro && a.Micro is { } micro)
        {
            sb.AppendLine($"| | **Macro average** ({a.Succeeded} PRs) | | | | **{F(macro.Precision)}** | **{F(macro.Recall)}** | **{F(macro.F1)}** | | |");
            sb.AppendLine($"| | **Micro (pooled)** | | {micro.Predicted} | {micro.GroundTruth} | **{F(micro.Precision)}** | **{F(micro.Recall)}** | **{F(micro.F1)}** | | |");
        }
        sb.AppendLine();
        if (a.MacroCi95 is { } ci)
        {
            sb.AppendLine($"**Macro 95% bootstrap interval** (resampling the {a.Succeeded} PRs): precision [{F(ci.Precision.Low)}, {F(ci.Precision.High)}], " +
                          $"recall [{F(ci.Recall.Low)}, {F(ci.Recall.High)}], F1 [{F(ci.F1.Low)}, {F(ci.F1.High)}]. " +
                          "Intervals this wide mean small differences between two runs are mostly noise; use `bench-compare` for a paired test.");
            sb.AppendLine();
        }

        var degraded = result.Results.Where(r => r.Warnings is { Count: > 0 }).ToList();
        if (degraded.Count > 0)
        {
            sb.AppendLine("## ⚠ Rows with pipeline warnings");
            sb.AppendLine();
            sb.AppendLine("A stage degraded on these rows (for example a specialist timed out or the judge could not score), so their scores may be understated and say more about the infrastructure than about the pipeline. `bench-compare --exclude-degraded` leaves them out of a comparison.");
            sb.AppendLine();
            foreach (var r in degraded)
                sb.AppendLine($"- Row {r.Row} - {Short(r.RepoUrl)}: {string.Join(" ", r.Warnings!)}");
            sb.AppendLine();
        }

        var failed = result.Results.Where(r => r.Status != "ok").ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## Failed rows (excluded from the averages)");
            sb.AppendLine();
            foreach (var r in failed)
                sb.AppendLine($"- Row {r.Row} - {Short(r.RepoUrl)} @ `{r.CommitSha[..Math.Min(8, r.CommitSha.Length)]}`: {r.Error?.Replace("\n", " ")}");
            sb.AppendLine();
        }

        sb.AppendLine("## How to read this");
        sb.AppendLine();
        sb.AppendLine("- **Precision** = pipeline findings that match a human finding ÷ pipeline findings. **Recall** = human findings matched by a pipeline finding ÷ human findings. **F1** is their harmonic mean.");
        sb.AppendLine("- A PR with no pipeline findings scores precision 0 (so F1 0). Failed PRs are left out of the averages, not counted as zero.");
        sb.AppendLine("- **Macro** averages the per-PR numbers (every PR weighs the same); **micro** pools all findings first (PRs with more findings weigh more).");
        sb.AppendLine("- Matching is lexical overlap, not understanding: it misses paraphrases and can match unrelated comments that share vocabulary. Review the `matches` and `unmatched*` lists in the JSON file before trusting a number.");
        sb.AppendLine("- Ground truth only holds what reviewers happened to write, so valid pipeline findings they didn't mention count as false positives; treat precision as a lower bound.");

        return sb.ToString();
    }

    private static string F(double v) => v.ToString("0.000", CultureInfo.InvariantCulture);
    private static string F(double? v) => v is { } x ? F(x) : "-";

    private static string Short(string url)
    {
        var u = url.Trim();
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "git@github.com:" })
        {
            if (u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                u = u[prefix.Length..];
                break;
            }
        }
        return u.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? u[..^4] : u;
    }

    private static string Cell(string text) => text.Replace("|", "\\|");
}
