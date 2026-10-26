using System.ClientModel.Primitives;
using System.Text.Json;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Agents;

/// <summary>
/// Collects the token usage reported by every LLM response made while the scope is open, including calls made on
/// tasks started inside it (the specialists run in parallel). Scopes nest, so a benchmark row and the review inside it
/// can both be measured, and two reviews running at once do not see each other's tokens.
/// </summary>
public sealed class TokenUsageScope : IDisposable
{
    private static readonly AsyncLocal<TokenUsageScope?> Current = new();

    private readonly TokenUsageScope? _parent;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Calls, int Prompt, int Completion)> _byModel = new();

    private TokenUsageScope(TokenUsageScope? parent) => _parent = parent;

    public static TokenUsageScope Begin()
    {
        var scope = new TokenUsageScope(Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>Adds one response's usage to this scope and every enclosing scope.</summary>
    public static void Record(string model, int promptTokens, int completionTokens)
    {
        for (var scope = Current.Value; scope != null; scope = scope._parent)
            scope.Add(model, promptTokens, completionTokens);
    }

    private void Add(string model, int promptTokens, int completionTokens)
    {
        lock (_gate)
        {
            _byModel.TryGetValue(model, out var current);
            _byModel[model] = (current.Calls + 1, current.Prompt + promptTokens, current.Completion + completionTokens);
        }
    }

    public TokenUsage Snapshot()
    {
        lock (_gate)
        {
            var byModel = _byModel.ToDictionary(kv => kv.Key, kv => new ModelTokenUsage(kv.Value.Calls, kv.Value.Prompt, kv.Value.Completion));
            return new TokenUsage(
                byModel.Values.Sum(m => m.Calls), byModel.Values.Sum(m => m.PromptTokens), byModel.Values.Sum(m => m.CompletionTokens), byModel);
        }
    }

    public void Dispose()
    {
        if (Current.Value == this)
            Current.Value = _parent;
    }
}

/// <summary>Reads "usage" off each successful chat-completion response and hands it to the open <see cref="TokenUsageScope"/>.</summary>
internal sealed class UsageTrackingPolicy : PipelinePolicy
{
    private readonly string _model;

    public UsageTrackingPolicy(string model) => _model = model;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        ProcessNext(message, pipeline, index);
        Record(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        await ProcessNextAsync(message, pipeline, index);
        Record(message);
    }

    private void Record(PipelineMessage message)
    {
        try
        {
            var response = message.Response;
            if (response == null || response.IsError)
                return;

            response.BufferContent();
            using var doc = JsonDocument.Parse(response.Content.ToMemory());
            if (doc.RootElement.TryGetProperty("usage", out var usage))
                TokenUsageScope.Record(_model, Read(usage, "prompt_tokens"), Read(usage, "completion_tokens"));
        }
        catch
        {
            // Accounting is a measurement; it must never be able to break a review.
        }
    }

    private static int Read(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
