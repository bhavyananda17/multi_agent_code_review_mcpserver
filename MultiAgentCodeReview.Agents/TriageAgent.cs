using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;
using System.Text.Json;

namespace MultiAgentCodeReview.Agents;

public class TriageAgent : MultiAgentCodeReview.Core.Interfaces.ITriageAgent
{
    public string Name => "TriageAgent";

    private readonly IAgent _agent;

    public TriageAgent(IAgent agent)
    {
        _agent = agent;
    }

    public async Task<TriageResult> ClassifyAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var userPrompt = BuildTriagePrompt(context);
        var response = await _agent.GenerateReplyAsync(
            new[] { new TextMessage(Role.User, userPrompt) },
            cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return ParseTriageResponse(content ?? "");
    }

    public async Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var triageResult = await ClassifyAsync(context, cancellationToken);
        return new AgentResult(
            new List<Finding>(),
            $"Triage: {string.Join(", ", triageResult.Classifications)} -> Route to {string.Join(", ", triageResult.RouteTo)}");
    }

    private string BuildTriagePrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Classify the following code changes to route to specialist agents:");
        sb.AppendLine();
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        sb.AppendLine();

        foreach (var file in context.ChangedFiles)
        {
            sb.AppendLine($"  {file.Path} (+{file.Additions}/-{file.Deletions}) [{file.ChangeType}]");
        }

        if (context.Diff != null)
        {
            sb.AppendLine();
            sb.AppendLine("Git Diff Summary:");
            sb.AppendLine(context.Diff.Summary);
        }

        sb.AppendLine();
        sb.AppendLine("Return JSON with classifications, routeTo, priority, and reasoning.");
        sb.AppendLine("Categories: SECURITY_SENSITIVE, LOGIC_CRITICAL, PERFORMANCE_IMPACT, MODERNIZATION_NEEDED");
        sb.AppendLine("RouteTo: SecurityAgent, LogicAgent, PerformanceAgent, ModernizationAgent");
        sb.AppendLine("Priority: CRITICAL, HIGH, MEDIUM, LOW");

        return sb.ToString();
    }

    private TriageResult ParseTriageResponse(string response)
    {
        try
        {
            var cleaned = CleanJsonResponse(response);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<TriageResult>(cleaned, options) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    private static string CleanJsonResponse(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
                trimmed = trimmed.Substring(firstNewline + 1);
            var lastBackticks = trimmed.LastIndexOf("```");
            if (lastBackticks >= 0)
                trimmed = trimmed.Substring(0, lastBackticks);
        }
        return trimmed.Trim();
    }

    private static TriageResult CreateDefault() => new(
        new List<string> { "LOGIC_CRITICAL" },
        new List<string> { "LogicAgent" },
        "MEDIUM",
        "Default fallback classification"
    );
}
