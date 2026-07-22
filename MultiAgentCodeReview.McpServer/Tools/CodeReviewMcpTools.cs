using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.McpServer.Tools;

[McpServerToolType]
public class CodeReviewMcpTools
{
    private readonly CodeReviewPipeline _pipeline;
    private readonly AgentFactory _agentFactory;
    private static readonly Dictionary<string, string> _reportCache = new();
    private static readonly Dictionary<string, ReviewOutput> _pipelineCache = new();
    private const string ReportFileName = ".codereview/last_report.md";

    public CodeReviewMcpTools(CodeReviewPipeline pipeline, AgentFactory agentFactory)
    {
        _pipeline = pipeline;
        _agentFactory = agentFactory;
    }

    [McpServerTool]
    [Description("Run a full multi-agent code review on a git repository. Use this when the user asks to review a commit, check a PR, or audit recent changes. Do NOT use for single-file questions — use ask_codebase instead.")]
    public async Task<string> ReviewRepo(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The commit to review (HEAD, sha, branch name)")] string commit_hash,
        [Description("Base to diff against (defaults to commit_hash~1)")] string base_commit = "")
    {
        var reportDir = Path.Combine(repo_path, ".codereview");
        var reportPath = Path.Combine(reportDir, "last_report.md");
        var metaPath = Path.Combine(reportDir, "last_report.meta.json");

        if (File.Exists(reportPath) && File.Exists(metaPath))
        {
            try
            {
                var metaJson = await File.ReadAllTextAsync(metaPath);
                var meta = JsonSerializer.Deserialize<ReportMeta>(metaJson);
                if (meta?.CommitHash == commit_hash)
                {
                    return await File.ReadAllTextAsync(reportPath);
                }
            }
            catch { /* fall through to re-run pipeline */ }
        }

        var output = await _pipeline.RunReviewAsync(repo_path, commit_hash, string.IsNullOrWhiteSpace(base_commit) ? null : base_commit);

        _pipelineCache[repo_path] = output;

        var report = FormatReport(output);

        _reportCache[repo_path] = report;

        Directory.CreateDirectory(reportDir);
        await File.WriteAllTextAsync(reportPath, report);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(new ReportMeta { CommitHash = commit_hash }));

