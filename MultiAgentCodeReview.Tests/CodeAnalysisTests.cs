using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Tests;

/// <summary>The Roslyn tool and the analyzer that decides which methods of a commit to ask it about.</summary>
public class CodeAnalysisTests
{
    private static string Pad(int n, string prefix) => string.Join("\n", Enumerable.Range(1, n).Select(i => $"        var {prefix}{i} = {i};"));

    private static string Orders(bool changed) => $$"""
using System;
using System.Collections.Generic;
namespace Shop;
public class Orders
{
    public int Process(int x)
    {
        var total = 0;
        foreach (var i in new[] { 1, 2, 3 })
        {
            if (i > x && x != 0)
            {
                total += Helper(i);
            }
            else if (i == x || x < 0)
            {
                total -= 1;
            }
        }
        switch (x) { case 1: total++; break; case 2: total--; break; }
        var r = x > 5 ? 1 : 0;
        while (total < 0) total++;
        try { total += {{(changed ? 2 : 1)}}; } catch (Exception) { total = 0; }
        return total + r;
    }

    public int Helper(int x)
    {
        var y = x + 1;
{{(changed ? "" : "        var z = y;\n")}}        return y;
    }

    public int Untouched(int x) { return Helper(x); }

    public async void Fire() { await System.Threading.Tasks.Task.Delay({{(changed ? 2 : 1)}}); }

    public int Over(int x) { return x; }
    public int Over(string s) { if (s == null) return {{(changed ? 7 : 0)}}; return 1; }

    public int LongOne(int x)
    {
{{Pad(60, "a")}}
        return x + {{(changed ? 2 : 1)}};
    }

    public int OtherLong(int x)
    {
{{Pad(60, "b")}}
        return x;
    }
}
""";

    private static TempRepo Fixture()
    {
        var repo = new TempRepo();
        repo.Write("src/Orders.cs", Orders(false));
        repo.Write("src/Other.cs", "namespace Shop;\npublic class Other\n{\n    public int Go(Orders o) { return o.Process(1) + o.Helper(2); }\n}\n");
        repo.Write("src/Gone.cs", "namespace Shop;\npublic class Gone { public int G() { return 1; } }\n");
        repo.Write("Shop.csproj", "<Project><PropertyGroup><X>1</X></PropertyGroup></Project>\n");
        repo.Write("README.md", "a");
        repo.Commit("base");
        repo.Write("src/Orders.cs", Orders(true));
        repo.Write("src/Other.cs", "namespace Shop;\npublic class Other\n{\n    public int Go(Orders o) { return o.Process(2) + o.Helper(2); }\n}\n");
        repo.Delete("src/Gone.cs");
        repo.Write("Shop.csproj", "<Project><PropertyGroup><X>2</X></PropertyGroup></Project>\n");
        repo.Write("README.md", "b");
        repo.Commit("change");
        return repo;
    }

    [Fact]
    public async Task The_tool_no_longer_throws_on_call_site_and_smell_analysis()
    {
        // Both used to throw ArgumentException ("SyntaxTree is not part of the compilation") on every file.
        using var repo = Fixture();
        var tool = new CodeAnalysisTool();
        var file = Path.Combine(repo.Path, "src/Orders.cs");

        var callers = await tool.FindCallersAsync(file, "Helper");
        Assert.Equal(new[] { "Process", "Untouched" }, callers.Select(c => c.CallingMethod).OrderBy(x => x));

        var smells = await tool.DetectCodeSmellsAsync(file);
        Assert.Equal(1, smells.Count(s => s.Type == "AsyncVoid"));
        Assert.Equal(2, smells.Count(s => s.Type == "LongMethod"));
    }

    [Fact]
    public void Changed_line_numbers_follow_the_new_file_and_deletions_map_to_where_they_sit()
    {
        var hunk = new Hunk(10, 5, 10, 6, " ctx1\n+added1\n+added2\n ctx2\n-removed\n ctx3\n\\ No newline at end of file\n");
        Assert.Equal(new[] { 11, 12, 14 }, ChangeAnalyzer.ChangedNewLines(new[] { hunk }).OrderBy(x => x));
        Assert.Equal(new[] { 1, 2, 3 }, ChangeAnalyzer.ChangedNewLines(new[] { new Hunk(0, 0, 1, 3, "+a\n+b\n+c\n") }).OrderBy(x => x));
        Assert.Empty(ChangeAnalyzer.ChangedNewLines(new List<Hunk>()));
    }

