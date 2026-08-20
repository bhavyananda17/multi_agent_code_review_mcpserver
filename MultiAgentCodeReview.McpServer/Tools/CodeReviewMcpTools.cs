using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Reporting;

namespace MultiAgentCodeReview.McpServer.Tools;

[McpServerToolType]
public class CodeReviewMcpTools
{
    private readonly CodeReviewPipeline _pipeline;
    private readonly AgentFactory _agentFactory;
    private static readonly Dictionary<string, ReviewOutput> _pipelineCache = new();
    private const string ReportFileName = ".codereview/last_report.md";

    public CodeReviewMcpTools(CodeReviewPipeline pipeline, AgentFactory agentFactory)
    {
        _pipeline = pipeline;
        _agentFactory = agentFactory;
    }

    [McpServerTool]
    [Description("Run a full multi-agent code review on a git repository. Use this when the user asks to review a commit, check a PR, or audit recent changes. Do NOT use for single-file questions — use ask_codebase instead.")]
    public async Task<string> ReviewRepo(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The commit to review (HEAD, sha, branch name)")] string commit_hash,
        [Description("Base to diff against (defaults to commit_hash~1)")] string base_commit = "")
    {
        var reportDir = Path.Combine(repo_path, ".codereview");
        var reportPath = Path.Combine(reportDir, "last_report.md");
        var metaPath = Path.Combine(reportDir, "last_report.meta.json");

        if (File.Exists(reportPath) && File.Exists(metaPath))
        {
            try
            {
                var metaJson = await File.ReadAllTextAsync(metaPath);
                var meta = JsonSerializer.Deserialize<ReportMeta>(metaJson);
                if (meta?.CommitHash == commit_hash)
                {
                    return await File.ReadAllTextAsync(reportPath);
                }
            }
            catch { /* fall through to re-run pipeline */ }
        }

        var output = await _pipeline.RunReviewAsync(repo_path, commit_hash, string.IsNullOrWhiteSpace(base_commit) ? null : base_commit);

        _pipelineCache[repo_path] = output;

        var modernizationAgent = _agentFactory.CreateModernizationQuickAgent();
        var modernizationResult = await modernizationAgent.AnalyzeAsync(output.Context);
        var modernizationNotes = modernizationResult.Summary;

        var report = ReportFormatter.FormatReport(output);

        if (!string.IsNullOrWhiteSpace(modernizationNotes))
        {
            report += $"\n---\n\n## Quick Modernization Notes\n\n{modernizationNotes}\n";
        }

        Directory.CreateDirectory(reportDir);
        await File.WriteAllTextAsync(reportPath, report);
        await File.WriteAllTextAsync(metaPath, JsonSerializer.Serialize(new ReportMeta { CommitHash = commit_hash }));

        return report;
    }

    [McpServerTool]
    [Description("Ask a natural language question about a codebase. Use this for questions like 'where is auth handled?', 'what calls IUserRepository?', or 'which files would break if I change X?'. Do NOT use for full reviews — use review_repo instead.")]
    public async Task<string> AskCodebase(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The natural language question to ask")] string question)
    {
        ReviewOutput output;
        if (_pipelineCache.TryGetValue(repo_path, out var cached))
        {
            output = cached;
        }
        else
        {
            output = await _pipeline.RunReviewAsync(repo_path, "HEAD", "HEAD~1");
            _pipelineCache[repo_path] = output;
        }

        var onboardingAgent = _agentFactory.CreateOnboardingAgent();
        var answer = await onboardingAgent.AnswerAsync(question, output.Context, output.Result);

        return answer;
    }

    [McpServerTool]
    [Description("Generate project documentation for a codebase. Use this when the user asks to generate or update documentation, README, API docs, or architecture docs.")]
    public async Task<string> GenerateDocs(
        [Description("Absolute path to the git repo on disk")] string repo_path,
        [Description("The commit to document (HEAD, sha, branch name)")] string commit_hash,
        [Description("Base to diff against (defaults to commit_hash~1)")] string base_commit = "")
    {
        ReviewOutput output;
        if (_pipelineCache.TryGetValue(repo_path, out var cached) && cached.Context.CommitHash == commit_hash)
        {
            output = cached;
        }
        else
        {
            output = await _pipeline.RunReviewAsync(repo_path, commit_hash, string.IsNullOrWhiteSpace(base_commit) ? null : base_commit);
            _pipelineCache[repo_path] = output;
        }

        var docAgent = _agentFactory.CreateDocumentationAgent();
        var docs = await docAgent.GenerateDocumentationAsync(output.Context, output.Result);

        var reportsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "CodeReviewReports");
        Directory.CreateDirectory(reportsDir);
        var repoFolder = new DirectoryInfo(repo_path).Name;
        var reportPath = Path.Combine(reportsDir, $"{repoFolder}_AGENT_REPORT.md");
        await File.WriteAllTextAsync(reportPath, docs);

        return $"Documentation saved to {reportPath}\n\n{docs}";
    }

    private sealed class ReportMeta
    {
        public string CommitHash { get; set; } = string.Empty;
    }
}
