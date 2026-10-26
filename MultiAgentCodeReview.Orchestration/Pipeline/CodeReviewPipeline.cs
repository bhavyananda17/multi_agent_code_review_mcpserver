using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Text;
using MultiAgentCodeReview.Orchestration.Text;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

public record ReviewOutput(PipelineContext Context, AgentResult Result);

/// <summary>What stages 1-4 produced: the context, the deduplicated findings (not yet verified), and any warnings so far.</summary>
public record GenerationSnapshot(PipelineContext Context, AgentResult Result, List<string> Warnings, TokenUsage? GenerationTokens);

public class CodeReviewPipeline
{
    private readonly FilterStage _filterStage;
    private readonly GroundingStage _groundingStage;
    private readonly EvaluationStage _evaluationStage;
    private readonly AgentFactory _agentFactory;
    private readonly PipelineConfig _config;
    private readonly ILogger<CodeReviewPipeline>? _logger;

    public AgentFactory AgentFactory => _agentFactory;

    public CodeReviewPipeline(
        FilterStage filterStage,
        GroundingStage groundingStage,
        EvaluationStage evaluationStage,
        AgentFactory agentFactory,
        IOptions<PipelineConfig> config,
        ILogger<CodeReviewPipeline>? logger = null)
    {
        _filterStage = filterStage;
        _groundingStage = groundingStage;
        _evaluationStage = evaluationStage;
        _agentFactory = agentFactory;
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the whole review: <see cref="GenerateAsync"/> (filter, triage, specialists, dedup) followed by
    /// <see cref="VerifyAsync"/> (grounding check and the LLM judge).
    /// </summary>
    public async Task<ReviewOutput> RunReviewAsync(
        string repositoryPath,
        string commitHash,
        string? baseCommit = null,
        IssueContext? issue = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = TokenUsageScope.Begin();
        var snapshot = await GenerateAsync(repositoryPath, commitHash, baseCommit, issue, cancellationToken);
        var output = await VerifyAsync(snapshot, includeGenerationTokens: false, cancellationToken);

        // The outer scope saw every call of both halves.
        var total = scope.Snapshot();
        return output with { Context = output.Context with { TokenUsage = total.Calls > 0 ? total : null } };
    }

    /// <summary>
    /// Stages 1-4: everything that costs specialist tokens. The result can be saved and later passed to
    /// <see cref="VerifyAsync"/> any number of times, so verification settings (the judge model, the threshold, the
    /// grounding check) can be compared on identical findings without regenerating them.
    /// </summary>
    public async Task<GenerationSnapshot> GenerateAsync(
        string repositoryPath,
        string commitHash,
        string? baseCommit = null,
        IssueContext? issue = null,
        CancellationToken cancellationToken = default)
    {
        using var usage = TokenUsageScope.Begin();
        _logger?.LogInformation("Starting code review for {Repo} at {Commit}", repositoryPath, commitHash);
        // Anything that makes this review less complete than it should be (a specialist that timed out, a judge that
        // could not score) is recorded here and shown on the report, never swallowed.
        var warnings = new ConcurrentQueue<string>();
        if (issue != null)
            _logger?.LogInformation("Linked issue: {Owner}/{Repo}#{Number}", issue.Owner, issue.Repo, issue.Number);

        _logger?.LogInformation("Stage 1: Filtering relevant files...");
        var context = await _filterStage.ExecuteAsync(
            repositoryPath, commitHash, baseCommit,
            _config.Filtering.MaxFiles,
            _config.Filtering.MinFiles,
            cancellationToken,
            issue,
            _config.Filtering.StaticAnalysis);

        _logger?.LogInformation("Filtered to {Count} relevant files", context.ChangedFiles.Count);

        if (context.ChangedFiles.Count == 0)
        {
            _logger?.LogWarning("No relevant files found in diff. Skipping analysis.");
            return new GenerationSnapshot(context, new AgentResult(new List<Finding>(), "No relevant files found in the commit diff. Nothing to review."), new List<string>(), null);
        }

        _logger?.LogInformation("Stage 2: Triaging changes...");
        var triageAgent = _agentFactory.CreateTriageAgent();
        var triageResult = await triageAgent.ClassifyAsync(context, cancellationToken);
        _logger?.LogInformation("Triage: {Classifications} -> Route to {Agents} (Priority: {Priority})",
            string.Join(", ", triageResult.Classifications),
            string.Join(", ", triageResult.RouteTo),
            triageResult.Priority);

        _logger?.LogInformation("Stage 3: Running specialist agents in parallel...");
        var specialistResults = await RunSpecialistsAsync(triageResult.RouteTo, context, warnings, cancellationToken);

        _logger?.LogInformation("Stage 4: Synthesizing findings (C# dedup)...");
        var finalResult = SynthesizeFindings(specialistResults, context);

        var tokens = usage.Snapshot();
        return new GenerationSnapshot(context, finalResult, warnings.ToList(), tokens.Calls > 0 ? tokens : null);
    }

    /// <summary>
    /// Stages 5-6: a deterministic check that each finding quotes code that really exists, then the LLM judge.
    /// </summary>
    /// <param name="includeGenerationTokens">Add the snapshot's generation tokens to the reported usage (false when only the verification spend is wanted).</param>
    public async Task<ReviewOutput> VerifyAsync(
        GenerationSnapshot snapshot,
        bool includeGenerationTokens = true,
        CancellationToken cancellationToken = default)
    {
        using var usage = TokenUsageScope.Begin();
        var context = snapshot.Context;
        var warnings = new List<string>(snapshot.Warnings);

        var groundedResult = await _groundingStage.ExecuteAsync(snapshot.Result, context, cancellationToken);

        var (evaluatedResult, evaluationSummary) = await _evaluationStage.ExecuteAsync(groundedResult, context, cancellationToken);
        if (evaluationSummary != null)
        {
            context = context with { EvaluationSummary = evaluationSummary };
            if (evaluationSummary.Unscored > 0)
                warnings.Add($"The judge could not score {evaluationSummary.Unscored} of {evaluationSummary.Scored + evaluationSummary.Unscored} findings; they are reported unscored.");
        }
        if (warnings.Count > 0)
            context = context with { Warnings = warnings };

        var tokens = usage.Snapshot();
        if (includeGenerationTokens)
            tokens = (snapshot.GenerationTokens ?? TokenUsage.Empty).Plus(tokens);
        context = context with { TokenUsage = tokens.Calls > 0 ? tokens : null };

        _logger?.LogInformation("Review complete. Found {Count} findings", evaluatedResult.Findings.Count);

        return new ReviewOutput(context, evaluatedResult);
    }

    private async Task<List<(string AgentName, AgentResult Result)>> RunSpecialistsAsync(
        List<string> routeTo,
        PipelineContext context,
        ConcurrentQueue<string> warnings,
        CancellationToken cancellationToken)
    {
        var agents = new List<ISpecialistAgent>();

        if (routeTo.Contains("SecurityAgent"))
            agents.Add(_agentFactory.CreateSecurityAgent());
        if (routeTo.Contains("PerformanceAgent"))
            agents.Add(_agentFactory.CreatePerformanceAgent());
        if (routeTo.Contains("LogicAgent"))
            agents.Add(_agentFactory.CreateLogicAgent());

        if (agents.Count == 0)
        {
            agents.AddRange(new ISpecialistAgent[]
            {
                _agentFactory.CreateSecurityAgent(),
                _agentFactory.CreatePerformanceAgent(),
                _agentFactory.CreateLogicAgent()
            });
        }

        _logger?.LogInformation("Running {Count} specialist agents in parallel: {Agents}",
            agents.Count, string.Join(", ", agents.Select(a => a.Name)));

        var tasks = agents.Select(async (agent, index) =>
        {
            if (index > 0)
                await Task.Delay(15000 * index, cancellationToken);
            var result = await AnalyzeWithRetryAsync(agent, context, cancellationToken, warnings);
            _logger?.LogInformation("  {Agent} completed with {Count} findings", agent.Name, result.Findings.Count);
            return (agent.Name, Result: result);
        });

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private AgentResult SynthesizeFindings(List<(string AgentName, AgentResult Result)> specialistResults, PipelineContext context)
    {
        var allTaggedFindings = specialistResults
            .SelectMany(pair => pair.Result.Findings
                .Select(f => f with { Agents = new List<string> { pair.AgentName } }))
            .ToList();

        var locatedFindings = allTaggedFindings
            .Where(f => !string.IsNullOrEmpty(f.File) && f.Line > 0)
            .ToList();

        var unlocatedFindings = allTaggedFindings
            .Where(f => string.IsNullOrEmpty(f.File) || f.Line <= 0)
            .ToList();

        var grouped = locatedFindings
            .GroupBy(f => new { f.File, f.Line })
            .ToList();

        var dedupedFindings = new List<Finding>();
        int multiAgentFindings = 0;

        foreach (var group in grouped)
        {
            var findings = group.ToList();

            if (findings.Count == 1)
            {
                dedupedFindings.Add(findings[0]);
                continue;
            }

            var distinctAgents = findings
                .SelectMany(f => f.Agents ?? new List<string>())
                .Distinct()
                .ToList();

            if (distinctAgents.Count == 1)
            {
                // Severity is declared Critical, High, Medium, Low, so the MOST severe is the smallest value.
                var highest = findings.OrderBy(f => f.Severity).First();
                dedupedFindings.Add(highest);
                continue;
            }

            var topFinding = findings.OrderBy(f => f.Severity).First();
            var mergedMessages = findings
                .Select(f => $"[{string.Join(", ", f.Agents ?? new List<string>())}]: {f.Description}")
                .ToList();

            var mergedRecommendations = findings
                .Where(f => !string.IsNullOrEmpty(f.Recommendation))
                .Select(f => f.Recommendation)
                .Distinct()
                .ToList();

            var merged = topFinding with
            {
                // Keeps the most severe of the group. (The old "boost to Critical" compared against the enum the
                // wrong way round and so never fired; agreement between agents is reported, not escalated.)
                Severity = topFinding.Severity,
                Description = string.Join("\n\n", mergedMessages),
                Recommendation = mergedRecommendations.Any()
                    ? string.Join("\n", mergedRecommendations)
                    : topFinding.Recommendation,
                Agents = distinctAgents
            };

            multiAgentFindings++;
            dedupedFindings.Add(merged);
        }

        var lowConfidenceFindings = dedupedFindings
            .Where(f => f.Confidence < 0.6)
            .ToList();

        unlocatedFindings.AddRange(lowConfidenceFindings);

        dedupedFindings.RemoveAll(f => f.Confidence < 0.6);

        var withoutLocation = unlocatedFindings.Count - lowConfidenceFindings.Count;
        unlocatedFindings = MergeNearDuplicates(unlocatedFindings, out var mergedDuplicates);

        var sorted = dedupedFindings
            .Concat(unlocatedFindings)
            .OrderBy(f => f.Severity) // most severe first
            .ThenByDescending(f => f.Confidence)
            .ThenBy(f => f.File)
            .ThenBy(f => f.Line)
            .ToList();

        var critCount = sorted.Count(f => f.Severity == Severity.Critical);
        var highCount = sorted.Count(f => f.Severity == Severity.High);
        var medCount = sorted.Count(f => f.Severity == Severity.Medium);
        var lowCount = sorted.Count(f => f.Severity == Severity.Low);
        var fileCount = sorted.Select(f => f.File).Where(f => !string.IsNullOrEmpty(f)).Distinct().Count();

        var summary = new StringBuilder();
        summary.Append($"Reviewed {context.ChangedFiles.Count} files. ");
        summary.Append($"Found {sorted.Count} findings across {fileCount} files: ");
        summary.Append($"{critCount} critical, {highCount} high, {medCount} medium, {lowCount} low.");
        if (lowConfidenceFindings.Count > 0)
            summary.Append($" {lowConfidenceFindings.Count} finding(s) with agent confidence < 0.6 are kept but listed without a line number.");
        if (withoutLocation > 0)
            summary.Append($" {withoutLocation} finding(s) could not be matched to a specific line.");
        if (mergedDuplicates > 0)
            summary.Append($" {mergedDuplicates} near-duplicate finding(s) without a line number were merged.");
        if (multiAgentFindings > 0)
            summary.Append($" {multiAgentFindings} finding(s) were reported by more than one agent.");

        return new AgentResult(sorted, summary.ToString());
    }

    /// <summary>
    /// Two findings in the same file whose wording overlaps at least this much (Dice, see TextSimilarity) are treated as
    /// the same finding. Chosen from real benchmark output: genuine duplicates scored 0.40 and 0.62, distinct findings in
    /// the same file 0.00 to 0.33. It is the lowest value that merges those duplicates, the margin is thin, and a wrong
    /// merge costs more than a missed one, so do not lower it without more data.
    /// </summary>
    internal const double NearDuplicateThreshold = 0.4;

    /// <summary>
    /// Findings without a usable line skip the (file, line) grouping, so the same complaint reported twice
    /// (by two agents, or twice by one) used to appear twice. Merge near-duplicates in the same file by wording,
    /// keeping the most severe version and the union of the agents that reported it. The category is ignored
    /// on purpose: unrecognised categories all fall back to one value and would make unrelated findings look alike.
    /// </summary>
    internal static List<Finding> MergeNearDuplicates(List<Finding> findings, out int merged)
    {
        merged = 0;
        var kept = new List<(Finding Finding, HashSet<string> Tokens)>();

        foreach (var finding in findings.OrderBy(f => f.Severity).ThenByDescending(f => f.Confidence))
        {
            var tokens = TextSimilarity.Tokenize(finding.Description);
            var i = kept.FindIndex(k =>
                string.Equals(k.Finding.File, finding.File, StringComparison.OrdinalIgnoreCase) &&
                TextSimilarity.Dice(k.Tokens, tokens) >= NearDuplicateThreshold);

            if (i < 0)
            {
                kept.Add((finding, tokens));
                continue;
            }

            merged++;
            var agents = (kept[i].Finding.Agents ?? new List<string>()).Union(finding.Agents ?? new List<string>()).ToList();
            kept[i] = (kept[i].Finding with { Agents = agents }, kept[i].Tokens);
        }

        return kept.Select(k => k.Finding).ToList();
    }

    private async Task<AgentResult> AnalyzeWithRetryAsync(
        ISpecialistAgent agent, PipelineContext context, CancellationToken ct, ConcurrentQueue<string> warnings)
    {
        const int attempts = 3;
        var timeout = TimeSpan.FromSeconds(_config.Pipeline.AgentTimeoutSeconds);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                return await agent.AnalyzeAsync(context, linkedCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("Agent {Agent} timed out after {Seconds}s on attempt {Attempt}", agent.Name, timeout.TotalSeconds, attempt + 1);
                if (attempt < attempts - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_config.Pipeline.TimeoutRetryDelaySeconds), ct);
                    continue;
                }
                warnings.Enqueue($"{agent.Name} timed out on all {attempts} attempts ({timeout.TotalSeconds:0}s each); its findings are missing from this review.");
                return new AgentResult(new List<Finding>(), $"Agent {agent.Name} timed out");
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt < attempts - 1)
                {
                    var delay = TimeSpan.FromSeconds((attempt + 1) * _config.Pipeline.TransientRetryDelaySeconds);
                    _logger?.LogWarning("Transient error on {Agent}, retrying in {Delay}s: {Error}", agent.Name, delay.TotalSeconds, FirstLine(ex.Message));
                    await Task.Delay(delay, ct);
                    continue;
                }
                warnings.Enqueue($"{agent.Name} failed on all {attempts} attempts with a transient error ({FirstLine(ex.Message)}); its findings are missing from this review.");
                return new AgentResult(new List<Finding>(), $"Agent {agent.Name} failed: transient error");
            }
            // Anything else (bad key, retired model, a bug) is not retried and is not hidden: it fails the review loudly.
        }

        throw new InvalidOperationException("unreachable");
    }

    internal static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 140 ? line[..140] + "..." : line;
    }

    /// <summary>A rate limit, or a server-side hiccup that is worth retrying (502/503/504, "over capacity").</summary>
    internal static bool IsTransient(Exception ex)
    {
        if (IsRateLimit(ex))
            return true;

        var msg = ex.Message + ex.InnerException?.Message + ex.InnerException?.InnerException?.Message;
        return System.Text.RegularExpressions.Regex.IsMatch(msg, @"\b50[234]\b")
            || msg.Contains("over capacity", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("service unavailable", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("bad gateway", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsRateLimit(Exception ex)
    {
        var msg = ex.Message + ex.InnerException?.Message + ex.InnerException?.InnerException?.Message;
        // Match the actual signals. A bare "rate" also matches "generate", "operate", "iterate"...
        return msg.Contains("429")
            || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("too many requests", StringComparison.OrdinalIgnoreCase);
    }
}
