using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Orchestration.DI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMultiAgentCodeReview(
        this IServiceCollection services,
        Action<PipelineConfig>? configure = null,
        IConfiguration? configuration = null)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddEnvironmentVariables(prefix: "MULTIAGENT_");

        if (configuration != null)
        {
            builder.AddConfiguration(configuration);
        }

        var config = builder.Build();

        services.Configure<PipelineConfig>(options =>
        {
            configure?.Invoke(options);
            LoadConfigFromEnv(config, options);
        });

        services.AddTransient<IGitOperationsTool, GitOperationsTool>();
        services.AddSingleton<ICodeAnalysisTool, CodeAnalysisTool>();

        services.AddTransient<FilterStage>();
        services.AddTransient<CodeReviewPipeline>();

        services.AddSingleton<AgentFactory>();

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        return services;
    }

    private static void LoadConfigFromEnv(IConfiguration config, PipelineConfig options)
    {
        options.ApiKey = config["API_KEY"]
            ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
            ?? "";

        options.BaseUrl = config["BASE_URL"]
            ?? "https://api.groq.com/openai";

        var modelRoles = new[]
        {
            "triage", "security", "logic", "performance",
            "modernization", "synthesis", "documentation", "onboarding"
        };

        foreach (var role in modelRoles)
        {
            var envKey = $"MODEL_{role.ToUpper()}";
            var modelString = config[envKey];

            if (!string.IsNullOrEmpty(modelString))
            {
                options.Models[role] = new ModelConfig(
                    Role: role,
                    Provider: "groq",
                    ModelId: modelString,
                    Temperature: GetModelTemp(config, role),
                    MaxTokens: GetModelTokens(config, role),
                    RpmLimit: 30,
                    TpmLimit: 6000
                );
            }
        }
    }

    private static double GetModelTemp(IConfiguration config, string role)
    {
        var key = $"MODEL_{role.ToUpper()}_TEMP";
        return double.TryParse(config[key], out var temp) ? temp :
            role switch { "triage" => 0.1, "security" => 0.2, "logic" => 0.3, "performance" => 0.2, "modernization" => 0.4, "synthesis" => 0.4, "documentation" => 0.3, "onboarding" => 0.5, _ => 0.2 };
    }

    private static int GetModelTokens(IConfiguration config, string role)
    {
        var key = $"MODEL_{role.ToUpper()}_TOKENS";
        return int.TryParse(config[key], out var tokens) ? tokens :
            role switch { "triage" => 500, "security" => 2000, "logic" => 3000, "performance" => 2000, "modernization" => 3000, "synthesis" => 4000, "documentation" => 4000, "onboarding" => 3000, _ => 2000 };
    }
}
