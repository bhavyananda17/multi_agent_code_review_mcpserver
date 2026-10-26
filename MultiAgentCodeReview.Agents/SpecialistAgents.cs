using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;
using System.Diagnostics;
using System.Text.Json;

namespace MultiAgentCodeReview.Agents;

/// <summary>Which slice of the Roslyn analysis a specialist gets.</summary>
public enum AnalysisFocus
{
    Logic,
    Performance,
    Security
}

public abstract class BaseSpecialistAgent : MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent
{
    /// <summary>Keeps the analysis block inside the free-tier token budget (~350 tokens).</summary>
    public const int MaxAnalysisChars = 1400;

    private const int ComplexityThreshold = 10;

    /// <summary>Methods simpler than this are noise in the Logic agent's prompt budget (Performance uses the threshold itself).</summary>
    private const int LogicComplexityFloor = 5;

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
        var (userPrompt, validLines) = BuildPrompt(context);
        var messages = new IMessage[] { new TextMessage(Role.User, userPrompt) };
        var sw = Stopwatch.StartNew();
        Console.Error.WriteLine($"[{_agentName}] Sending request ({userPrompt.Length} chars)...");
        var response = await _agent.GenerateReplyAsync(messages, cancellationToken: cancellationToken);
        sw.Stop();
        Console.Error.WriteLine($"[{_agentName}] Got response in {sw.Elapsed.TotalSeconds:F1}s");
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        var result = ParseResponse(content ?? "", validLines);
        return FillMissingQuickFixes(result);
    }

    protected abstract (string prompt, Dictionary<string, HashSet<int>> validLines) BuildPrompt(PipelineContext context);

    protected virtual AgentResult ParseResponse(string response, Dictionary<string, HashSet<int>> validLines)
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
                return ClampInvalidLines(result, validLines);
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
                    return ClampInvalidLines(result, validLines);
            }
        }
        catch { }

        // Try 3: Fallback to text parsing
        var findings = ExtractFindingsFromText(response);
        return ClampInvalidLines(new AgentResult(findings, response), validLines);
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

    /// <summary>
    /// The part of <see cref="PipelineContext.CodeAnalysis"/> that is relevant to <paramref name="focus"/>, as a
    /// prompt block; empty (so the prompt is unchanged) when there is nothing to say.
    /// Logic gets complexity, callers and all smells; Performance gets only high-complexity methods, callers and
    /// the async-void / long-method smells; Security gets only callers (how exposed a changed method is).
    /// </summary>
    public static string BuildAnalysisContext(PipelineContext context, AnalysisFocus focus)
    {
        var report = context.CodeAnalysis;
        if (report == null)
            return "";

        var sections = new List<string>();

        var complexityFloor = focus == AnalysisFocus.Logic ? LogicComplexityFloor : ComplexityThreshold;
        var complexity = report.Complexity
            .Where(c => focus != AnalysisFocus.Security && c.Complexity >= complexityFloor)
            .OrderByDescending(c => c.Complexity)
            .Select(c => $"- {c.FilePath}: {ShortName(c.MethodName)} (line {c.StartLine}) = {c.Complexity}" +
                         (c.Complexity > ComplexityThreshold ? $" (over {ComplexityThreshold})" : ""))
            .ToList();
        if (complexity.Count > 0)
            sections.Add($"Cyclomatic complexity of touched methods (only {complexityFloor}+ shown; threshold {ComplexityThreshold}):\n" + string.Join("\n", complexity));

        var callers = report.CallSites
            .OrderByDescending(c => c.Callers.Count)
            .Select(c => $"- {c.FilePath}: {ShortName(c.MethodName)} (line {c.StartLine}) is called by " +
                         string.Join(", ", c.Callers.Take(4).Select(x => $"{ShortName(x.CallingMethod)} (line {x.Line})")) +
                         (c.Callers.Count > 4 ? $" and {c.Callers.Count - 4} more" : ""))
            .ToList();
        if (callers.Count > 0)
            sections.Add("Callers within the same file (callers in OTHER files are not detected, so no entry here does not mean unused):\n" + string.Join("\n", callers));

        var smells = report.CodeSmells
            .Where(s => focus == AnalysisFocus.Logic || (focus == AnalysisFocus.Performance && (s.Type is "AsyncVoid" or "LongMethod")))
            .OrderBy(s => s.Severity)
            .Select(s => $"- [{s.Severity}] {s.Type} at {s.FilePath}:{s.Line}: {Clip(s.Description, 160)}")
            .ToList();
        if (smells.Count > 0)
            sections.Add("Code smells in the touched code:\n" + string.Join("\n", smells));

        if (sections.Count == 0)
            return "";

        var block = "Static analysis (measured by a Roslyn pass over the methods this change touches; treat as facts and prefer them to your own estimates):\n"
                    + string.Join("\n", sections);
        return block.Length > MaxAnalysisChars ? block[..MaxAnalysisChars] + "\n... [static analysis truncated]" : block;
    }

    // Appended AFTER the diff is built: validLines (the line numbers a finding may cite) come from the diff
    // text alone, so nothing in the analysis block can widen what the model is allowed to point at.
    protected static void AppendAnalysis(System.Text.StringBuilder sb, PipelineContext context, AnalysisFocus focus)
    {
        var block = BuildAnalysisContext(context, focus);
        if (block.Length > 0)
        {
            sb.AppendLine(block);
            sb.AppendLine();
        }
    }

    private static string ShortName(string identifier) => Clip(identifier, 80);

    private static string Clip(string text, int max) => text.Length > max ? text[..max] + "..." : text;

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

    protected static (string content, Dictionary<string, HashSet<int>> validLines) BuildDiffContent(PipelineContext context, int maxChars = 4000)
    {
        if (context.Diff == null) return ("", new Dictionary<string, HashSet<int>>());
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
                    return (sb.ToString(), ExtractValidLineNumbers(sb.ToString()));
                }
                sb.AppendLine(numbered);
                totalChars += numbered.Length;
            }
            sb.AppendLine();
        }
        var diffContent = sb.ToString();
        return (diffContent, ExtractValidLineNumbers(diffContent));
    }

    private static Dictionary<string, HashSet<int>> ExtractValidLineNumbers(string diffContent)
    {
        var result = new Dictionary<string, HashSet<int>>();
        string currentFile = "";

        foreach (var line in diffContent.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("--- ") && trimmed.EndsWith(" ---"))
            {
                currentFile = trimmed.Substring(4, trimmed.Length - 8);
                if (!result.ContainsKey(currentFile))
                    result[currentFile] = new HashSet<int>();
            }

            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"\[Line (\d+)\]");
            if (match.Success && !string.IsNullOrEmpty(currentFile))
            {
                if (int.TryParse(match.Groups[1].Value, out var lineNum))
                {
                    result[currentFile].Add(lineNum);
                }
            }
        }

        return result;
    }

    private static AgentResult ClampInvalidLines(AgentResult result, Dictionary<string, HashSet<int>> validLines)
    {
        if (validLines.Count == 0)
            return result;

        var clamped = result.Findings.Select(f =>
        {
            if (!string.IsNullOrEmpty(f.File) && f.Line > 0)
            {
                if (validLines.TryGetValue(f.File, out var lines) && !lines.Contains(f.Line))
                    return f with { Line = 0 };
            }
            return f;
        }).ToList();

        return new AgentResult(clamped, result.Summary);
    }

    private static AgentResult FillMissingQuickFixes(AgentResult result)
    {
        var filled = result.Findings.Select(f =>
        {
            if (!string.IsNullOrEmpty(f.QuickFix))
                return f;

            var fallback = f.Recommendation?.Trim().TrimEnd('.');
            if (!string.IsNullOrEmpty(fallback))
                return f with { QuickFix = fallback + "." };

            return f;
        }).ToList();

        return new AgentResult(filled, result.Summary);
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

    protected override (string prompt, Dictionary<string, HashSet<int>> validLines) BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for security vulnerabilities:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        var (diffContent, validLines) = BuildDiffContent(context);
        sb.AppendLine(diffContent);
        AppendAnalysis(sb, context, AnalysisFocus.Security);
        sb.AppendLine("Focus on: SQL injection, XSS, auth bypass, sensitive data exposure, crypto weaknesses, input validation, dependency vulnerabilities.");
        sb.AppendLine("Return JSON with findings array and summary.");
        return (sb.ToString(), validLines);
    }
}

