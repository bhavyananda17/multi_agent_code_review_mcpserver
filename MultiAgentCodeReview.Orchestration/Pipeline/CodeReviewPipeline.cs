using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

public record ReviewOutput(PipelineContext Context, AgentResult Result);

public class CodeReviewPipeline
{
    private readonly FilterStage _filterStage;
    private readonly AgentFactory _agentFactory;
    private readonly PipelineConfig _config;
    private readonly ILogger<CodeReviewPipeline>? _logger;

    public AgentFactory AgentFactory => _agentFactory;

    public CodeReviewPipeline(
        FilterStage filterStage,
        AgentFactory agentFactory,
        IOptions<PipelineConfig> config,
        ILogger<CodeReviewPipeline>? logger = null)
    {
        _filterStage = filterStage;
        _agentFactory = agentFactory;
        _config = config.Value;
        _logger = logger;
    }

    public async Task<ReviewOutput> RunReviewAsync(
        string repositoryPath,
        string commitHash,
        string? baseCommit = null,
        CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting code review for {Repo} at {Commit}", repositoryPath, commitHash);

        _logger?.LogInformation("Stage 1: Filtering relevant files...");
        var context = await _filterStage.ExecuteAsync(
            repositoryPath, commitHash, baseCommit,
            _config.Filtering.MaxFiles,
            _config.Filtering.MinFiles,
            cancellationToken);

        _logger?.LogInformation("Filtered to {Count} relevant files", context.ChangedFiles.Count);

        _logger?.LogInformation("Stage 2: Triaging changes...");
        var triageAgent = _agentFactory.CreateTriageAgent();
        var triageResult = await triageAgent.ClassifyAsync(context, cancellationToken);
        _logger?.LogInformation("Triage: {Classifications} -> Route to {Agents} (Priority: {Priority})",
            string.Join(", ", triageResult.Classifications),
            string.Join(", ", triageResult.RouteTo),
            triageResult.Priority);

        _logger?.LogInformation("Stage 3: Running specialist agents in parallel...");
        var specialistResults = await RunSpecialistsAsync(triageResult.RouteTo, context, cancellationToken);

        _logger?.LogInformation("Stage 4: Synthesizing findings (C# dedup)...");
        var finalResult = SynthesizeFindings(specialistResults, context);
        _logger?.LogInformation("Review complete. Found {Count} findings", finalResult.Findings.Count);

        return new ReviewOutput(context, finalResult);
    }

    private async Task<List<(string AgentName, AgentResult Result)>> RunSpecialistsAsync(
        List<string> routeTo,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var agents = new List<ISpecialistAgent>();

        if (routeTo.Contains("SecurityAgent"))
            agents.Add(_agentFactory.CreateSecurityAgent());
        if (routeTo.Contains("PerformanceAgent"))
            agents.Add(_agentFactory.CreatePerformanceAgent());
        if (routeTo.Contains("ModernizationAgent"))
            agents.Add(_agentFactory.CreateModernizationAgent());

        if (agents.Count == 0)
        {
            agents.AddRange(new ISpecialistAgent[]
            {
                _agentFactory.CreateSecurityAgent(),
                _agentFactory.CreatePerformanceAgent(),
                _agentFactory.CreateModernizationAgent()
            });
        }

        _logger?.LogInformation("Running {Count} specialist agents in parallel: {Agents}",
            agents.Count, string.Join(", ", agents.Select(a => a.Name)));

        var tasks = agents.Select(async agent =>
        {
            var result = await AnalyzeWithRetryAsync(agent, context, cancellationToken);
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

        var grouped = allTaggedFindings
            .GroupBy(f => new { f.File, f.Line })
            .ToList();

        var dedupedFindings = new List<Finding>();
        int crossAgentBoosts = 0;

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
                var highest = findings.OrderByDescending(f => f.Severity).First();
                dedupedFindings.Add(highest);
                continue;
            }

            var topFinding = findings.OrderByDescending(f => f.Severity).First();
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
                Severity = topFinding.Severity < Severity.Critical ? Severity.Critical : topFinding.Severity,
                Description = string.Join("\n\n", mergedMessages),
                Recommendation = mergedRecommendations.Any()
                    ? string.Join("\n", mergedRecommendations)
                    : topFinding.Recommendation,
                Agents = distinctAgents
            };

            crossAgentBoosts++;
            dedupedFindings.Add(merged);
        }

        var sorted = dedupedFindings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File)
            .ThenBy(f => f.Line)
            .ToList();

        var critCount = sorted.Count(f => f.Severity == Severity.Critical);
        var highCount = sorted.Count(f => f.Severity == Severity.High);
        var medCount = sorted.Count(f => f.Severity == Severity.Medium);
        var lowCount = sorted.Count(f => f.Severity == Severity.Low);
        var fileCount = sorted.Select(f => f.File).Distinct().Count();

        var summary = new StringBuilder();
        summary.Append($"Reviewed {context.ChangedFiles.Count} files. ");
        summary.Append($"Found {sorted.Count} findings across {fileCount} files: ");
        summary.Append($"{critCount} critical, {highCount} high, {medCount} medium, {lowCount} low.");
        if (crossAgentBoosts > 0)
            summary.Append($" {crossAgentBoosts} finding(s) boosted to Critical via cross-agent agreement.");

        return new AgentResult(sorted, summary.ToString());
    }

    private async Task<AgentResult> AnalyzeWithRetryAsync(
        ISpecialistAgent agent, PipelineContext context, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await agent.AnalyzeAsync(context, ct);
            }
            catch (Exception ex) when (attempt < 2 && ex.Message.Contains("429"))
            {
                var delay = (attempt + 1) * 15000;
                _logger?.LogWarning("Rate limited on {Agent}, retrying in {Delay}s", agent.Name, delay / 1000);
                await Task.Delay(delay, ct);
            }
        }
        return await agent.AnalyzeAsync(context, ct);
    }
}
