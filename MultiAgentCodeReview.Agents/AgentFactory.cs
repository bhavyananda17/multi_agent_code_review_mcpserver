using AutoGen;
using AutoGen.Core;
using AutoGen.OpenAI;
using AutoGen.OpenAI.Extension;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Prompts;
using OpenAI;
using OpenAI.Chat;

namespace MultiAgentCodeReview.Agents;

public class AgentFactory
{
    private readonly PipelineConfig _config;

    public AgentFactory(IOptions<PipelineConfig> config)
    {
        _config = config.Value;
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

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreateLogicAgent()
    {
        var modelConfig = GetModelConfig("logic");
        var agent = CreateOpenAIAgent(modelConfig, "LogicAgent", AgentPrompts.LogicSystemPrompt);
        return new LogicAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreatePerformanceAgent()
    {
        var modelConfig = GetModelConfig("performance");
        var agent = CreateOpenAIAgent(modelConfig, "PerformanceAgent", AgentPrompts.PerformanceSystemPrompt);
        return new PerformanceAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISpecialistAgent CreateModernizationAgent()
    {
        var modelConfig = GetModelConfig("modernization");
        var agent = CreateOpenAIAgent(modelConfig, "ModernizationAgent", AgentPrompts.ModernizationSystemPrompt);
        return new ModernizationAgent(agent);
    }

    public MultiAgentCodeReview.Core.Interfaces.ISynthesisAgent CreateSynthesisAgent()
    {
        var modelConfig = GetModelConfig("synthesis");
        var agent = CreateOpenAIAgent(modelConfig, "SynthesisAgent", AgentPrompts.SynthesisSystemPrompt);
        return new SynthesisAgent(agent);
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

    private IAgent CreateOpenAIAgent(ModelConfig modelConfig, string name, string systemMessage)
    {
        var apiKey = _config.ApiKey
            ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
            ?? throw new InvalidOperationException("No API key configured. Set MULTIAGENT_API_KEY or GROQ_API_KEY.");

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(_config.BaseUrl)
        };

        var chatClient = new ChatClient(modelConfig.ModelId, apiKey, options);

        return new OpenAIChatAgent(
            chatClient: chatClient,
            name: name,
            systemMessage: systemMessage,
            temperature: (float)modelConfig.Temperature,
            maxTokens: modelConfig.MaxTokens)
            .RegisterMessageConnector();
    }

    private ModelConfig GetModelConfig(string role)
    {
        if (_config.Models.TryGetValue(role, out var modelConfig))
            return modelConfig;

        return new ModelConfig(
            Role: role,
            Provider: "groq",
            ModelId: "llama-3.3-70b-versatile",
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
                "documentation" => 4000,
                "onboarding" => 3000,
                _ => 2000
            },
            RpmLimit: 30,
            TpmLimit: role == "synthesis" ? 12000 : 6000
        );
    }
}
