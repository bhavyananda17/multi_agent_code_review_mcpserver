using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Tests;

public class TokenAccountingTests
{
    [Fact]
    public void Usage_is_summed_per_model_and_in_total()
    {
        using var scope = TokenUsageScope.Begin();
        TokenUsageScope.Record("model-a", 100, 20);
        TokenUsageScope.Record("model-a", 50, 10);
        TokenUsageScope.Record("model-b", 7, 3);

        var usage = scope.Snapshot();
        Assert.Equal((3, 157, 33, 190), (usage.Calls, usage.PromptTokens, usage.CompletionTokens, usage.Total));
        Assert.Equal(new ModelTokenUsage(2, 150, 30), usage.ByModel["model-a"]);
        Assert.Equal(new ModelTokenUsage(1, 7, 3), usage.ByModel["model-b"]);
    }

    [Fact]
    public void Nothing_recorded_outside_a_scope_is_an_error_or_leaks_into_the_next_one()
    {
        TokenUsageScope.Record("orphan", 999, 999);   // no scope open: dropped, not stored anywhere
        using var scope = TokenUsageScope.Begin();
        Assert.Equal(0, scope.Snapshot().Calls);
    }

    [Fact]
    public void Nested_scopes_each_see_their_own_calls_and_the_outer_one_sees_everything()
    {
        using var outer = TokenUsageScope.Begin();
        TokenUsageScope.Record("m", 10, 1);
        using (var inner = TokenUsageScope.Begin())
        {
            TokenUsageScope.Record("m", 20, 2);
            Assert.Equal(22, inner.Snapshot().Total);
        }
        TokenUsageScope.Record("m", 30, 3);

        Assert.Equal(66, outer.Snapshot().Total);
    }

    [Fact]
    public async Task Two_reviews_running_at_once_do_not_see_each_others_tokens()
    {
        async Task<int> Review(int promptTokens)
        {
            using var scope = TokenUsageScope.Begin();
            await Task.Yield();
            await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => { await Task.Delay(5); TokenUsageScope.Record("m", promptTokens, 0); }));
            return scope.Snapshot().PromptTokens;
        }

        var results = await Task.WhenAll(Review(1), Review(1000));
        Assert.Equal(new[] { 5, 5000 }, results);
    }

    [Fact]
    public void Usage_objects_can_be_added_together_model_by_model()
    {
        var a = new TokenUsage(2, 100, 10, new() { ["x"] = new ModelTokenUsage(2, 100, 10) });
        var b = new TokenUsage(1, 5, 1, new() { ["x"] = new ModelTokenUsage(1, 5, 1), ["y"] = new ModelTokenUsage(0, 0, 0) });

        var sum = a.Plus(b);
        Assert.Equal((3, 105, 11), (sum.Calls, sum.PromptTokens, sum.CompletionTokens));
        Assert.Equal(new ModelTokenUsage(3, 105, 11), sum.ByModel["x"]);
        Assert.Same(a, a.Plus(null));
        Assert.Equal(0, TokenUsage.Empty.Total);
    }
}
