using System.Text;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Orchestration.Reporting;

/// <summary>
/// Builds the markdown code review report from a pipeline <see cref="ReviewOutput"/>.
/// Single source of truth for the report format — shared by the MCP server
/// (stdio tool review_repo) and the HTTP API (POST /api/review).
/// Methods were moved verbatim from CodeReviewMcpTools; no logic changes.
/// </summary>
public static class ReportFormatter
{
    public static string FormatReport(ReviewOutput output)
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
}