    [Fact]
    public async Task Only_methods_the_commit_touched_are_measured_with_hand_checked_numbers()
    {
        using var repo = Fixture();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var report = (await ChangeAnalyzer.AnalyzeAsync(new CodeAnalysisTool(), repo.Path, diff))!;

        var complexity = report.Complexity.ToDictionary(c => c.MethodName, c => c.Complexity);
        // Process = 1 + foreach + if + && + else-if + || + switch(2 sections) + ?: + while + catch = 11
        Assert.Equal(11, complexity["Process"]);
        Assert.Equal(new[] { "Fire", "Go", "Helper", "LongOne", "Process" }, complexity.Keys.OrderBy(x => x));   // Helper: a pure deletion
        Assert.DoesNotContain("Untouched", complexity.Keys);
        Assert.DoesNotContain("OtherLong", complexity.Keys);
        Assert.DoesNotContain("Over", complexity.Keys);   // overloaded: the tool would report the wrong overload
        Assert.All(report.Complexity, c => Assert.False(Path.IsPathRooted(c.FilePath)));
        Assert.Contains(report.Notes!, n => n.Contains("overloaded method 'Over'"));
    }

    [Fact]
    public async Task Callers_are_found_within_the_same_file_only_and_deduplicated_per_line()
    {
        using var repo = Fixture();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var report = (await ChangeAnalyzer.AnalyzeAsync(new CodeAnalysisTool(), repo.Path, diff))!;

        var helper = Assert.Single(report.CallSites);
        Assert.Equal("Helper", helper.MethodName);
        Assert.Equal(new[] { "Process", "Untouched" }, helper.Callers.Select(c => c.CallingMethod).OrderBy(x => x));
        Assert.DoesNotContain(report.CallSites, c => c.Callers.Any(x => x.CallingMethod == "Go")); // cross-file: a documented limit
    }

    [Fact]
    public async Task Smells_are_limited_to_the_touched_code()
    {
        using var repo = Fixture();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var report = (await ChangeAnalyzer.AnalyzeAsync(new CodeAnalysisTool(), repo.Path, diff))!;

        Assert.Equal(2, report.CodeSmells.Count);
        Assert.Contains(report.CodeSmells, s => s.Type == "AsyncVoid" && s.Description.Contains("Fire"));
        Assert.Contains(report.CodeSmells, s => s.Type == "LongMethod" && s.Description.Contains("LongOne"));
        Assert.DoesNotContain(report.CodeSmells, s => s.Description.Contains("OtherLong")); // pre-existing debt
    }

    private sealed class FailingTool(ICodeAnalysisTool inner, string failMethod) : ICodeAnalysisTool
    {
        public Task<int> GetCyclomaticComplexityAsync(string f, string m, string b = "") => inner.GetCyclomaticComplexityAsync(f, m, b);
        public Task<DependencyGraph> GetDependencyGraphAsync(string f, string b = "") => inner.GetDependencyGraphAsync(f, b);
        public Task<List<CallSite>> FindCallersAsync(string f, string m, string b = "") => m == failMethod ? throw new InvalidOperationException("boom") : inner.FindCallersAsync(f, m, b);
        public Task<List<CodeSmell>> DetectCodeSmellsAsync(string f, string b = "") => inner.DetectCodeSmellsAsync(f, b);
    }

    [Fact]
    public async Task A_tool_failure_on_one_method_is_noted_and_the_rest_is_still_analysed()
    {
        using var repo = Fixture();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var report = (await ChangeAnalyzer.AnalyzeAsync(new FailingTool(new CodeAnalysisTool(), "Fire"), repo.Path, diff))!;

        Assert.Contains(report.Notes!, n => n.Contains("'Fire'") && n.Contains("failed"));
        Assert.Contains(report.Complexity, c => c.MethodName == "Process");
        Assert.Equal(2, report.CodeSmells.Count);
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var repo = Fixture();
        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChangeAnalyzer.AnalyzeAsync(new CodeAnalysisTool(), repo.Path, diff, cts.Token));
    }

    [Fact]
    public async Task The_number_of_analysed_methods_is_capped_and_garbage_files_are_harmless()
    {
        using var repo = new TempRepo();
        string Many(int v) => "namespace C;\npublic class Many\n{\n" + string.Join("\n", Enumerable.Range(1, 40).Select(i => $"    public int M{i}() {{ return {v}; }}")) + "\n}\n";
        repo.Write("Many.cs", Many(1)); repo.Commit("a");
        repo.Write("Many.cs", Many(2)); repo.Write("Junk.cs", "this is { not C# ((( at all\n"); repo.Commit("b");

        var diff = await new GitOperationsTool(repo.Path).GetDiffAsync("HEAD~1", "HEAD");
        var report = (await ChangeAnalyzer.AnalyzeAsync(new CodeAnalysisTool(), repo.Path, diff))!;

        Assert.Equal(ChangeAnalyzer.MaxMethods, report.Complexity.Count);
        Assert.Contains(report.Notes!, n => n.Contains($"first {ChangeAnalyzer.MaxMethods}"));
        Assert.DoesNotContain(report.Complexity, c => c.FilePath == "Junk.cs");
    }
}
