using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Core.Interfaces;

public interface IAgent
{
    string Name { get; }
    Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default);
}

public interface ITriageAgent : IAgent
{
    Task<TriageResult> ClassifyAsync(PipelineContext context, CancellationToken cancellationToken = default);
}

public interface ISpecialistAgent : IAgent
{
    List<string> TriggerCategories { get; }
}

public interface ISynthesisAgent : IAgent
{
    Task<AgentResult> SynthesizeAsync(
        List<AgentResult> specialistResults,
        PipelineContext context,
        CancellationToken cancellationToken = default);
}

public interface IDocumentationAgent : IAgent
{
    Task<string> GenerateDocumentationAsync(
        PipelineContext context,
        AgentResult synthesisResult,
        CancellationToken cancellationToken = default);
}

public interface IOnboardingAgent : IAgent
{
    Task<string> AnswerAsync(
        string question,
        PipelineContext context,
        AgentResult synthesisResult,
        CancellationToken cancellationToken = default);
}

public interface ILlmClient
{
    Task<string> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.2,
        int maxTokens = 2000,
        CancellationToken cancellationToken = default);

    Task<T> CompleteJsonAsync<T>(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.2,
        int maxTokens = 2000,
        CancellationToken cancellationToken = default) where T : class;
}

public interface IKnowledgeSearchTool
{
    Task<List<CodeSnippet>> SearchCodeAsync(string query, int topK = 5);
    Task<List<Document>> SearchDocumentationAsync(string query, int topK = 3);
    Task<List<CodePattern>> FindSimilarPatternsAsync(string codeSnippet, int topK = 5);
    Task<List<VulnerabilityInfo>> SearchVulnerabilitiesAsync(string pattern);
}

public interface ICodeAnalysisTool
{
    Task<int> GetCyclomaticComplexityAsync(string filePath, string methodName, string basePath = "");
    Task<DependencyGraph> GetDependencyGraphAsync(string filePath, string basePath = "");
    Task<List<CallSite>> FindCallersAsync(string filePath, string methodName, string basePath = "");
    Task<List<CodeSmell>> DetectCodeSmellsAsync(string filePath, string basePath = "");
}

public interface IGitOperationsTool
{
    Task<GitDiff> GetDiffAsync(string fromRef, string toRef = "HEAD");
    Task<List<string>> GetChangedFilesAsync(string fromRef = "HEAD~1", string toRef = "HEAD");
    Task<List<BlameLine>> GetBlameAsync(string filePath);
    Task<List<Commit>> GetFileHistoryAsync(string filePath, int limit = 10);
}