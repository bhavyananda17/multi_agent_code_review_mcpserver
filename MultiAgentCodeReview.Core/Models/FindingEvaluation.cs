namespace MultiAgentCodeReview.Core.Models;

/// <summary>
/// Self-evaluation of a single <see cref="Finding"/>: each dimension is scored 1 (poor) to 3 (good).
/// These are LLM judgments, not ground truth — they exist to rank/flag findings, not to prove them.
/// </summary>
public record FindingEvaluation(
    int Relevance,
    int SeverityAccuracy,
    int Actionability,
    string? Rationale = null,
    bool LowConfidence = false)
{
    public const int MinScore = 1;
    public const int MaxScore = 3;
    public const int MaxTotal = MaxScore * 3;

    public int Total => Relevance + SeverityAccuracy + Actionability;
}

/// <summary>What the self-evaluation stage did for one review; carried on <see cref="PipelineContext"/>.</summary>
public record EvaluationSummary(
    int Threshold,
    int Scored,
    int Unscored,
    int LowConfidence,
    bool LowConfidenceExcluded);
