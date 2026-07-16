using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Agents;

public class DocumentationAgent : MultiAgentCodeReview.Core.Interfaces.IDocumentationAgent
{
    public string Name => "DocumentationAgent";

    private readonly IAgent _agent;

    public DocumentationAgent(IAgent agent)
    {
        _agent = agent;
    }

    public async Task<string> GenerateDocumentationAsync(
        PipelineContext context,
        AgentResult synthesisResult,
        CancellationToken cancellationToken = default)
    {
        var userPrompt = BuildDocumentationPrompt(context, synthesisResult);
        var response = await _agent.GenerateReplyAsync(
            new[] { new TextMessage(Role.User, userPrompt) },
            cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return content ?? "";
    }

    public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResult(
            new List<Finding>(),
            "Documentation agent requires synthesis result"));
    }

    private string BuildDocumentationPrompt(PipelineContext context, AgentResult synthesisResult)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Generate a comprehensive, single-document project README that covers everything a developer needs to understand, set up, and contribute to this project.");
        sb.AppendLine();
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        sb.AppendLine();
        sb.AppendLine("Changed files:");
        foreach (var file in context.ChangedFiles)
        {
            sb.AppendLine($"  - {file.Path} (+{file.Additions} -{file.Deletions})");
        }
        sb.AppendLine();
        sb.AppendLine("Code Review Summary:");
        sb.AppendLine(synthesisResult.Summary);
        if (synthesisResult.Findings?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Key findings:");
            foreach (var finding in synthesisResult.Findings.Take(10))
            {
                sb.AppendLine($"  - [{finding.Severity}] {finding.Category}: {finding.Description}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("REQUIRED SECTIONS (write ALL of these, be thorough and detailed):");
        sb.AppendLine();
        sb.AppendLine("1. # Project Name");
        sb.AppendLine("   - One-line description of what this project does");
        sb.AppendLine("   - Badges (build status, license, .NET version)");
        sb.AppendLine();
        sb.AppendLine("2. ## Overview");
        sb.AppendLine("   - What problem this project solves");
        sb.AppendLine("   - Key features and capabilities");
        sb.AppendLine("   - Who this project is for");
        sb.AppendLine();
        sb.AppendLine("3. ## Architecture");
        sb.AppendLine("   - High-level architecture diagram using mermaid");
        sb.AppendLine("   - Project structure with explanations for each directory");
        sb.AppendLine("   - Data flow through the system");
        sb.AppendLine("   - Key design decisions and patterns used");
        sb.AppendLine();
        sb.AppendLine("4. ## Tech Stack");
        sb.AppendLine("   - Complete list of frameworks, libraries, and tools");
        sb.AppendLine("   - Version requirements for each");
        sb.AppendLine();
        sb.AppendLine("5. ## Prerequisites");
        sb.AppendLine("   - Required SDKs and tools with exact versions");
        sb.AppendLine("   - External services needed (databases, APIs, etc)");
        sb.AppendLine();
        sb.AppendLine("6. ## Getting Started");
        sb.AppendLine("   - Step-by-step setup instructions");
        sb.AppendLine("   - Environment variable configuration");
        sb.AppendLine("   - How to run the project for the first time");
        sb.AppendLine();
        sb.AppendLine("7. ## Configuration");
        sb.AppendLine("   - All configuration options explained");
        sb.AppendLine("   - Environment variables table");
        sb.AppendLine("   - Example .env file");
        sb.AppendLine();
        sb.AppendLine("8. ## Usage");
        sb.AppendLine("   - CLI commands with examples");
        sb.AppendLine("   - MCP server setup for AI assistants");
        sb.AppendLine("   - Common workflows");
        sb.AppendLine();
        sb.AppendLine("9. ## API Reference");
        sb.AppendLine("   - Key interfaces and their purposes");
        sb.AppendLine("   - How to extend the system with new agents");
        sb.AppendLine();
        sb.AppendLine("10. ## Development");
        sb.AppendLine("    - How to add new agents");
        sb.AppendLine("    - How to modify the pipeline");
        sb.AppendLine("    - Testing approach");
        sb.AppendLine();
        sb.AppendLine("11. ## Troubleshooting");
        sb.AppendLine("    - Common issues and solutions");
        sb.AppendLine("    - Rate limiting considerations");
        sb.AppendLine();
        sb.AppendLine("12. ## Contributing");
        sb.AppendLine("    - Code style and conventions");
        sb.AppendLine("    - PR process");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Write the full document. Do not use placeholders like '[TODO]' or 'TBD'.");
        sb.AppendLine("Use mermaid diagrams for architecture. Be specific with code examples from the actual codebase.");
        sb.AppendLine("This should be a production-quality README that a new developer could use to onboard.");

        return sb.ToString();
    }
}

public class OnboardingAgent : MultiAgentCodeReview.Core.Interfaces.IOnboardingAgent
{
    public string Name => "OnboardingAgent";

    private readonly IAgent _agent;

    public OnboardingAgent(IAgent agent)
    {
        _agent = agent;
    }

    public async Task<string> AnswerAsync(
        string question,
        PipelineContext context,
        AgentResult synthesisResult,
        CancellationToken cancellationToken = default)
    {
        var userPrompt = BuildOnboardingPrompt(question, context, synthesisResult);
        var response = await _agent.GenerateReplyAsync(
            new[] { new TextMessage(Role.User, userPrompt) },
            cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return content ?? "";
    }

    public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResult(
            new List<Finding>(),
            "Onboarding agent requires a question and synthesis result"));
    }

    private string BuildOnboardingPrompt(string question, PipelineContext context, AgentResult synthesisResult)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Question: {question}");
        sb.AppendLine();
        sb.AppendLine("Project Context:");
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        sb.AppendLine();
        sb.AppendLine("Recent Code Review Summary:");
        sb.AppendLine(synthesisResult.Summary);
        sb.AppendLine();
        sb.AppendLine("Answer the question using the codebase and review context. Be friendly, educational, and use examples from the actual code.");

        return sb.ToString();
    }
}
