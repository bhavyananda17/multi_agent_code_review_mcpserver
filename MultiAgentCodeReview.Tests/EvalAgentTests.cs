using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Tests;

public class EvalAgentTests
{
    private const string Good = """{"evaluations":[{"id":0,"relevance":3,"severityAccuracy":2,"actionability":3,"rationale":"ok"},{"id":1,"relevance":1,"severityAccuracy":1,"actionability":2}]}""";

    [Fact]
    public void Clean_json_is_parsed_with_totals_and_rationale()
    {
        var parsed = EvalAgent.ParseEvaluations(Good, 2);
        Assert.Equal((3, 2, 3, 8, "ok"), (parsed[0]!.Relevance, parsed[0]!.SeverityAccuracy, parsed[0]!.Actionability, parsed[0]!.Total, parsed[0]!.Rationale));
        Assert.Equal(4, parsed[1]!.Total);
    }

    [Theory]
    [InlineData("```json\n" + Good + "\n```")]
    [InlineData("<think>hmm {\"x\":1}</think>\nSure! Here you go:\n" + Good + "\nHope that helps")]
    public void Fences_reasoning_traces_and_surrounding_prose_are_tolerated(string response) =>
        Assert.All(EvalAgent.ParseEvaluations(response, 2), Assert.NotNull);

    [Fact]
    public void A_bare_array_is_accepted_and_only_the_scored_id_is_filled()
    {
        var parsed = EvalAgent.ParseEvaluations("""[{"id":1,"relevance":2,"severityAccuracy":2,"actionability":2}]""", 2);
        Assert.Null(parsed[0]);
        Assert.Equal(6, parsed[1]!.Total);
    }

    [Fact]
    public void String_numbers_floats_snake_case_and_odd_casing_are_understood()
    {
        var parsed = EvalAgent.ParseEvaluations("""{"evaluations":[{"id":0,"relevance":"3","severity_accuracy":2.0,"Actionability":3}]}""", 1);
        Assert.Equal((3, 2, 3), (parsed[0]!.Relevance, parsed[0]!.SeverityAccuracy, parsed[0]!.Actionability));
    }

    [Fact]
    public void Out_of_range_scores_are_clamped_to_the_1_to_3_scale()
    {
        var parsed = EvalAgent.ParseEvaluations("""{"evaluations":[{"id":0,"relevance":9,"severityAccuracy":0,"actionability":-4}]}""", 1);
        Assert.Equal((3, 1, 1), (parsed[0]!.Relevance, parsed[0]!.SeverityAccuracy, parsed[0]!.Actionability));
    }

    [Fact]
    public void A_missing_dimension_leaves_the_finding_unscored_instead_of_guessing() =>
        Assert.Null(EvalAgent.ParseEvaluations("""{"evaluations":[{"id":0,"relevance":3,"actionability":3}]}""", 1)[0]);

    [Fact]
    public void Ids_outside_the_batch_are_ignored() =>
        Assert.All(EvalAgent.ParseEvaluations("""{"evaluations":[{"id":7,"relevance":3,"severityAccuracy":3,"actionability":3},{"id":-1,"relevance":3,"severityAccuracy":3,"actionability":3}]}""", 2), Assert.Null);

    [Fact]
    public void Entries_without_ids_are_taken_in_order()
    {
        var parsed = EvalAgent.ParseEvaluations("""{"evaluations":[{"relevance":3,"severityAccuracy":3,"actionability":3},{"relevance":1,"severityAccuracy":1,"actionability":1}]}""", 2);
        Assert.Equal((9, 3), (parsed[0]!.Total, parsed[1]!.Total));
    }

    [Theory]
    [InlineData("I cannot do that")]
    [InlineData("")]
    [InlineData("{not json")]
    public void Unusable_output_leaves_everything_unscored(string response) =>
        Assert.All(EvalAgent.ParseEvaluations(response, 3), Assert.Null);

    [Fact]
    public void The_result_always_has_one_slot_per_finding() => Assert.Equal(5, EvalAgent.ParseEvaluations(Good, 5).Count);

    private static Finding F(Severity s, string file, int line, string description, List<string>? agents = null) =>
        new(s, FindingCategory.CodeSmell, file, line, description, "Do the thing.", "var x = 1;", new FixExample("", ""), 0.9, "Fix it.", Agents: agents);

    [Fact]
    public void The_prompt_tells_the_judge_what_changed_and_where_each_finding_points()
    {
        var context = new PipelineContext("repo", "abc123", null,
            new List<ChangedFile> { new("src/A.cs", 3, 0, ChangeType.Modified) },
            new GitDiff("1 file", new List<FileDiff> { new("src/A.cs", "src/A.cs", ChangeType.Modified, new List<Hunk>
                { new(1, 1, 1, 3, "+first hunk line\n"), new(10, 1, 10, 3, "+SECOND hunk line\n") }) }),
            null);

        var prompt = EvalAgent.BuildPrompt(context, new[]
        {
            F(Severity.High, "src/A.cs", 12, "In changed file", new List<string> { "SecurityAgent", "LogicAgent" }),
            F(Severity.Low, "src/Elsewhere.cs", 5, "Not in change"),
            F(Severity.Low, "", 0, "No location " + new string('z', 1000)),
        });

        Assert.Contains("id=0", prompt); Assert.Contains("id=2", prompt);
        Assert.Contains("src/A.cs:12 (file is part of this change)", prompt);
        Assert.Contains("src/Elsewhere.cs:5 (file is NOT part of this change)", prompt);
        Assert.Contains("no location given", prompt);
        Assert.Contains("SECOND hunk line", prompt);          // the hunk that contains line 12
        Assert.DoesNotContain("first hunk line", prompt);
        Assert.Contains("Reported by: SecurityAgent, LogicAgent", prompt);
        Assert.DoesNotContain(new string('z', 400), prompt);   // long text is clipped
        Assert.Contains("(not available)", prompt);
    }
}
