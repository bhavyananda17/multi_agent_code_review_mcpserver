using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

/// <summary>One benchmark input: a commit plus the findings human reviewers actually left on it.</summary>
public record BenchmarkRow(int RowNumber, string RepoUrl, string CommitSha, List<string> GroundTruth);

public class BenchmarkOptions
{
    /// <summary>Minimum text similarity (0-1) for a pipeline finding to count as matching a ground-truth finding.</summary>
    public double SimilarityThreshold { get; set; } = FindingMatcher.DefaultThreshold;

    /// <summary>
    /// false (default): a pipeline finding is correct if it matches ANY ground-truth finding, and a ground-truth
    /// finding is recalled if ANY pipeline finding matches it (duplicates can inflate precision).
    /// true: greedy one-to-one pairing, so each finding on either side is used at most once.
    /// </summary>
    public bool OneToOne { get; set; }

    /// <summary>Run the generation stages for real and save them here (one JSON file per row); verification then runs as usual.</summary>
    public string? RecordDirectory { get; set; }

    /// <summary>
    /// Do not call the specialists: load the generations saved by an earlier <see cref="RecordDirectory"/> run and run
    /// only the verification stages (grounding check, judge). Findings are then identical across runs, so judge and
    /// grounding settings can be compared like for like, and only the judge spends tokens.
    /// </summary>
    public string? ReplayDirectory { get; set; }

    /// <summary>Score flagged findings too (low self-evaluation score, or quoted code not found). Default: only what the report shows.</summary>
    public bool IncludeLowConfidence { get; set; }
}

public record PredictedFinding(
    string Severity,
    string Category,
    string File,
    int Line,
    string Description,
    string? Recommendation,
    bool LowConfidence,
    int? SelfEvalTotal,
    bool? Grounded = null,
    string? CodeSnippet = null);

public record MatchedPair(PredictedFinding Finding, string GroundTruth, double Similarity);

public record BenchmarkRowResult(
    int Row,
    string RepoUrl,
    string CommitSha,
    string Status,
    string? Error,
    double PipelineSeconds,
    int PredictedCount,
    int GroundTruthCount,
    int ExcludedLowConfidence,
    int MatchedPredictions,
    int MatchedGroundTruth,
    double? Precision,
    double? Recall,
    double? F1,
    string? PipelineSummary,
    List<MatchedPair> Matches,
    List<PredictedFinding> UnmatchedPredictions,
    List<string> UnmatchedGroundTruth,
    List<string>? Warnings = null,
    TokenUsage? Tokens = null);

public record Metrics(double Precision, double Recall, double F1);

public record MicroMetrics(
    int Predicted,
    int GroundTruth,
    int MatchedPredictions,
    int MatchedGroundTruth,
    double Precision,
    double Recall,
    double F1);

public record BenchmarkAggregate(int Rows, int Succeeded, int Failed, Metrics? Macro, MicroMetrics? Micro, MetricIntervals? MacroCi95 = null, int DegradedRows = 0, int TotalTokens = 0);

public record BenchmarkSettings(
    double SimilarityThreshold,
    string MatchMode,
    bool IncludeLowConfidence,
    string Similarity,
    bool EvaluationEnabled,
    int EvaluationThreshold,
    bool EvaluationExcludesLowConfidence,
    Dictionary<string, string> ModelOverrides,
    bool GroundingEnabled = true,
    bool StaticAnalysisEnabled = true,
    int DiffCharBudget = 0,
    string? JudgeModel = null,
    string? JudgeRelation = null,
    int AgentTimeoutSeconds = 0,
    string? Mode = null);

public record BenchmarkResult(
    DateTime GeneratedAtUtc,
    bool Interrupted,
    BenchmarkSettings Settings,
    BenchmarkAggregate Aggregate,
    List<BenchmarkRowResult> Results);
