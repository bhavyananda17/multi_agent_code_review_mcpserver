using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using AutoGen;
using AutoGen.Core;
using AutoGen.OpenAI;
using AutoGen.OpenAI.Extension;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Prompts;
using MultiAgentCodeReview.Core.RateLimiting;
using OpenAI;
using OpenAI.Chat;

namespace MultiAgentCodeReview.Agents;

public class AgentFactory
{
    private readonly PipelineConfig _config;
    private readonly RateLimitedHttpClient _rateLimiter;

    public AgentFactory(IOptions<PipelineConfig> config, RateLimitedHttpClient rateLimiter)
    {
        _config = config.Value;
        _rateLimiter = rateLimiter;
    }

    public MultiAgentCodeReview.Core.Interfaces.ITriageAgent CreateTriageAgent()
    {
        var modelConfig = GetModelConfig("triage");
        var agent = CreateOpenAIAgent(modelConfig, "TriageAgent", AgentPrompts.TriageSystemPrompt);
        return new TriageAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreateSecurityAgent()
    {
        var modelConfig = GetModelConfig("security");
        var agent = CreateOpenAIAgent(modelConfig, "SecurityAgent", AgentPrompts.SecuritySystemPrompt);
        return new SecurityAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreatePerformanceAgent()
    {
        var modelConfig = GetModelConfig("performance");
        var agent = CreateOpenAIAgent(modelConfig, "PerformanceAgent", AgentPrompts.PerformanceSystemPrompt);
        return new PerformanceAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreateLogicAgent()
    {
        var modelConfig = GetModelConfig("logic");
        var agent = CreateOpenAIAgent(modelConfig, "LogicAgent", AgentPrompts.LogicSystemPrompt);
        return new LogicAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.IDocumentationAgent CreateDocumentationAgent()
    {
        var modelConfig = GetModelConfig("documentation");
        var agent = CreateOpenAIAgent(modelConfig, "DocumentationAgent", AgentPrompts.TechnicalDocsSystemPrompt);
        return new DocumentationAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.IOnboardingAgent CreateOnboardingAgent()
    {
        var modelConfig = GetModelConfig("onboarding");
        var agent = CreateOpenAIAgent(modelConfig, "OnboardingAgent", AgentPrompts.OnboardingSystemPrompt);
        return new OnboardingAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.IAgent CreateModernizationQuickAgent()
    {
        var modelConfig = GetModelConfig("onboarding");
        var agent = CreateOpenAIAgent(modelConfig, "ModernizationQuickAgent", AgentPrompts.ModernizationQuickSystemPrompt);
        return new ModernizationQuickAgent(agent);
    }

    private IAgent CreateOpenAIAgent(ModelConfig modelConfig, string name, string systemMessage)
    {
        var apiKey = _config.ApiKey
            ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
            ?? throw new InvalidOperationException("No API key configured. Set MULTIAGENT_API_KEY or GROQ_API_KEY.");

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(_config.BaseUrl),
            NetworkTimeout = TimeSpan.FromSeconds(90)
        };

        // qwen models on Groq emit a <think> reasoning trace inline in `content` by default.
        // The previous fix injected "reasoning_effort":"none" via reflection into
        // ChatCompletionOptions.SerializedAdditionalRawData, but OpenAI SDK 2.0.0-beta.10
        // silently DROPS that backing field when serializing the request (verified by
        // capturing the outgoing body: only model/messages/temperature/max_tokens were sent),
        // so the suppression never reached Groq. A pipeline policy rewrites the serialized
        // JSON body itself — public API, no SDK internals to rot. Guard matches any model id
        // starting with "qwen" so both "qwen/qwen3-32b" and unprefixed ids like "qwen3.6-27b"
        // are covered (Groq has renamed these ids before).
        // NOTE: must be added BEFORE the ChatClient is constructed — the constructor freezes
        // these options (AssertNotFrozen throws on later changes).
        if (modelConfig.ModelId.StartsWith("qwen", StringComparison.OrdinalIgnoreCase))
        {
            clientOptions.AddPolicy(new ReasoningEffortPolicy("none"), PipelinePosition.PerCall);
        }

        var chatClient = new ChatClient(modelConfig.ModelId, apiKey, clientOptions);

        var chatOptions = new ChatCompletionOptions
        {
            Temperature = (float)modelConfig.Temperature,
            MaxTokens = modelConfig.MaxTokens
        };

        return new OpenAIChatAgent(
            chatClient: chatClient,
            name: name,
            options: chatOptions,
            systemMessage: systemMessage)
            .RegisterMessageConnector();
    }

    private ModelConfig GetModelConfig(string role)
    {
        if (_config.Models.TryGetValue(role, out var modelConfig))
            return modelConfig;

        return new ModelConfig(
            Role: role,
            Provider: "groq",
            ModelId: role switch
            {
                "triage" => "llama-3.1-8b-instant",
                "onboarding" => "llama-3.1-8b-instant",
                _ => "llama-3.3-70b-versatile"
            },
            Temperature: role switch
            {
                "triage" => 0.1,
                "security" => 0.2,
                "logic" => 0.3,
                "performance" => 0.2,
                "modernization" => 0.4,
                "synthesis" => 0.4,
                "documentation" => 0.3,
                "onboarding" => 0.5,
                _ => 0.2
            },
            MaxTokens: role switch
            {
                "triage" => 500,
                "security" => 2000,
                "logic" => 3000,
                "performance" => 2000,
                "modernization" => 3000,
                "synthesis" => 4000,
                "documentation" => 8000,
                "onboarding" => 3000,
                _ => 2000
            },
            RpmLimit: 30,
            TpmLimit: role == "synthesis" ? 12000 : 6000
        );
    }
}

/// <summary>
/// Injects a top-level field (e.g. "reasoning_effort": "none") into the serialized JSON
/// request body just before it goes over the wire. ChatCompletionOptions has no public
/// property for this Groq-specific parameter, and its internal raw-data store is dropped
/// by SDK 2.0.0-beta.10 during serialization, so rewriting the body at the HTTP layer is
/// the only reliable way to send it.
/// </summary>
internal sealed class ReasoningEffortPolicy : PipelinePolicy
{
    private readonly string _effort;

    public ReasoningEffortPolicy(string effort)
    {
        _effort = effort;
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        Inject(message);
        ProcessNext(message, pipeline, index);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        Inject(message);
        return ProcessNextAsync(message, pipeline, index);
    }

    private void Inject(PipelineMessage message)
    {
        var request = message.Request;
        if (request == null)
            return;

        var content = request.Content;
        if (content == null)
            return;

        string json;
        try
        {
            using var ms = new MemoryStream();
            content.WriteTo(ms, CancellationToken.None);
            json = Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ReasoningEffortPolicy] could not buffer request body: {ex.Message}");
            return;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch
        {
            node = null;
        }

        if (node is not JsonObject obj)
        {
            Console.Error.WriteLine("[ReasoningEffortPolicy] request body was not a JSON object — reasoning_effort not injected");
            return;
        }

        obj["reasoning_effort"] = _effort;

        request.Content = BinaryContent.Create(BinaryData.FromString(obj.ToJsonString()));
    }
}
