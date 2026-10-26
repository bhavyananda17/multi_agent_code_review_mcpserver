namespace MultiAgentCodeReview.Core.Models;

public record ModelTokenUsage(int Calls, int PromptTokens, int CompletionTokens);

/// <summary>
/// Tokens an LLM provider reported for the calls of one review. Counted from each response's own "usage" field,
/// so it is what the provider billed or rate-limited, not an estimate.
/// </summary>
public record TokenUsage(int Calls, int PromptTokens, int CompletionTokens, Dictionary<string, ModelTokenUsage> ByModel)
{
    public int Total => PromptTokens + CompletionTokens;

    public static TokenUsage Empty => new(0, 0, 0, new Dictionary<string, ModelTokenUsage>());

    public TokenUsage Plus(TokenUsage? other)
    {
        if (other == null)
            return this;

        var byModel = new Dictionary<string, ModelTokenUsage>(ByModel);
        foreach (var (model, usage) in other.ByModel)
        {
            byModel[model] = byModel.TryGetValue(model, out var existing)
                ? new ModelTokenUsage(existing.Calls + usage.Calls, existing.PromptTokens + usage.PromptTokens, existing.CompletionTokens + usage.CompletionTokens)
                : usage;
        }
        return new TokenUsage(Calls + other.Calls, PromptTokens + other.PromptTokens, CompletionTokens + other.CompletionTokens, byModel);
    }
}
