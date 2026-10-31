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

public interface IEvalAgent : IAgent
{
    /// <summary>
    /// Scores one batch of findings. The result is positionally aligned with <paramref name="findings"/>;
    /// an entry is null when the model returned no usable score for that finding.
    /// </summary>
    Task<List<FindingEvaluation?>> EvaluateAsync(
        PipelineContext context,
        IReadOnlyList<Finding> findings,
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