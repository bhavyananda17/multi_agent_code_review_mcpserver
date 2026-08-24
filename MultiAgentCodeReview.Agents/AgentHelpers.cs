using System.Text.RegularExpressions;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Agents;

internal static class AgentHelpers
{
    // Matches a <think>...</think> reasoning trace: case-insensitive, spans multiple
    // lines, and tolerates a MISSING closing tag (output truncated mid-think) via the
    // `$` alternative — everything from <think> to end of string is dropped.
    private static readonly Regex ThinkingTraceRegex = new(
        @"<think\b[^>]*>.*?(?:</think>|$)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex OrphanCloseTagRegex = new(
        @"</think\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes LLM reasoning traces (&lt;think&gt; blocks) from response text before it is
    /// used as user-visible output. Defensive by design: reasoning_effort:"none" is sent to
    /// Groq for qwen models (see ReasoningEffortPolicy), but relying on a provider to honor
    /// a request parameter is fragile — same rationale as TriageAgent.CleanJsonResponse.
    /// </summary>
    internal static string StripThinkingTrace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var stripped = ThinkingTraceRegex.Replace(text, string.Empty);
        stripped = OrphanCloseTagRegex.Replace(stripped, string.Empty);
        return stripped.Trim();
    }

    internal static List<string> ReadChangedFileContents(PipelineContext context)
    {
        var results = new List<string>();
        foreach (var file in context.ChangedFiles)
        {
            var fullPath = Path.Combine(context.RepositoryPath, file.Path);
            try
            {
                if (!File.Exists(fullPath))
                {
                    results.Add($"--- {file.Path} [could not read {file.Path} — file not found] ---");
                    continue;
                }
                var content = File.ReadAllText(fullPath);
                if (content.Length > 2000)
                    content = content.Substring(0, 2000) + "\n... [truncated at 2000 chars]";
                results.Add($"--- {file.Path} ---\n{content}");
            }
            catch
            {
                results.Add($"--- {file.Path} [could not read {file.Path}] ---");
            }
        }
        return results;
    }

    internal static void AppendDependencyGraph(System.Text.StringBuilder sb, PipelineContext context)
    {
        if (context.DependencyGraph?.FileDependencies != null && context.DependencyGraph.FileDependencies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Dependency Graph (file -> files it depends on):");
            foreach (var kvp in context.DependencyGraph.FileDependencies)
            {
                sb.AppendLine($"  {kvp.Key} -> {string.Join(", ", kvp.Value)}");
            }
        }
    }
}
