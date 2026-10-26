using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

/// <summary>
/// Stage 5: after synthesis, an LLM scores every finding on relevance, severity accuracy and
/// actionability (1-3 each). Findings whose total is below the configured threshold are flagged
/// low-confidence and either kept for a separate report section or dropped.
/// Fails open: if scoring is unavailable or a finding can't be scored, it is reported as-is —
/// an evaluator outage must never silently hide findings.
/// </summary>
public class EvaluationStage
{
    private readonly AgentFactory _agentFactory;
    private readonly PipelineConfig _pipelineConfig;
    private readonly EvaluationConfig _config;
    private readonly ILogger<EvaluationStage>? _logger;

    public EvaluationStage(
        AgentFactory agentFactory,
        IOptions<PipelineConfig> config,
        ILogger<EvaluationStage>? logger = null)
    {
        _agentFactory = agentFactory;
        _pipelineConfig = config.Value;
        _config = config.Value.Evaluation;
        _logger = logger;
    }

    /// <returns>The (possibly filtered) result plus a summary, or a null summary when the stage did not run.</returns>
    public async Task<(AgentResult Result, EvaluationSummary? Summary)> ExecuteAsync(
        AgentResult result,
        PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || result.Findings.Count == 0)
            return (result, null);

        _logger?.LogInformation("Stage 5: Self-evaluating {Count} findings...", result.Findings.Count);
        var sw = Stopwatch.StartNew();

        var threshold = Math.Clamp(_config.Threshold, 0, FindingEvaluation.MaxTotal);
        var batchSize = Math.Max(1, _config.BatchSize);
        var findings = result.Findings;

        var evaluations = new List<FindingEvaluation?>(findings.Count);
        IEvalAgent? evaluator = null;
        try
        {
            evaluator = _agentFactory.CreateEvalAgent();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning("Self-evaluation unavailable, reporting findings unscored: {Error}", ex.Message);
        }

        for (var i = 0; i < findings.Count; i += batchSize)
        {
            var batch = findings.Skip(i).Take(batchSize).ToList();
            evaluations.AddRange(evaluator == null
                ? Enumerable.Repeat<FindingEvaluation?>(null, batch.Count)
                : await ScoreBatchAsync(evaluator, context, batch, cancellationToken));
        }

        var updated = new List<Finding>(findings.Count);
        int scored = 0, lowConfidence = 0;
        for (var i = 0; i < findings.Count; i++)
        {
            var evaluation = evaluations[i];
            if (evaluation == null)
            {
                updated.Add(findings[i]);
                continue;
            }

            var flagged = evaluation.Total < threshold;
            scored++;
            if (flagged) lowConfidence++;
            updated.Add(findings[i] with { Evaluation = evaluation with { LowConfidence = flagged } });
        }

        var unscored = findings.Count - scored;
        var kept = _config.ExcludeLowConfidence
            ? updated.Where(f => f.Evaluation?.LowConfidence != true).ToList()
            : updated;

        _logger?.LogInformation(
            "Self-evaluation done in {Seconds:F1}s: {Scored} scored, {Unscored} unscored, {Low} low-confidence (< {Threshold}/{Max})",
            sw.Elapsed.TotalSeconds, scored, unscored, lowConfidence, threshold, FindingEvaluation.MaxTotal);

        // LLM judges tend to favour output from their own model/family, so say which kind of judge this was.
        var judge = AgentFactory.EffectiveJudgeModelId(_pipelineConfig);
        var generators = AgentFactory.GeneratorModelIds(_pipelineConfig);
        var sameModel = generators.Contains(judge, StringComparer.OrdinalIgnoreCase);
        var sameFamily = generators.Any(g => AgentFactory.ModelFamily(g) == AgentFactory.ModelFamily(judge));
        if (sameFamily)
            _logger?.LogWarning("Self-evaluation judge {Judge} is {Kind} the models that wrote the findings; its scores may be biased towards them. Set MULTIAGENT_MODEL_EVAL to a model from a different family.",
                judge, sameModel ? "the same model as one of" : "from the same family as");

        var summary = new EvaluationSummary(threshold, scored, unscored, lowConfidence, _config.ExcludeLowConfidence, judge, sameModel, sameFamily);
        return (new AgentResult(kept, AppendNote(result.Summary, summary, findings.Count)), summary);
    }

    private async Task<List<FindingEvaluation?>> ScoreBatchAsync(
        IEvalAgent evaluator,
        PipelineContext context,
        List<Finding> batch,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_pipelineConfig.Pipeline.AgentTimeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                var scores = await evaluator.EvaluateAsync(context, batch, linkedCts.Token);

                // Keep the result aligned with the batch even if an implementation returns too few/many.
                var aligned = batch.Select((_, i) => i < scores.Count ? scores[i] : null).ToList();
                if (aligned.All(s => s == null))
                    _logger?.LogWarning("Evaluator returned no usable scores for a batch of {Count} findings", batch.Count);
                return aligned;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < 2 && (ex is OperationCanceledException || CodeReviewPipeline.IsTransient(ex)))
            {
                var delay = ex is OperationCanceledException
                    ? TimeSpan.FromSeconds(_pipelineConfig.Pipeline.TimeoutRetryDelaySeconds)
                    : TimeSpan.FromSeconds((attempt + 1) * _pipelineConfig.Pipeline.TransientRetryDelaySeconds);
                _logger?.LogWarning("Evaluator attempt {Attempt} failed ({Reason}), retrying in {Delay}s",
                    attempt + 1, ex is OperationCanceledException ? "timeout" : "transient error", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Evaluator failed for a batch of {Count} findings, reporting them unscored: {Error}",
                    batch.Count, ex.Message);
                break;
            }
        }

        return batch.Select(_ => (FindingEvaluation?)null).ToList();
    }

    private static string AppendNote(string summary, EvaluationSummary s, int total)
    {
        string note;
        if (s.Scored == 0)
        {
            note = "Self-evaluation was enabled but no findings could be scored; all findings are reported as-is.";
        }
        else
        {
            var flagged = s.LowConfidence == 0
                ? "none flagged low-confidence"
                : $"{s.LowConfidence} flagged low-confidence and {(s.LowConfidenceExcluded ? "excluded from this report" : "shown in a separate section")}";
            note = $"Self-evaluation scored {s.Scored}/{total} findings (threshold {s.Threshold}/{FindingEvaluation.MaxTotal}); {flagged}.";
            if (s.Unscored > 0)
                note += $" {s.Unscored} could not be scored and are reported as-is.";
        }

        note += s.JudgeSameModelAsGenerators
            ? $" Judge: {s.JudgeModel}, the same model that wrote these findings (same-model judges tend to favour their own output; set MULTIAGENT_MODEL_EVAL to a different model family for an independent judge)."
            : s.JudgeSameFamilyAsGenerators
                ? $" Judge: {s.JudgeModel}, from the same model family as a generator (possible self-preference; a different family is more independent)."
                : $" Judge: {s.JudgeModel} (a different model family from the generators).";

        return string.IsNullOrWhiteSpace(summary) ? note : $"{summary} {note}";
    }
}
