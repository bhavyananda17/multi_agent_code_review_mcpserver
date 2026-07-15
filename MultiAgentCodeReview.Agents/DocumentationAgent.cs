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
        sb.AppendLine("Generate project documentation:");
        sb.AppendLine();
        sb.AppendLine($"Repository: {context.RepositoryPath}");
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine($"Changed Files: {context.ChangedFiles.Count}");
        sb.AppendLine();
        sb.AppendLine("Code Review Summary:");
        sb.AppendLine(synthesisResult.Summary);
        sb.AppendLine();
        sb.AppendLine("Generate: README.md, API_DOCUMENTATION.md, ARCHITECTURE.md, INSTALLATION_GUIDE.md");
        sb.AppendLine("Extract from code: tech stack, project structure, config, API endpoints, data flow, design decisions.");
        sb.AppendLine("Use mermaid for diagrams. Mark auto-generated sections for review.");

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
