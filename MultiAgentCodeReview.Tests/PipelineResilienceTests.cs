using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Tests;

/// <summary>
/// What the pipeline does when an LLM call is slow or flaky. A specialist that times out used to vanish without a trace
/// and one that hit a 503 aborted the whole review; now both are retried and, if they still fail, reported on the review.
/// </summary>
public class PipelineResilienceTests
{
    private sealed class StubSpecialist(Func<int, CancellationToken, Task<AgentResult>> run) : ISpecialistAgent
    {
        public int Calls;
        public string Name => "StubAgent";
        public List<string> TriggerCategories => new();
        public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default) => run(++Calls, cancellationToken);
    }

    private static CodeReviewPipeline Pipeline() =>
        new ServiceCollection()
            .AddMultiAgentCodeReview(o =>
            {
                o.Pipeline.AgentTimeoutSeconds = 1;          // fast tests
                o.Pipeline.TimeoutRetryDelaySeconds = 0;
                o.Pipeline.TransientRetryDelaySeconds = 0;
            })
            .BuildServiceProvider().GetRequiredService<CodeReviewPipeline>();

    private static readonly PipelineContext Context =
        new("repo", "HEAD", null, new List<ChangedFile>(), null, null);

    private static AgentResult OneFinding() =>
        new(new List<Finding> { new(Severity.High, FindingCategory.CodeSmell, "a.cs", 1, "d", "r", "", new FixExample("", ""), 0.9) }, "ok");

    private static async Task<AgentResult> Run(CodeReviewPipeline pipeline, ISpecialistAgent agent, ConcurrentQueue<string> warnings, CancellationToken ct = default) =>
        await (Task<AgentResult>)typeof(CodeReviewPipeline).GetMethod("AnalyzeWithRetryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(pipeline, new object[] { agent, Context, ct, warnings })!;

    [Fact]
    public async Task A_specialist_that_never_answers_is_retried_then_reported_missing()
    {
        var agent = new StubSpecialist(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return OneFinding(); });
        var warnings = new ConcurrentQueue<string>();

        var result = await Run(Pipeline(), agent, warnings);

        Assert.Empty(result.Findings);
        Assert.Equal(3, agent.Calls);
        var warning = Assert.Single(warnings);
        Assert.Contains("StubAgent timed out on all 3 attempts", warning);
        Assert.Contains("findings are missing", warning);
    }

    [Fact]
    public async Task A_timeout_that_recovers_leaves_no_warning()
    {
        var agent = new StubSpecialist(async (call, ct) =>
        {
            if (call == 1) await Task.Delay(Timeout.Infinite, ct);
            return OneFinding();
        });
        var warnings = new ConcurrentQueue<string>();

        var result = await Run(Pipeline(), agent, warnings);

        Assert.Single(result.Findings);
        Assert.Equal(2, agent.Calls);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_server_error_such_as_503_is_retried_and_succeeds()
    {
        var agent = new StubSpecialist((call, _) => call < 3
            ? throw new HttpRequestException("Service request failed.\r\nStatus: 503 (Service Unavailable): model is over capacity")
            : Task.FromResult(OneFinding()));
        var warnings = new ConcurrentQueue<string>();

        var result = await Run(Pipeline(), agent, warnings);

        Assert.Single(result.Findings);
        Assert.Equal(3, agent.Calls);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_transient_error_that_never_clears_degrades_the_review_instead_of_aborting_it()
    {
        var agent = new StubSpecialist((_, _) => throw new HttpRequestException("HTTP 429 Too Many Requests"));
        var warnings = new ConcurrentQueue<string>();

        var result = await Run(Pipeline(), agent, warnings);   // used to throw and fail the whole review

        Assert.Empty(result.Findings);
        Assert.Equal(3, agent.Calls);
        Assert.Contains("failed on all 3 attempts with a transient error", Assert.Single(warnings));
    }

    [Fact]
    public async Task A_configuration_error_is_not_retried_and_not_hidden()
    {
        var agent = new StubSpecialist((_, _) => throw new InvalidOperationException("HTTP 401 (invalid_request_error: invalid_api_key)"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Pipeline(), agent, new ConcurrentQueue<string>()));
        Assert.Equal(1, agent.Calls);
    }

    [Fact]
    public async Task Cancelling_the_review_is_not_mistaken_for_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var agent = new StubSpecialist(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return OneFinding(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(Pipeline(), agent, new ConcurrentQueue<string>(), cts.Token));
        Assert.Equal(1, agent.Calls);
    }

    [Theory]
    [InlineData("Status: 503 (Service Unavailable)")]
    [InlineData("Status: 502 (Bad Gateway)")]
    [InlineData("gateway timeout")]
    [InlineData("qwen/qwen3.8-27b is currently over capacity. Please try again")]
    [InlineData("HTTP 429 Too Many Requests")]
    public void Server_side_hiccups_and_rate_limits_are_transient(string message) =>
        Assert.True(CodeReviewPipeline.IsTransient(new HttpRequestException(message)));

    [Theory]
    [InlineData("HTTP 401 (invalid_request_error: invalid_api_key)")]
    [InlineData("The model `qwen/qwen3.6-27b` does not exist (model_not_found)")]
    [InlineData("Could not generate a response")]
    [InlineData("a request id of 5030 was rejected")]      // not a status code
    public void Configuration_errors_and_lookalikes_are_not(string message) =>
        Assert.False(CodeReviewPipeline.IsTransient(new InvalidOperationException(message)));

    [Fact]
    public void The_judge_gets_enough_output_tokens_for_a_reasoning_model()
    {
        var factory = new ServiceCollection().AddMultiAgentCodeReview().BuildServiceProvider().GetRequiredService<AgentFactory>();
        var config = (MultiAgentCodeReview.Core.Configuration.ModelConfig)typeof(AgentFactory)
            .GetMethod("GetModelConfig", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(factory, new object[] { "eval" })!;
        Assert.Equal(4000, config.MaxTokens);
    }
}
