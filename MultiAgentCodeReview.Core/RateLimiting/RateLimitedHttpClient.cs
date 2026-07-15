using System.Net.Http;
using MultiAgentCodeReview.Core.Configuration;

namespace MultiAgentCodeReview.Core.RateLimiting;

public class RateLimitedHttpClient
{
    private readonly TokenBudget _tokenBudget;
    private readonly Dictionary<string, HttpClient> _clients = new();

    public RateLimitedHttpClient(TokenBudget tokenBudget)
    {
        _tokenBudget = tokenBudget;
    }

    public HttpClient CreateClient(ModelConfig modelConfig)
    {
        var key = $"{modelConfig.Provider}:{modelConfig.ModelId}";
        
        if (_clients.TryGetValue(key, out var existing))
            return existing;

        var handler = new RateLimitingHandler(_tokenBudget, modelConfig);
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        _clients[key] = client;
        return client;
    }
}

public class RateLimitingHandler : DelegatingHandler
{
    private readonly TokenBudget _tokenBudget;
    private readonly ModelConfig _modelConfig;

    public RateLimitingHandler(TokenBudget tokenBudget, ModelConfig modelConfig)
        : base(new HttpClientHandler())
    {
        _tokenBudget = tokenBudget;
        _modelConfig = modelConfig;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, 
        CancellationToken cancellationToken)
    {
        // Estimate tokens from request
        var requestContent = request.Content != null 
            ? await request.Content.ReadAsStringAsync(cancellationToken) 
            : "";
        var estimatedTokens = EstimateTokens(requestContent) + _modelConfig.MaxTokens;

        // Wait for budget
        await _tokenBudget.WaitForBudgetAsync(estimatedTokens, cancellationToken);

        // Send request
        var response = await base.SendAsync(request, cancellationToken);

        // Record actual usage if available
        if (response.Headers.TryGetValues("x-usage-tokens", out var usageHeaders))
        {
            if (int.TryParse(usageHeaders.FirstOrDefault(), out var actualTokens))
            {
                _tokenBudget.RecordUsage(actualTokens);
            }
        }

        return response;
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 chars per token
        return Math.Max(1, text.Length / 4);
    }
}