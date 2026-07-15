using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

public class CodeReviewPipeline
{
    private readonly FilterStage _filterStage;
    private readonly AgentFactory _agentFactory;
    private readonly PipelineConfig _config;
    private readonly ILogger<CodeReviewPipeline>? _logger;

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

    public async Task<AgentResult> RunReviewAsync(
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

        _logger?.LogInformation("Stage 3: Running specialist agents...");
        var specialistResults = await RunSpecialistsAsync(triageResult.RouteTo, context, cancellationToken);

        _logger?.LogInformation("Stage 4: Synthesizing findings...");
        var synthesisAgent = _agentFactory.CreateSynthesisAgent();
        var finalResult = await synthesisAgent.SynthesizeAsync(specialistResults, context, cancellationToken);
        _logger?.LogInformation("Review complete. Found {Count} findings", finalResult.Findings.Count);

        _logger?.LogInformation("Stage 5: Generating documentation...");
        _ = Task.Run(async () =>
        {
            try
            {
                var docAgent = _agentFactory.CreateDocumentationAgent();
                await docAgent.GenerateDocumentationAsync(context, finalResult, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Documentation generation failed");
            }
        }, cancellationToken);

        return finalResult;
    }

    private async Task<List<AgentResult>> RunSpecialistsAsync(
        List<string> routeTo,
        PipelineContext context,
        CancellationToken cancellationToken)
    {
        var results = new List<AgentResult>();
        var agents = new List<ISpecialistAgent>();

        if (routeTo.Contains("SecurityAgent"))
            agents.Add(_agentFactory.CreateSecurityAgent());
        if (routeTo.Contains("LogicAgent"))
            agents.Add(_agentFactory.CreateLogicAgent());
        if (routeTo.Contains("PerformanceAgent"))
            agents.Add(_agentFactory.CreatePerformanceAgent());
        if (routeTo.Contains("ModernizationAgent"))
            agents.Add(_agentFactory.CreateModernizationAgent());

        if (agents.Count == 0)
        {
            agents.AddRange(new ISpecialistAgent[]
            {
                _agentFactory.CreateSecurityAgent(),
                _agentFactory.CreateLogicAgent(),
                _agentFactory.CreatePerformanceAgent(),
                _agentFactory.CreateModernizationAgent()
            });
        }

        foreach (var agent in agents)
        {
            var result = await AnalyzeWithRetryAsync(agent, context, cancellationToken);
            results.Add(result);
            await Task.Delay(2000, cancellationToken);
        }

        return results;
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
