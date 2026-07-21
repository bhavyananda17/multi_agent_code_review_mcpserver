using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Agents;

internal static class AgentHelpers
{
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
