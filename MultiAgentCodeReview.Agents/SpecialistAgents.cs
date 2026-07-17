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

        response = StripThinkingTags(response);

        // Try 1: Direct JSON parse
        try
        {
            var cleaned = CleanJsonResponse(response);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<AgentResult>(cleaned, options);
            if (result != null && result.Findings.Count > 0)
                return result;
        }
        catch { }

        // Try 2: Extract JSON from mixed text (LLM may wrap JSON in explanation)
        try
        {
            var extracted = ExtractJsonFromText(response);
            if (!string.IsNullOrEmpty(extracted))
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<AgentResult>(extracted, options);
                if (result != null && result.Findings.Count > 0)
                    return result;
            }
        }
        catch { }

        // Try 3: Fallback to text parsing
        var findings = ExtractFindingsFromText(response);
        return new AgentResult(findings, response);
    }

    private static string StripThinkingTags(string response)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            response,
            @"<thinking>[\s\S]*?</thinking>\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
    }

    private static List<Finding> ExtractFindingsFromText(string response)
    {
        var findings = new List<Finding>();
        var lines = response.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart(' ', '-', '*', '#');
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

                // Try to extract file and line from surrounding context
                var file = "";
                int line = 0;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 3); j++)
                {
                    var contextLine = lines[j];
                    // Look for file:line pattern
                    var match = System.Text.RegularExpressions.Regex.Match(contextLine, @"([a-zA-Z0-9_/\.\-]+\.(cs|js|ts|py|java|go|rb|cpp|c|h|tsx|jsx)):(\d+)");
                    if (match.Success)
                    {
                        file = match.Groups[1].Value;
                        int.TryParse(match.Groups[3].Value, out line);
                        break;
                    }
                }

                findings.Add(new Finding(
                    severity,
                    FindingCategory.CodeSmell,
                    file,
                    line,
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

    private static string ExtractJsonFromText(string response)
    {
        // Try to find a JSON object or array in mixed text
        // Look for the first { or [ that starts a valid JSON structure
        var patterns = new[]
        {
            // Look for JSON object with "findings" key
            @"\{[\s\S]*""findings""[\s\S]*\}",
            // Look for any JSON object
            @"\{[\s\S]*\}",
            // Look for JSON array
            @"\[[\s\S]*\]"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(response, pattern, System.Text.RegularExpressions.RegexOptions.Singleline);
            if (match.Success)
            {
                var candidate = match.Value;
                // Validate it's somewhat reasonable length
                if (candidate.Length > 20 && candidate.Length < response.Length)
                {
                    return candidate;
                }
            }
        }

        return "";
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
                var numbered = InjectLineNumbers(hunk);
                if (totalChars + numbered.Length > maxChars)
                {
                    var remaining = maxChars - totalChars;
                    if (remaining > 100)
                        sb.AppendLine(numbered.Substring(0, remaining) + "\n... [truncated]");
                    sb.AppendLine($"... [{context.Diff.Files.Count - context.Diff.Files.IndexOf(fileDiff)} more files truncated]");
                    return sb.ToString();
                }
                sb.AppendLine(numbered);
                totalChars += numbered.Length;
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string InjectLineNumbers(Hunk hunk)
    {
        var sb = new System.Text.StringBuilder();
        var lines = hunk.Content.Split('\n');
        int newLine = hunk.NewStart;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("@@"))
            {
                sb.AppendLine(line);
                continue;
            }

            if (line.Length == 0)
            {
                sb.AppendLine(line);
                continue;
            }

            char prefix = line[0];
            string rest = line.Length > 1 ? line.Substring(1) : "";

            switch (prefix)
            {
                case ' ':
                    sb.AppendLine($"[Line {newLine}] {rest}");
                    newLine++;
                    break;
                case '+':
                    sb.AppendLine($"[Line {newLine}] +{rest}");
                    newLine++;
                    break;
                case '-':
                    sb.AppendLine($"[-] -{rest}");
                    break;
                default:
                    sb.AppendLine(line);
                    break;
            }
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
        sb.AppendLine("Analyze the following code changes and project structure for technical debt and modernization opportunities:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        sb.AppendLine();

        // Add project structure context for broader modernization suggestions
        sb.AppendLine("=== PROJECT STRUCTURE ===");
        var allFiles = context.ChangedFiles.Select(f => f.Path).ToList();
        var csprojFiles = allFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        var csFiles = allFiles.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var configFiles = allFiles.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                               f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                                               f.EndsWith(".config", StringComparison.OrdinalIgnoreCase)).ToList();

        sb.AppendLine($"Total changed files: {allFiles.Count}");
        if (csprojFiles.Any())
        {
            sb.AppendLine($"Project files (.csproj): {string.Join(", ", csprojFiles)}");
            sb.AppendLine("Look for: outdated target frameworks, deprecated package references, missing nullable enable");
        }
        if (configFiles.Any())
        {
            sb.AppendLine($"Config files: {string.Join(", ", configFiles)}");
            sb.AppendLine("Look for: legacy configuration patterns, missing modern config providers");
        }
        if (csFiles.Any())
        {
            sb.AppendLine($"C# files: {csFiles.Count} files");
            sb.AppendLine("Look for: missing modern C# features, legacy patterns, outdated APIs");
        }

        sb.AppendLine();
        sb.AppendLine("=== CODE CHANGES ===");
        sb.AppendLine(BuildDiffContent(context, maxChars: 6000));

        sb.AppendLine();
        sb.AppendLine("=== MODERNIZATION ANALYSIS INSTRUCTIONS ===");
        sb.AppendLine("1. Analyze the diff for immediate legacy patterns in changed code");
        sb.AppendLine("2. Based on project structure, suggest PROJECT-WIDE modernization opportunities");
        sb.AppendLine("3. For each finding, explain: what is legacy, what is modern, effort to migrate");
        sb.AppendLine("4. End your summary with a 'Modernization Roadmap' listing prioritized migration steps");
        sb.AppendLine("5. Consider: target framework version, package freshness, C# language version, architecture patterns");
        sb.AppendLine();
        sb.AppendLine("Return JSON with findings array and summary. Include modernizationContext for each finding.");
        return sb.ToString();
    }
}
