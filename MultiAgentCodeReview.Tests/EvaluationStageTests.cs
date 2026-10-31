using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Tests;

public class EvaluationStageTests
{
    private sealed class StubEval(Func<IReadOnlyList<Finding>, List<FindingEvaluation?>> score) : IEvalAgent
    {
        public List<int> BatchSizes { get; } = new();
        public string Name => "stub";
        public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default) => Task.FromResult(new AgentResult(new(), ""));
        public Task<List<FindingEvaluation?>> EvaluateAsync(PipelineContext context, IReadOnlyList<Finding> findings, CancellationToken cancellationToken = default)
        {
            BatchSizes.Add(findings.Count);
            return Task.FromResult(score(findings));
        }
    }

    private sealed class StubFactory : AgentFactory
    {
        public Func<IEvalAgent> Make = () => throw new InvalidOperationException("no API key");
        public StubFactory(PipelineConfig config) : base(Options.Create(config)) { }
        public override IEvalAgent CreateEvalAgent() => Make();
    }

    private static Finding F(string description, int line = 1) =>
        new(Severity.High, FindingCategory.CodeSmell, "src/A.cs", line, description, "fix", "", new FixExample("", ""), 0.9);

    private static List<FindingEvaluation?> ByDescription(IReadOnlyList<Finding> findings) =>
        findings.Select(f => f.Description switch
        {
            "GOOD" => new FindingEvaluation(3, 3, 2),     // 8
            "BORDER" => new FindingEvaluation(2, 2, 1),   // 5
            "WEAK" => new FindingEvaluation(1, 1, 2),     // 4
            _ => (FindingEvaluation?)null
        }).ToList();

    private static PipelineContext Context() =>
        new("repo", "HEAD", null, new List<ChangedFile> { new("src/A.cs", 1, 0, ChangeType.Modified) }, null, null);

    private static EvaluationStage Stage(IEvalAgent? agent, Action<PipelineConfig>? configure = null)
    {
        var config = new PipelineConfig();
        configure?.Invoke(config);
        var factory = new StubFactory(config);
        if (agent != null) factory.Make = () => agent;
        return new EvaluationStage(factory, Options.Create(config));
    }

    private static AgentResult Three() => new(new List<Finding> { F("GOOD"), F("BORDER"), F("WEAK") }, "Reviewed 1 files.");

    [Fact]
    public async Task A_total_equal_to_the_threshold_passes_and_only_a_lower_total_is_flagged()
    {
        var (result, summary) = await Stage(new StubEval(ByDescription)).ExecuteAsync(Three(), Context());

        Assert.Equal(new[] { 8, 5, 4 }, result.Findings.Select(f => f.Evaluation!.Total));
        Assert.Equal(new[] { false, false, true }, result.Findings.Select(f => f.Evaluation!.LowConfidence));
        Assert.Equal(3, result.Findings.Count);   // default: flagged findings are kept for a separate section
        Assert.Equal((5, 3, 0, 1, false), (summary!.Threshold, summary.Scored, summary.Unscored, summary.LowConfidence, summary.LowConfidenceExcluded));
        Assert.Contains("1 flagged low-confidence and shown in a separate section", result.Summary);
    }

    [Fact]
    public async Task Exclude_mode_drops_the_flagged_findings_and_says_so()
    {
        var (result, summary) = await Stage(new StubEval(ByDescription), c => c.Evaluation.ExcludeLowConfidence = true).ExecuteAsync(Three(), Context());
        Assert.Equal(2, result.Findings.Count);
        Assert.True(summary!.LowConfidenceExcluded);
        Assert.Contains("excluded from this report", result.Summary);
    }

    [Theory]
    [InlineData(3, 0)]    // 3 is the lowest possible total: nothing can be below it
    [InlineData(9, 3)]    // nothing but a perfect score reaches 9
    [InlineData(99, 3)]   // clamped to 9
    [InlineData(-5, 0)]   // clamped to 0
    public async Task The_threshold_is_clamped_to_the_possible_range(int threshold, int expectedFlagged)
    {
        var (result, _) = await Stage(new StubEval(ByDescription), c => c.Evaluation.Threshold = threshold).ExecuteAsync(Three(), Context());
        Assert.Equal(expectedFlagged, result.Findings.Count(f => f.Evaluation!.LowConfidence));
    }

    [Fact]
    public async Task When_disabled_or_with_nothing_to_score_the_agent_is_never_created()
    {
        var input = Three();
        var (disabledResult, disabledSummary) = await Stage(null, c => c.Evaluation.Enabled = false).ExecuteAsync(input, Context());
        Assert.Same(input, disabledResult);
        Assert.Null(disabledSummary);

        var (_, emptySummary) = await Stage(null).ExecuteAsync(new AgentResult(new List<Finding>(), ""), Context());
        Assert.Null(emptySummary);
    }

    [Fact]
    public async Task Findings_are_scored_in_batches_and_keep_their_order()
    {
        var agent = new StubEval(ByDescription);
        var many = new AgentResult(Enumerable.Range(1, 20).Select(i => F("GOOD", i)).ToList(), "");
        var (result, summary) = await Stage(agent, c => c.Evaluation.BatchSize = 8).ExecuteAsync(many, Context());

        Assert.Equal(new[] { 8, 8, 4 }, agent.BatchSizes);
        Assert.Equal(20, summary!.Scored);
        Assert.Equal(Enumerable.Range(1, 20), result.Findings.Select(f => f.Line));
    }

    [Fact]
    public async Task A_judge_that_throws_never_hides_findings()
    {
        var agent = new StubEval(_ => throw new InvalidOperationException("model exploded"));
        var (result, summary) = await Stage(agent).ExecuteAsync(Three(), Context());

        Assert.Equal(3, result.Findings.Count);
        Assert.All(result.Findings, f => Assert.Null(f.Evaluation));
        Assert.Equal(new[] { 3 }, agent.BatchSizes);                    // a single attempt over all 3 findings: not a rate-limit error, so no retry
        Assert.Equal((0, 3), (summary!.Scored, summary.Unscored));
        Assert.Contains("no findings could be scored", result.Summary);
    }

    [Fact]
    public async Task A_judge_that_cannot_be_created_also_fails_open()
    {
        var (result, summary) = await Stage(null).ExecuteAsync(Three(), Context());
        Assert.Equal(3, result.Findings.Count);
        Assert.Equal((0, 3), (summary!.Scored, summary.Unscored));
    }

    [Fact]
    public async Task A_short_score_list_is_aligned_and_the_rest_reported_unscored()
    {
        var (result, summary) = await Stage(new StubEval(f => ByDescription(f).Take(1).ToList())).ExecuteAsync(Three(), Context());
        Assert.Equal((1, 2), (summary!.Scored, summary.Unscored));
        Assert.NotNull(result.Findings[0].Evaluation);
        Assert.Null(result.Findings[1].Evaluation);
        Assert.Contains("2 could not be scored", result.Summary);
    }

    [Fact]
    public async Task Cancelling_the_review_is_not_swallowed_by_the_fail_open_handling()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var agent = new StubEval(_ => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stage(agent).ExecuteAsync(Three(), Context(), cts.Token));
    }

    // ---- judge independence: an LLM judge tends to favour output from its own model / family ----

    private static ModelConfig Model(string role, string id) => new(role, "groq", id, 0.2, 1000);

    private static async Task<(AgentResult Result, EvaluationSummary Summary)> WithModels(params (string Role, string Id)[] models)
    {
        var (result, summary) = await Stage(new StubEval(ByDescription), c =>
        {
            foreach (var (role, id) in models) c.Models[role] = Model(role, id);
        }).ExecuteAsync(Three(), Context());
        return (result, summary!);
    }

    [Fact]
    public async Task A_judge_that_is_the_same_model_as_the_generators_is_called_out()
    {
        var (result, summary) = await WithModels(("security", "openai/gpt-oss-120b"), ("performance", "openai/gpt-oss-120b"),
            ("logic", "openai/gpt-oss-120b"), ("synthesis", "openai/gpt-oss-120b"));   // judge falls back to synthesis

        Assert.Equal("openai/gpt-oss-120b", summary.JudgeModel);
        Assert.True(summary.JudgeSameModelAsGenerators);
        Assert.Contains("the same model that wrote these findings", result.Summary);
    }

    [Fact]
    public async Task A_judge_from_the_same_family_but_a_different_size_is_flagged_as_possible_self_preference()
    {
        var (result, summary) = await WithModels(("security", "openai/gpt-oss-120b"), ("performance", "openai/gpt-oss-120b"),
            ("logic", "openai/gpt-oss-120b"), ("eval", "openai/gpt-oss-20b"));

        Assert.False(summary.JudgeSameModelAsGenerators);
        Assert.True(summary.JudgeSameFamilyAsGenerators);
        Assert.Contains("same model family", result.Summary);
    }

    [Fact]
    public async Task A_judge_from_a_different_family_is_reported_as_independent()
    {
        var (result, summary) = await WithModels(("security", "openai/gpt-oss-120b"), ("performance", "openai/gpt-oss-120b"),
            ("logic", "openai/gpt-oss-120b"), ("eval", "qwen/qwen3.8-27b"));

        Assert.False(summary.JudgeSameModelAsGenerators);
        Assert.False(summary.JudgeSameFamilyAsGenerators);
        Assert.Contains("a different model family from the generators", result.Summary);
    }

    [Fact]
    public void The_judge_model_is_the_eval_model_then_the_synthesis_model_then_the_built_in_default()
    {
        var none = new PipelineConfig();
        Assert.Equal(AgentFactory.DefaultModelId("eval"), AgentFactory.EffectiveJudgeModelId(none));

        var synthesisOnly = new PipelineConfig();
        synthesisOnly.Models["synthesis"] = Model("synthesis", "model-s");
        Assert.Equal("model-s", AgentFactory.EffectiveJudgeModelId(synthesisOnly));

        var both = new PipelineConfig();
        both.Models["synthesis"] = Model("synthesis", "model-s");
        both.Models["eval"] = Model("eval", "model-e");
        Assert.Equal("model-e", AgentFactory.EffectiveJudgeModelId(both));
    }

    [Theory]
    [InlineData("openai/gpt-oss-120b", "gpt-oss")]
    [InlineData("gpt-oss-20b", "gpt-oss")]
    [InlineData("llama-3.3-70b-versatile", "llama")]
    [InlineData("qwen/qwen3.8-27b", "qwen")]
    [InlineData("meta-llama/llama-prompt-guard-2-86m", "llama-prompt-guard")]
    public void Model_family_is_the_leading_name_without_vendor_or_size(string id, string family) =>
        Assert.Equal(family, AgentFactory.ModelFamily(id));
}
