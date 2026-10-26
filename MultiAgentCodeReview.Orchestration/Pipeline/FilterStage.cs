using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

public class FilterStage
{
    private readonly IGitOperationsTool _gitOperations;
    private readonly ICodeAnalysisTool _codeAnalysis;

    public FilterStage(IGitOperationsTool gitOperations, ICodeAnalysisTool codeAnalysis)
    {
        _gitOperations = gitOperations;
        _codeAnalysis = codeAnalysis;
    }

    public async Task<PipelineContext> ExecuteAsync(
        string repositoryPath,
        string commitHash,
        string? baseCommit,
        int maxFiles = 30,
        int minFiles = 5,
        CancellationToken cancellationToken = default,
        IssueContext? issue = null,
        bool runStaticAnalysis = true)
    {
        var fromRef = baseCommit ?? $"{commitHash}~1";
        var git = new Tools.GitOperationsTool(repositoryPath);
        var diff = await git.GetDiffAsync(fromRef, commitHash);
        var changedFiles = await git.GetChangedFilesAsync(fromRef, commitHash);

        var sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".csproj", ".fsproj", ".vbproj", ".sln", ".fs", ".fsx", ".props", ".targets"
        };

        changedFiles = changedFiles
            .Where(f => sourceExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var filteredDiff = new GitDiff(
            diff.Summary,
            diff.Files.Where(f => sourceExtensions.Contains(Path.GetExtension(f.Path))).ToList()
        );

        // Optional issue-aware ordering. Both lists are reordered (stable) so that issue-relevant files
        // (a) win the maxFiles cap and are expanded first in the dependency walk, and (b) have their
        // diffs placed first, ahead of the specialists' diff-size truncation. Without an issue this
        // block is skipped and the order is exactly what git returned.
        if (issue != null)
        {
            changedFiles = IssueRelevanceScorer.RankPaths(changedFiles, filteredDiff, issue);
            filteredDiff = filteredDiff with { Files = IssueRelevanceScorer.RankDiffs(filteredDiff, issue) };
        }

        var dependencyGraph = await BuildDependencyGraphAsync(changedFiles, repositoryPath, cancellationToken);
        var filteredFiles = FilterRelevantFiles(changedFiles, dependencyGraph, filteredDiff, maxFiles, minFiles);

        // Roslyn complexity / call sites / smells for the methods this commit touched; carried on the context
        // so each specialist can be given the slice that is relevant to it.
        var analysis = runStaticAnalysis
            ? await ChangeAnalyzer.AnalyzeAsync(_codeAnalysis, repositoryPath, filteredDiff, cancellationToken)
            : null;

        return new PipelineContext(
            RepositoryPath: repositoryPath,
            CommitHash: commitHash,
            BaseCommit: baseCommit,
            ChangedFiles: filteredFiles,
            Diff: filteredDiff,
            DependencyGraph: dependencyGraph,
            Issue: issue,
            CodeAnalysis: analysis
        );
    }

    private async Task<DependencyGraph> BuildDependencyGraphAsync(
        List<string> changedFiles,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var fileDependencies = new Dictionary<string, List<string>>();
        var reverseDependencies = new Dictionary<string, List<string>>();
        var entryPoints = new List<string>();

        foreach (var file in changedFiles)
        {
            var fullPath = Path.Combine(repositoryPath, file);
            if (!File.Exists(fullPath))
                continue;

            try
            {
                var graph = await _codeAnalysis.GetDependencyGraphAsync(fullPath);
                foreach (var kvp in graph.FileDependencies)
                    fileDependencies[kvp.Key] = kvp.Value;
                foreach (var kvp in graph.ReverseDependencies)
                {
                    if (!reverseDependencies.ContainsKey(kvp.Key))
                        reverseDependencies[kvp.Key] = new();
                    reverseDependencies[kvp.Key].AddRange(kvp.Value);
                }
                entryPoints.AddRange(graph.EntryPoints);
            }
            catch
            {
                // Skip files that can't be analyzed
            }
        }

        return new DependencyGraph(fileDependencies, reverseDependencies, entryPoints.Distinct().ToList());
    }

    private List<ChangedFile> FilterRelevantFiles(
        List<string> changedFiles,
        DependencyGraph dependencyGraph,
        GitDiff diff,
        int maxFiles,
        int minFiles)
    {
        var relevant = new HashSet<string>(changedFiles);
        var queue = new Queue<string>(changedFiles);
        var visited = new HashSet<string>(changedFiles);

        while (queue.Count > 0 && relevant.Count < maxFiles)
        {
            var current = queue.Dequeue();

            if (dependencyGraph.ReverseDependencies.TryGetValue(current, out var dependents))
            {
                foreach (var dependent in dependents)
                {
                    if (visited.Add(dependent) && relevant.Count < maxFiles)
                    {
                        relevant.Add(dependent);
                        queue.Enqueue(dependent);
                    }
                }
            }
        }

        return relevant
            .Select(f =>
            {
                var fileDiff = diff.Files.FirstOrDefault(fd =>
                    fd.Path == f || fd.Path.EndsWith(f) || f.EndsWith(fd.Path));
                var additions = fileDiff?.Hunks.Sum(h => Math.Max(0, h.NewLines)) ?? 0;
                var deletions = fileDiff?.Hunks.Sum(h => Math.Max(0, h.OldLines)) ?? 0;
                var changeType = fileDiff?.ChangeType ?? ChangeType.Modified;
                return new ChangedFile(f, additions, deletions, changeType);
            })
            .Take(maxFiles)
            .ToList();
    }
}