        return report;
    }

    [McpServerTool]
    [Description("Ask a natural language question about a codebase. Use this for questions like 'where is auth handled?', 'what calls IUserRepository?', or 'which files would break if I change X?'. Do NOT use for full reviews — use review_repo instead.")]
    public async Task<string> AskCodebase(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The natural language question to ask")] string question)
    {
        ReviewOutput output;
        if (_pipelineCache.TryGetValue(repo_path, out var cached))
        {
            output = cached;
        }
        else
        {
            output = await _pipeline.RunReviewAsync(repo_path, "HEAD", "HEAD~1");
            _pipelineCache[repo_path] = output;
        }

        var onboardingAgent = _agentFactory.CreateOnboardingAgent();
        var answer = await onboardingAgent.AnswerAsync(question, output.Context, output.Result);

        return answer;
    }

    [McpServerTool]
    [Description("Generate project documentation for a codebase. Use this when the user asks to generate or update documentation, README, API docs, or architecture docs.")]
    public async Task<string> GenerateDocs(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The commit to document (HEAD, sha, branch name)")] string commit_hash,
        [Description("Base to diff against (defaults to commit_hash~1)")] string base_commit = "")
    {
        ReviewOutput output;
        if (_pipelineCache.TryGetValue(repo_path, out var cached) && cached.Context.CommitHash == commit_hash)
        {
            output = cached;
        }
        else
        {
            output = await _pipeline.RunReviewAsync(repo_path, commit_hash, string.IsNullOrWhiteSpace(base_commit) ? null : base_commit);
            _pipelineCache[repo_path] = output;
        }

        var docAgent = _agentFactory.CreateDocumentationAgent();
        var docs = await docAgent.GenerateDocumentationAsync(output.Context, output.Result);

        var reportsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "CodeReviewReports");
        Directory.CreateDirectory(reportsDir);
        var repoFolder = new DirectoryInfo(repo_path).Name;
        var reportPath = Path.Combine(reportsDir, $"{repoFolder}_AGENT_REPORT.md");
        await File.WriteAllTextAsync(reportPath, docs);

        return $"Documentation saved to {reportPath}\n\n{docs}";
    }

    private static string FormatReport(ReviewOutput output)
    {
        var sb = new StringBuilder();
        var findings = output.Result.Findings;

        sb.AppendLine("## Code Review Report");
        sb.AppendLine();
        sb.AppendLine($"**Repository:** {output.Context.RepositoryPath}");
        sb.AppendLine($"**Commit:** {output.Context.CommitHash}");
        sb.AppendLine($"**Base:** {output.Context.BaseCommit ?? "HEAD~1"}");
        sb.AppendLine($"**Total Findings:** {findings?.Count ?? 0}");
        sb.AppendLine();

        // Health score
        var critCount = findings?.Count(f => f.Severity == Severity.Critical) ?? 0;
        var highCount = findings?.Count(f => f.Severity == Severity.High) ?? 0;
        var medCount = findings?.Count(f => f.Severity == Severity.Medium) ?? 0;
        var lowCount = findings?.Count(f => f.Severity == Severity.Low) ?? 0;

        var score = Math.Max(0, 100 - (critCount * 20) - (highCount * 10) - (medCount * 5) - (lowCount * 2));
        var grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";
        var verdict = grade switch
        {
            "A" => "Excellent — no issues found.",
            "B" => "Good — minor issues, address when convenient.",
            "C" => "Fair — some issues should be addressed this sprint.",
            "D" => "Poor — significant issues need attention.",
            _ => "Critical — do not merge until issues are resolved."
        };

        sb.AppendLine("## Health Score");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| **Score** | {score}/100 |");
        sb.AppendLine($"| **Grade** | {grade} |");
        sb.AppendLine($"| **Critical** | {critCount} |");
        sb.AppendLine($"| **High** | {highCount} |");
        sb.AppendLine($"| **Medium** | {medCount} |");
        sb.AppendLine($"| **Low** | {lowCount} |");
        sb.AppendLine();
        sb.AppendLine($"> Code quality is **{verdict.ToLowerInvariant()}** {verdict}");
        sb.AppendLine();

        // Executive summary from synthesis agent
        sb.AppendLine(output.Result.Summary ?? "No summary available");
        sb.AppendLine();

        if (findings is { Count: > 0 })
        {
            // Group findings by severity
            var critical = findings.Where(f => f.Severity == Severity.Critical).ToList();
            var high = findings.Where(f => f.Severity == Severity.High).ToList();
            var medium = findings.Where(f => f.Severity == Severity.Medium).ToList();
            var low = findings.Where(f => f.Severity == Severity.Low).ToList();

            sb.AppendLine("---");
            sb.AppendLine();

            // Critical findings
            if (critical.Any())
            {
                sb.AppendLine("## CRITICAL - Must Fix Before Merge");
                sb.AppendLine();
                foreach (var finding in critical)
                {
                    FormatFinding(sb, finding);
                }
            }

            // High findings
            if (high.Any())
            {
                sb.AppendLine("## HIGH - Fix Soon");
                sb.AppendLine();
                foreach (var finding in high)
                {
                    FormatFinding(sb, finding);
                }
            }

            // Medium findings
            if (medium.Any())
            {
                sb.AppendLine("## MEDIUM - Address This Sprint");
                sb.AppendLine();
                foreach (var finding in medium)
                {
                    FormatFinding(sb, finding);
                }
            }

            // Low findings
            if (low.Any())
            {
                sb.AppendLine("## LOW - Suggestions");
                sb.AppendLine();
                foreach (var finding in low)
                {
                    FormatFinding(sb, finding);
                }
            }

            // Modernization Roadmap section
            var modernizationFindings = findings.Where(f =>
                f.Category == FindingCategory.LegacyPattern ||
                f.Category == FindingCategory.OutdatedFramework ||
                f.Category == FindingCategory.MissingModernLanguageFeatures ||
                f.Category == FindingCategory.ArchitectureDebt ||
                f.Category == FindingCategory.OutdatedDependencies).ToList();

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Modernization Roadmap");
            sb.AppendLine();

            if (modernizationFindings.Any())
            {
                sb.AppendLine("### Project-Wide Modernization Opportunities");
                sb.AppendLine();
                foreach (var finding in modernizationFindings)
                {
                    FormatModernizationFinding(sb, finding);
                }
            }
            else
            {
                sb.AppendLine("### Modernization Status: No Action Required");
                sb.AppendLine();
                sb.AppendLine("The codebase was analyzed for modernization opportunities including legacy patterns, outdated frameworks, missing modern language features, architecture debt, and outdated dependencies.");
                sb.AppendLine();
                sb.AppendLine("**Result:** No modernization issues detected. The code follows current best practices and uses up-to-date patterns and dependencies.");
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Review completed with no findings.");
        }

        return sb.ToString();
    }

    private static void FormatFinding(StringBuilder sb, Finding finding)
    {
        var location = !string.IsNullOrEmpty(finding.File) && finding.Line > 0
            ? $"`{finding.File}:{finding.Line}`"
            : !string.IsNullOrEmpty(finding.File)
                ? $"`{finding.File}`"
                : "(location not determined)";

        var confidenceEmoji = finding.Confidence >= 0.7 ? "🟢" : finding.Confidence >= 0.4 ? "🟡" : "🔴";

        sb.AppendLine($"### [{finding.Severity}] {finding.Category}");
        sb.AppendLine($"- **File:** {location}");
        if (!string.IsNullOrEmpty(finding.QuickFix))
        {
            sb.AppendLine($"- **Quick fix:** `{finding.QuickFix}`");
        }
        sb.AppendLine($"- **Confidence:** {confidenceEmoji} {finding.Confidence:P0}");
        sb.AppendLine();
        sb.AppendLine(finding.Description);
        sb.AppendLine();

        if (!string.IsNullOrEmpty(finding.Recommendation))
        {
            sb.AppendLine($"**Recommendation:** {finding.Recommendation}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(finding.CodeSnippet))
        {
            sb.AppendLine("**Current Code:**");
            sb.AppendLine("```csharp");
            sb.AppendLine(finding.CodeSnippet);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (finding.FixExample != null && (!string.IsNullOrEmpty(finding.FixExample.Before) || !string.IsNullOrEmpty(finding.FixExample.After)))
        {
            if (!string.IsNullOrEmpty(finding.FixExample.Before))
            {
                sb.AppendLine("**Before (Vulnerable / Problematic):**");
                sb.AppendLine("```csharp");
                sb.AppendLine(finding.FixExample.Before);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(finding.FixExample.After))
            {
                sb.AppendLine("**After (Recommended Fix):**");
                sb.AppendLine("```csharp");
                sb.AppendLine(finding.FixExample.After);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }
        else if (!string.IsNullOrEmpty(finding.CodeSnippet))
        {
            sb.AppendLine("**Suggested Fix:**");
            sb.AppendLine("```csharp");
            sb.AppendLine($"// Apply the recommendation: {finding.Recommendation}");
            sb.AppendLine($"// Refactor the code at {location}");
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (finding.Impact != null && finding.Impact.Count > 0)
        {
            sb.AppendLine("**Impact:**");
            foreach (var kvp in finding.Impact)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
            sb.AppendLine();
        }

        if (finding.Metrics != null && finding.Metrics.Count > 0)
        {
            sb.AppendLine("**Metrics:**");
            foreach (var kvp in finding.Metrics)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
            sb.AppendLine();
        }

        if (finding.References != null && finding.References.Count > 0)
        {
            sb.AppendLine($"**References:** {string.Join(", ", finding.References)}");
            sb.AppendLine();
        }

        sb.AppendLine();
    }

    private static void FormatModernizationFinding(StringBuilder sb, Finding finding)
    {
        var location = !string.IsNullOrEmpty(finding.File) && finding.Line > 0
            ? $"`{finding.File}:{finding.Line}`"
            : !string.IsNullOrEmpty(finding.File)
                ? $"`{finding.File}`"
                : "(project-wide)";

        sb.AppendLine($"#### {finding.Category}: {location}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(finding.Description))
        {
            sb.AppendLine(finding.Description);
            sb.AppendLine();
        }

        if (finding.ModernizationContext != null && finding.ModernizationContext.Count > 0)
        {
            sb.AppendLine("**Modernization Details:**");
            if (finding.ModernizationContext.TryGetValue("legacyPattern", out var legacy))
                sb.AppendLine($"- **Current Pattern:** {legacy}");
            if (finding.ModernizationContext.TryGetValue("modernAlternative", out var modern))
                sb.AppendLine($"- **Modern Alternative:** {modern}");
            if (finding.ModernizationContext.TryGetValue("introducedIn", out var version))
                sb.AppendLine($"- **Available Since:** {version}");
            if (finding.ModernizationContext.TryGetValue("effort", out var effort))
                sb.AppendLine($"- **Migration Effort:** {effort}");
            if (finding.ModernizationContext.TryGetValue("benefits", out var benefits) && benefits is List<string> benefitList)
                sb.AppendLine($"- **Benefits:** {string.Join(", ", benefitList)}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(finding.Recommendation))
        {
            sb.AppendLine($"**Recommendation:** {finding.Recommendation}");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(finding.CodeSnippet))
        {
            sb.AppendLine("**Current Code:**");
            sb.AppendLine("```csharp");
            sb.AppendLine(finding.CodeSnippet);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (finding.FixExample != null && (!string.IsNullOrEmpty(finding.FixExample.Before) || !string.IsNullOrEmpty(finding.FixExample.After)))
        {
            if (!string.IsNullOrEmpty(finding.FixExample.Before))
            {
                sb.AppendLine("**Before (Legacy Pattern):**");
                sb.AppendLine("```csharp");
                sb.AppendLine(finding.FixExample.Before);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(finding.FixExample.After))
            {
                sb.AppendLine("**After (Modern Alternative):**");
                sb.AppendLine("```csharp");
                sb.AppendLine(finding.FixExample.After);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine();
    }

    internal string? GetCachedReport(string repo_path)
    {
        return _reportCache.TryGetValue(repo_path, out var report) ? report : null;
    }

    private sealed class ReportMeta
    {
        public string CommitHash { get; set; } = string.Empty;
    }
}
