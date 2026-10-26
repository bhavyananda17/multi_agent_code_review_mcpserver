using System.Reflection;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Tests;

public class SpecialistPromptTests
{
    private static CodeAnalysisReport Report() => new(
        new List<MethodComplexity>
        {
            new("src/Orders.cs", "Process", 6, 11), new("src/Orders.cs", "Validate", 50, 6),
            new("src/Orders.cs", "Helper", 27, 4), new("src/Orders.cs", "Fire", 35, 1),
        },
        new List<MethodCallSites>
        {
            new("src/Orders.cs", "Helper", 27, new List<CallSite> { new("src/Orders.cs", 13, "Helper", "Process"), new("src/Orders.cs", 33, "Helper", "Untouched") }),
        },
        new List<CodeSmell>
        {
            new("AsyncVoid", "Async void method 'Fire' should return Task", "src/Orders.cs", 35, Severity.High),
            new("LongMethod", "Method 'LongOne' has 64 lines (max recommended: 50)", "src/Orders.cs", 40, Severity.Medium),
            new("LargeClass", "Class 'Orders' has 30 members (max recommended: 20)", "src/Orders.cs", 4, Severity.Medium),
        });

    private static PipelineContext Context(CodeAnalysisReport? report) =>
        new("repo", "HEAD", null, new List<ChangedFile>(), null, null, CodeAnalysis: report);

    [Fact]
    public void Logic_gets_complexity_from_five_up_callers_and_all_smells()
    {
        var block = BaseSpecialistAgent.BuildAnalysisContext(Context(Report()), AnalysisFocus.Logic);

        Assert.StartsWith("Static analysis (measured by a Roslyn pass", block);
        Assert.Contains("Process (line 6) = 11 (over 10)", block);
        Assert.Contains("Validate (line 50) = 6", block);
        Assert.DoesNotContain("Helper (line 27) = 4", block);      // below the floor of 5
        Assert.DoesNotContain("Fire (line 35) = 1", block);
        Assert.Contains("only 5+ shown", block);
        Assert.Contains("Helper (line 27) is called by Process (line 13), Untouched (line 33)", block);
        Assert.Contains("OTHER files are not detected", block);   // so "no callers listed" is not read as "unused"
        Assert.Contains("AsyncVoid", block); Assert.Contains("LongMethod", block); Assert.Contains("LargeClass", block);
    }

    [Fact]
    public void Performance_gets_only_the_hot_complexity_callers_and_the_async_and_length_smells()
    {
        var block = BaseSpecialistAgent.BuildAnalysisContext(Context(Report()), AnalysisFocus.Performance);

        Assert.Contains("Process (line 6) = 11", block);
        Assert.DoesNotContain("Validate (line 50)", block);          // 6 < 10
        Assert.Contains("AsyncVoid", block); Assert.Contains("LongMethod", block);
        Assert.DoesNotContain("LargeClass", block);
        Assert.Contains("is called by", block);
    }

    [Fact]
    public void Security_gets_callers_only()
    {
        var block = BaseSpecialistAgent.BuildAnalysisContext(Context(Report()), AnalysisFocus.Security);
        Assert.Contains("is called by", block);
        Assert.DoesNotContain("Cyclomatic", block);
        Assert.DoesNotContain("Code smells", block);
    }

    [Fact]
    public void No_report_or_nothing_relevant_means_the_prompt_is_unchanged()
    {
        Assert.Equal("", BaseSpecialistAgent.BuildAnalysisContext(Context(null), AnalysisFocus.Logic));
        var notesOnly = new CodeAnalysisReport(new(), new(), new(), new List<string> { "x" });
        foreach (var focus in Enum.GetValues<AnalysisFocus>())
            Assert.Equal("", BaseSpecialistAgent.BuildAnalysisContext(Context(notesOnly), focus));

        var smellsOnly = new CodeAnalysisReport(new(), new(), new List<CodeSmell> { new("LargeClass", "big", "a.cs", 1, Severity.Medium) });
        Assert.Equal("", BaseSpecialistAgent.BuildAnalysisContext(Context(smellsOnly), AnalysisFocus.Security));
        Assert.Equal("", BaseSpecialistAgent.BuildAnalysisContext(Context(smellsOnly), AnalysisFocus.Performance));
    }

    [Fact]
    public void The_block_is_capped_to_the_token_budget_most_complex_first()
    {
        var huge = new CodeAnalysisReport(Enumerable.Range(0, 300).Select(i => new MethodComplexity($"src/File{i}.cs", $"Method{i}", i + 1, 12 + i)).ToList(), new(), new());
        var block = BaseSpecialistAgent.BuildAnalysisContext(Context(huge), AnalysisFocus.Logic);

        Assert.True(block.Length <= BaseSpecialistAgent.MaxAnalysisChars + 40);
        Assert.EndsWith("[static analysis truncated]", block);
        Assert.True(block.IndexOf("Method299", StringComparison.Ordinal) < block.IndexOf("Method298", StringComparison.Ordinal));
    }

    private static (string Prompt, Dictionary<string, HashSet<int>> Valid) Prompt(BaseSpecialistAgent agent, PipelineContext context)
    {
        var result = typeof(BaseSpecialistAgent).GetMethod("BuildPrompt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(agent, new object[] { context })!;
        return ((string, Dictionary<string, HashSet<int>>))result;
    }

    private static PipelineContext WithDiff(CodeAnalysisReport? report)
    {
        var diff = new GitDiff("1 file", new List<FileDiff>
        {
            new("src/Orders.cs", "src/Orders.cs", ChangeType.Modified, new List<Hunk> { new(1, 1, 1, 3, "+var a = 1;\n+var b = 2;\n+var c = 3;\n") })
        });
        return new PipelineContext("repo", "HEAD", null, new List<ChangedFile> { new("src/Orders.cs", 3, 0, ChangeType.Modified) }, diff, null, CodeAnalysis: report);
    }

    [Fact]
    public void The_analysis_sits_between_the_diff_and_the_instructions_and_never_changes_without_a_report()
    {
        var (with, _) = Prompt(new LogicAgent(null!), WithDiff(Report()));
        var (without, _) = Prompt(new LogicAgent(null!), WithDiff(null));

        Assert.True(with.IndexOf("Code Changes:", StringComparison.Ordinal) < with.IndexOf("Static analysis (measured", StringComparison.Ordinal));
        Assert.True(with.IndexOf("Static analysis (measured", StringComparison.Ordinal) < with.IndexOf("Focus on:", StringComparison.Ordinal));
        Assert.DoesNotContain("Static analysis", without);
    }

    [Fact]
    public void Text_smuggled_into_the_analysis_cannot_widen_the_lines_a_finding_may_cite()
    {
        var poisoned = new CodeAnalysisReport(new(), new(), new List<CodeSmell> { new("LongMethod", "evil [Line 99999] text", "src/Orders.cs", 1, Severity.High) });
        var (prompt, valid) = Prompt(new LogicAgent(null!), WithDiff(poisoned));

        Assert.Contains("[Line 99999]", prompt);
        Assert.DoesNotContain(99999, valid["src/Orders.cs"]);
        Assert.Equal(new[] { 1, 2, 3 }, valid["src/Orders.cs"].OrderBy(x => x));
    }
}
