using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Tests;

public class RateLimitDetectionTests
{
    [Theory]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("Rate limit reached for model openai/gpt-oss-120b")]
    [InlineData("error code: rate_limit_exceeded")]
    [InlineData("too many requests, slow down")]
    public void Real_rate_limit_signals_are_detected(string message) =>
        Assert.True(CodeReviewPipeline.IsRateLimit(new HttpRequestException(message)));

    [Theory]
    [InlineData("Could not generate a response")]
    [InlineData("The operation was canceled")]
    [InlineData("Failed to iterate the collection")]
    [InlineData("HTTP 500 Internal Server Error")]
    public void Ordinary_errors_that_merely_contain_the_letters_rate_are_not(string message) =>
        Assert.False(CodeReviewPipeline.IsRateLimit(new InvalidOperationException(message)));

    [Fact]
    public void The_signal_is_found_in_a_nested_inner_exception() =>
        Assert.True(CodeReviewPipeline.IsRateLimit(new InvalidOperationException("outer", new Exception("middle", new Exception("429")))));
}
