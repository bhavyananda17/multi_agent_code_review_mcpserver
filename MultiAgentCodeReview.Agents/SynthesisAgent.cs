using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;
using System.Text.Json;

namespace MultiAgentCodeReview.Agents;

public class SynthesisAgent : MultiAgentCodeReview.Core.Interfaces.ISynthesisAgent
{
    public string Name => "SynthesisAgent";

    private readonly IAgent _agent;

    public SynthesisAgent(IAgent agent)
    {
        _agent = agent;
    }

    public async Task<AgentResult> SynthesizeAsync(
        List<AgentResult> specialistResults,
        PipelineContext context,
        CancellationToken cancellationToken = default)
    {
        var allFindings = specialistResults
            .SelectMany(r => r.Findings)
            .ToList();

        var userPrompt = BuildSynthesisPrompt(specialistResults, context);
        var response = await _agent.GenerateReplyAsync(
            new[] { new TextMessage(Role.User, userPrompt) },
            cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return new AgentResult(allFindings, content ?? "No summary generated");
    }

    public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResult(
            new List<Finding>(),
            "Synthesis agent requires specialist results"));
    }

    private string BuildSynthesisPrompt(List<AgentResult> specialistResults, PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Synthesize findings from specialist agents into a final code review report:");
        sb.AppendLine();
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        sb.AppendLine();

        var agentNames = new[] { "Security", "Logic", "Performance", "Modernization" };
        for (int i = 0; i < specialistResults.Count && i < agentNames.Length; i++)
        {
            var result = specialistResults[i];
            sb.AppendLine($"=== {agentNames[i]} Agent Findings ===");
            sb.AppendLine($"Summary: {result.Summary}");
            sb.AppendLine($"Findings Count: {result.Findings.Count}");

            foreach (var finding in result.Findings.Take(10))
            {
                sb.AppendLine($"  - [{finding.Severity}] {finding.Category} in {finding.File}:{finding.Line} - {finding.Description}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Perform: 1) Deduplication 2) Meta-insight extraction 3) Effort estimation 4) Prioritization 5) Fix sequencing");
        sb.AppendLine("Output: Markdown report with Executive Summary, Critical/High/Medium/Low sections, Meta-Insights, Positive Findings, Affected Areas Table.");

        return sb.ToString();
    }

    private AgentResult ParseSynthesisResponse(string response)
    {
        try
        {
            var cleaned = CleanJsonResponse(response);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<AgentResult>(cleaned, options)
                   ?? new AgentResult(new List<Finding>(), response);
        }
        catch
        {
            return new AgentResult(new List<Finding>(), response);
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
}