public class PerformanceAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Database/", "Repository/", "DataAccess/", "Services/"];

    public PerformanceAgent(IAgent agent) : base(agent, "PerformanceAgent") { }

    protected override (string prompt, Dictionary<string, HashSet<int>> validLines) BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for performance bottlenecks:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        var (diffContent, validLines) = BuildDiffContent(context);
        sb.AppendLine(diffContent);
        AppendAnalysis(sb, context, AnalysisFocus.Performance);
        sb.AppendLine("Focus: N+1 queries, missing indexes, SELECT *, queries in loops, no pagination, blocking calls (.Result/.Wait()), async void, memory leaks, large allocations, string concat in loops, O(n2) algorithms, repeated LINQ, missing caching.");
        sb.AppendLine("Quantify impact: 'Adds 200ms', 'N+1: 1+N queries instead of 1'. Return JSON with findings, impact estimates, optimized code examples.");
        return (sb.ToString(), validLines);
    }
}

public class LogicAgent : BaseSpecialistAgent
{
    public override List<string> TriggerCategories { get; } = ["Controllers/", "Services/", "Models/", "Logic/"];

    public LogicAgent(IAgent agent) : base(agent, "LogicAgent") { }

    protected override (string prompt, Dictionary<string, HashSet<int>> validLines) BuildPrompt(PipelineContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following code changes for logic correctness, code quality, and maintainability:");
        sb.AppendLine();
        sb.AppendLine(BuildContextSummary(context));
        var (diffContent, validLines) = BuildDiffContent(context);
        sb.AppendLine(diffContent);
        AppendAnalysis(sb, context, AnalysisFocus.Logic);
        sb.AppendLine("Focus on: Logic errors, null reference risks, unhandled exceptions, edge cases, complexity >10, code smells, SOLID violations, poor naming, swallowed exceptions, missing validation, testability, duplicated code.");
        sb.AppendLine("Quantify: 'cyclomatic complexity of 15 (target <10)', 'method spans 120 lines (max 50)'. Return JSON with findings array and summary.");
        return (sb.ToString(), validLines);
    }
}
