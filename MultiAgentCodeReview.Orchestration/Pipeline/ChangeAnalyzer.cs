using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

/// <summary>
/// Runs the Roslyn <see cref="ICodeAnalysisTool"/> (complexity, call sites, code smells) over what a commit
/// actually changed. The tool works per file / per method name, so this works out which methods the diff
/// touches (from the hunks' new-side line numbers) and asks the tool about just those, rather than dumping
/// whole-file analysis on the specialists. Best effort: a file the tool chokes on is noted and skipped.
/// </summary>
public static class ChangeAnalyzer
{
    /// <summary>Bounds Roslyn cost: files analysed per review (diff order, so issue-relevant files come first).</summary>
    public const int MaxFiles = 20;

    /// <summary>Bounds Roslyn cost: touched methods analysed per review (complexity + callers each re-parse the file).</summary>
    public const int MaxMethods = 25;

    private static readonly Regex QuotedName = new("'([^']+)'", RegexOptions.Compiled);

    public static async Task<CodeAnalysisReport?> AnalyzeAsync(
        ICodeAnalysisTool tool,
        string repositoryPath,
        GitDiff diff,
        CancellationToken cancellationToken = default)
    {
        var complexity = new List<MethodComplexity>();
        var callSites = new List<MethodCallSites>();
        var smells = new List<CodeSmell>();
        var notes = new List<string>();
        var methodsAnalyzed = 0;
        var capNoted = false;

        var files = diff.Files
            .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Take(MaxFiles);

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fullPath = Path.Combine(repositoryPath, file.Path);
            if (!File.Exists(fullPath))
                continue; // deleted in this commit

            var changed = ChangedNewLines(file.Hunks);
            if (changed.Count == 0)
                continue;

            SyntaxNode root;
            try
            {
                root = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(fullPath, cancellationToken)).GetRoot(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"{file.Path}: could not be parsed ({ex.Message})");
                continue;
            }

            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(m => (Node: m, Name: m.Identifier.Text, Start: StartLine(m), End: EndLine(m)))
                .ToList();
            var touched = methods.Where(m => Touches(changed, m.Start, m.End)).ToList();
            var touchedClassStarts = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => Touches(changed, StartLine(c), EndLine(c)))
                .Select(StartLine)
                .ToHashSet();

            // Complexity and callers are looked up by NAME and the tool resolves a name to the first method
            // that has it, so an overloaded name would silently report a different overload's numbers.
            foreach (var name in touched.Select(m => m.Name).Distinct().Where(n => methods.Count(m => m.Name == n) > 1))
                notes.Add($"{file.Path}: overloaded method '{name}' not analysed for complexity/callers");

            foreach (var method in touched.Where(m => methods.Count(o => o.Name == m.Name) == 1)
                         .OrderBy(m => m.Start))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (methodsAnalyzed >= MaxMethods)
                {
                    if (!capNoted)
                        notes.Add($"analysis limited to the first {MaxMethods} touched methods");
                    capNoted = true;
                    break;
                }
                methodsAnalyzed++;

                try
                {
                    var value = await tool.GetCyclomaticComplexityAsync(fullPath, method.Name);
                    if (value > 0)
                        complexity.Add(new MethodComplexity(file.Path, method.Name, method.Start, value));

                    var callers = (await tool.FindCallersAsync(fullPath, method.Name))
                        .GroupBy(c => (c.CallingMethod, c.Line)) // one call site per (method, line), not per call expression
                        .Select(g => g.First())
                        .OrderBy(c => c.Line)
                        .Select(c => c with { FilePath = file.Path })
                        .ToList();
                    if (callers.Count > 0)
                        callSites.Add(new MethodCallSites(file.Path, method.Name, method.Start, callers));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    notes.Add($"{file.Path}: analysis of method '{method.Name}' failed ({ex.Message})");
                }
            }

            try
            {
                var touchedNames = touched.Select(m => m.Name).ToHashSet();
                var touchedStarts = touched.Select(m => m.Start).ToHashSet();
                foreach (var smell in await tool.DetectCodeSmellsAsync(fullPath))
                {
                    // Keep only debt the change is sitting in, not everything wrong with the file.
                    var relevant = smell.Type switch
                    {
                        "LargeClass" => touchedClassStarts.Contains(smell.Line),
                        "DuplicatedCode" => touchedStarts.Contains(smell.Line)
                            || QuotedName.Matches(smell.Description).Any(m => touchedNames.Contains(m.Groups[1].Value)),
                        _ => touchedStarts.Contains(smell.Line) // LongMethod, AsyncVoid: reported at the method's first line
                    };
                    if (relevant)
                        smells.Add(smell with { FilePath = file.Path });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notes.Add($"{file.Path}: code-smell analysis failed ({ex.Message})");
            }
        }

        if (complexity.Count == 0 && callSites.Count == 0 && smells.Count == 0 && notes.Count == 0)
            return null;

        return new CodeAnalysisReport(complexity, callSites, smells, notes.Distinct().ToList());
    }

    /// <summary>
    /// New-file line numbers touched by the hunks: every added line, plus the line a pure deletion sits at
    /// (so removing code from inside a method still counts as touching that method).
    /// </summary>
    public static HashSet<int> ChangedNewLines(IEnumerable<Hunk> hunks)
    {
        var lines = new HashSet<int>();
        foreach (var hunk in hunks)
        {
            var newLine = hunk.NewStart;
            foreach (var raw in hunk.Content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                switch (line[0])
                {
                    case '+': lines.Add(newLine); newLine++; break;
                    case ' ': newLine++; break;
                    case '-': lines.Add(newLine); break;
                    // '\' ("No newline at end of file") consumes no line
                }
            }
        }
        return lines;
    }

    private static bool Touches(HashSet<int> changed, int start, int end) => changed.Any(l => l >= start && l <= end);

    // Same line convention the tool uses for its smells/call sites (node location, 1-based).
    private static int StartLine(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    private static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;
}
