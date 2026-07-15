using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;
using System.Text.Json;

namespace MultiAgentCodeReview.Agents;

public abstract class BaseSpecialistAgent : MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent
{
    protected readonly IAgent _agent;
    protected readonly string _agentName;

    public string Name => _agentName;
    public abstract List<string> TriggerCategories { get; }

    protected BaseSpecialistAgent(IAgent agent, string name)
    {
        _agent = agent;
        _agentName = name;
    }

    public async Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        var userPrompt = BuildPrompt(context);
        var messages = new IMessage[] { new TextMessage(Role.User, userPrompt) };
        var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return ParseResponse(content ?? "");
    }

    protected abstract string BuildPrompt(PipelineContext context);

    protected virtual AgentResult ParseResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new AgentResult(new List<Finding>(), "No response from agent");

        try
        {
            var cleaned = CleanJsonResponse(response);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<AgentResult>(cleaned, options);
            if (result != null && result.Findings.Count > 0)
                return result;
        }
        catch { }

        var findings = ExtractFindingsFromText(response);
        return new AgentResult(findings, response);
    }

    private static List<Finding> ExtractFindingsFromText(string response)
    {
        var findings = new List<Finding>();
        var lines = response.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart(' ', '-', '*', '#');
            if (trimmed.Contains("severity:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("[high]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("[medium]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("[low]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("[critical]", StringComparison.OrdinalIgnoreCase))
            {
                var severity = trimmed.Contains("critical", StringComparison.OrdinalIgnoreCase) ? Severity.Critical
                    : trimmed.Contains("high", StringComparison.OrdinalIgnoreCase) ? Severity.High
                    : trimmed.Contains("medium", StringComparison.OrdinalIgnoreCase) ? Severity.Medium
                    : Severity.Low;
                findings.Add(new Finding(
                    severity,
                    FindingCategory.CodeSmell,
                    "",
                    0,
                    trimmed.Length > 200 ? trimmed.Substring(0, 200) : trimmed,
                    "",
                    "",
                    new FixExample("", ""),
                    0.5));
            }
        }
        return findings;
    }

    protected static string CleanJsonResponse(string response)
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

    protected static string BuildContextSummary(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        foreach (var file in context.ChangedFiles)
        {
            sb.AppendLine($"  {file.Path} (+{file.Additions}/-{file.Deletions}) [{file.ChangeType}]");
        }
        return sb.ToString();
    }

    protected static string BuildDiffContent(PipelineContext context, int maxChars = 4000)
    {
        if (context.Diff == null) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Code Changes:");
        int totalChars = 0;
        foreach (var fileDiff in context.Diff.Files)
        {
            sb.AppendLine($"--- {fileDiff.Path} ---");
            foreach (var hunk in fileDiff.Hunks)
            {
                if (totalChars + hunk.Content.Length > maxChars)
                {
                    var remaining = maxChars - totalChars;
                    if (remaining > 100)
                        sb.AppendLine(hunk.Content.Substring(0, remaining) + "\n... [truncated]");
                    sb.AppendLine($"... [{context.Diff.Files.Count - context.Diff.Files.IndexOf(fileDiff)} more files truncated]");
                    return sb.ToString();
                }
                sb.AppendLine(hunk.Content);
                totalChars += hunk.Content.Length;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

public class SecurityAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Security/", "Auth/", "Crypto/", "Validation/"];

    public SecurityAgent(IAgent agent) : base(agent, "SecurityAgent") { }

    protected override string BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for security vulnerabilities:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        sb.AppendLine(BuildDiffContent(context));
        sb.AppendLine("Focus on: SQL injection, XSS, auth bypass, sensitive data exposure, crypto weaknesses, input validation, dependency vulnerabilities.");
        sb.AppendLine("Return JSON with findings array and summary.");
        return sb.ToString();
    }
}

public class LogicAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Controllers/", "Services/", "Models/", "Logic/"];

    public LogicAgent(IAgent agent) : base(agent, "LogicAgent") { }

    protected override string BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for logic correctness, code quality, and maintainability:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        sb.AppendLine(BuildDiffContent(context));
        sb.AppendLine("Check: Logic errors, edge cases, null handling, complexity (>10), code smells, SOLID violations, testability, naming, error handling.");
        sb.AppendLine("Return JSON with findings array and summary. Include metrics (complexity, LOC, nesting).");
        return sb.ToString();
    }
}

public class PerformanceAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Database/", "Repository/", "DataAccess/", "Services/"];

    public PerformanceAgent(IAgent agent) : base(agent, "PerformanceAgent") { }

    protected override string BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for performance bottlenecks:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        sb.AppendLine(BuildDiffContent(context));
        sb.AppendLine("Focus: N+1 queries, missing indexes, SELECT *, queries in loops, no pagination, blocking calls (.Result/.Wait()), async void, memory leaks, large allocations, string concat in loops, O(n2) algorithms, repeated LINQ, missing caching.");
        sb.AppendLine("Quantify impact: 'Adds 200ms', 'N+1: 1+N queries instead of 1'. Return JSON with findings, impact estimates, optimized code examples.");
        return sb.ToString();
    }
}

public class ModernizationAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Legacy/", "Old/", "Deprecated/"];

    public ModernizationAgent(IAgent agent) : base(agent, "ModernizationAgent") { }

    protected override string BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for technical debt and modernization opportunities:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        sb.AppendLine(BuildDiffContent(context));
        sb.AppendLine("Focus: Outdated frameworks, legacy patterns, missing modern C#, outdated NuGet packages, architecture debt, missing tests.");
        sb.AppendLine("Return JSON with findings, modernizationContext for each finding.");
        return sb.ToString();
    }
}
