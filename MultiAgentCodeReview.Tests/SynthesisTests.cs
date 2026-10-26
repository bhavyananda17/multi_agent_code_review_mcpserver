using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Tests;

public class SynthesisTests
{
    private static readonly CodeReviewPipeline Pipeline =
        new ServiceCollection().AddMultiAgentCodeReview().BuildServiceProvider().GetRequiredService<CodeReviewPipeline>();

    private static Finding F(Severity severity, string file, int line, string description, double confidence = 0.9) =>
        new(severity, FindingCategory.CodeSmell, file, line, description, "fix", "", new FixExample("", ""), confidence);

    private static AgentResult Synthesize(params (string Agent, Finding[] Findings)[] results)
    {
        var method = typeof(CodeReviewPipeline).GetMethod("SynthesizeFindings", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var specialists = results.Select(r => (r.Agent, new AgentResult(r.Findings.ToList(), ""))).ToList();
        var context = new PipelineContext("repo", "HEAD", null, new List<ChangedFile> { new("a.cs", 1, 0, ChangeType.Modified) }, null, null);
        return (AgentResult)method.Invoke(Pipeline, new object[] { specialists, context })!;
    }

    [Fact]
    public void Two_findings_on_one_line_from_one_agent_keep_the_MOST_severe()
    {
        // Severity is declared Critical, High, Medium, Low; the old code compared the wrong way round and kept the Low one.
        var result = Synthesize(("SecurityAgent", new[] { F(Severity.Low, "a.cs", 5, "minor"), F(Severity.Critical, "a.cs", 5, "major") }));
        var kept = Assert.Single(result.Findings);
        Assert.Equal(Severity.Critical, kept.Severity);
    }

    [Fact]
    public void Agreement_between_agents_is_reported_but_does_not_escalate_severity()
    {
        var result = Synthesize(
            ("SecurityAgent", new[] { F(Severity.Medium, "a.cs", 5, "security view") }),
            ("LogicAgent", new[] { F(Severity.High, "a.cs", 5, "logic view") }));

        var merged = Assert.Single(result.Findings);
        Assert.Equal(Severity.High, merged.Severity);                       // the more severe of the two, not Critical
        Assert.Equal(new[] { "LogicAgent", "SecurityAgent" }, merged.Agents!.OrderBy(a => a));
        Assert.Contains("security view", merged.Description);
        Assert.Contains("logic view", merged.Description);
        Assert.Contains("reported by more than one agent", result.Summary);
        Assert.DoesNotContain("boosted to Critical", result.Summary);
    }

    [Fact]
    public void The_result_is_ordered_most_severe_first()
    {
        var result = Synthesize(("LogicAgent", new[]
        {
            F(Severity.Low, "a.cs", 1, "low"), F(Severity.Critical, "a.cs", 2, "crit"), F(Severity.Medium, "a.cs", 3, "med"),
        }));
        Assert.Equal(new[] { Severity.Critical, Severity.Medium, Severity.Low }, result.Findings.Select(f => f.Severity));
    }

    [Fact]
    public void Low_agent_confidence_findings_are_kept_and_the_summary_does_not_claim_they_were_excluded()
    {
        var result = Synthesize(("LogicAgent", new[] { F(Severity.High, "a.cs", 4, "unsure", confidence: 0.4) }));
        Assert.Single(result.Findings);
        Assert.DoesNotContain("excluded", result.Summary);
        Assert.Contains("kept but listed without a line number", result.Summary);
    }

    // Wording taken from real benchmark output, where each pair was reported twice with no line number.
    private const string ValidateA = "Method `Validate` has cyclomatic complexity 30 (threshold 10) and contains many nested conditionals, which can degrade performance";
    private const string ValidateB = "Method Validate has a cyclomatic complexity of 30, far exceeding the recommended maximum of 10, making it hard to understand and maintain";
    private const string Srp = "ConfigParser handles command-line parsing, validation, and job creation in a single class, breaching the Single Responsibility Principle";

    [Fact]
    public void Findings_without_a_line_that_say_the_same_thing_are_merged_keeping_the_most_severe()
    {
        var merged = CodeReviewPipeline.MergeNearDuplicates(new List<Finding>
        {
            F(Severity.Medium, "c.cs", 0, ValidateA) with { Agents = new List<string> { "LogicAgent" } },
            F(Severity.High, "c.cs", 0, ValidateB) with { Agents = new List<string> { "PerformanceAgent" } },
        }, out var count);

        var kept = Assert.Single(merged);
        Assert.Equal(1, count);
        Assert.Equal(Severity.High, kept.Severity);
        Assert.Equal(new[] { "LogicAgent", "PerformanceAgent" }, kept.Agents!.OrderBy(a => a));
    }

    [Fact]
    public void Different_problems_in_the_same_file_are_not_merged() =>
        Assert.Equal(2, CodeReviewPipeline.MergeNearDuplicates(new List<Finding>
        {
            F(Severity.Medium, "c.cs", 0, Srp), F(Severity.High, "c.cs", 0, ValidateB),
        }, out _).Count);

    [Fact]
    public void The_same_wording_in_different_files_is_not_merged() =>
        Assert.Equal(2, CodeReviewPipeline.MergeNearDuplicates(new List<Finding>
        {
            F(Severity.Medium, "one.cs", 0, ValidateB), F(Severity.Medium, "two.cs", 0, ValidateB),
        }, out _).Count);

    [Fact]
    public void Merging_happens_inside_the_pipeline_and_is_reported()
    {
        var result = Synthesize(
            ("LogicAgent", new[] { F(Severity.Medium, "c.cs", 0, ValidateA) }),
            ("PerformanceAgent", new[] { F(Severity.High, "c.cs", 0, ValidateB), F(Severity.Medium, "c.cs", 0, Srp) }));

        Assert.Equal(2, result.Findings.Count);
        Assert.Contains("1 near-duplicate finding(s) without a line number were merged", result.Summary);
    }
}
