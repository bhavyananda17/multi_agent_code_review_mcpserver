using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;

if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var builder = Host.CreateApplicationBuilder(args);

// Load configuration from environment variables
builder.Configuration.AddEnvironmentVariables(prefix: "MULTIAGENT_");

// Add MultiAgent services
builder.Services.AddMultiAgentCodeReview(configuration: builder.Configuration);

var app = builder.Build();

var pipeline = app.Services.GetRequiredService<CodeReviewPipeline>();

if (args.Length == 0)
{
    ShowHelp();
    return;
}

var command = args[0].ToLowerInvariant();

try
{
    switch (command)
    {
        case "review":
            await RunReviewAsync(pipeline, args);
            break;
        case "ask":
            await RunAskAsync(pipeline, args);
            break;
        case "docs":
            await RunDocsAsync(pipeline, args);
            break;
        default:
            Console.WriteLine($"Unknown command: {command}");
            ShowHelp();
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ Error: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"Details: {ex.InnerException.Message}");
    Environment.Exit(1);
}

static void ShowHelp()
{
    Console.WriteLine("""
        Multi-Agent Code Review System
        
        Usage:
          dotnet run -- review <repo-path> <commit-hash> [base-commit]
          dotnet run -- ask <repo-path> <commit-hash> <question>
          dotnet run -- docs <repo-path> <commit-hash> [base-commit]
        
        Commands:
          review  Run full code review pipeline
          ask     Ask onboarding question about codebase
          docs    Generate documentation
        
        Environment Variables:
          MULTIAGENT_GROQ_API_KEY    Your Groq API key (required)
          MULTIAGENT_GROQ_BASE_URL   Groq API endpoint (default: https://api.groq.com/openai)
        """);
}

static async Task RunReviewAsync(CodeReviewPipeline pipeline, string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: review <repo-path> <commit-hash> [base-commit]");
        return;
    }

    var repoPath = args[1];
    var commitHash = args[2];
    var baseCommit = args.Length > 3 ? args[3] : "HEAD~1";

    Console.WriteLine($"Starting code review for: {repoPath}");
    Console.WriteLine($"Commit: {commitHash}, Base: {baseCommit}");

    var result = await pipeline.RunReviewAsync(repoPath, commitHash, baseCommit);

    Console.WriteLine("\n========== CODE REVIEW REPORT ==========");
    Console.WriteLine($"Repository: {repoPath}");
    Console.WriteLine($"Commit: {commitHash}");
    Console.WriteLine($"Base: {baseCommit}");
    Console.WriteLine($"Findings: {result.Findings?.Count ?? 0}");
    Console.WriteLine();
    Console.WriteLine(result.Summary ?? "No summary available");

    if (result.Findings != null && result.Findings.Count > 0)
    {
        Console.WriteLine("\n========== FINDINGS ==========");
        foreach (var finding in result.Findings)
        {
            Console.WriteLine($"\n[{finding.Severity}] {finding.Category} in {finding.File}:{finding.Line}");
            Console.WriteLine($"  {finding.Description}");
            Console.WriteLine($"  Fix: {finding.Recommendation}");
            if (!string.IsNullOrEmpty(finding.CodeSnippet))
            {
                Console.WriteLine($"  Code: {finding.CodeSnippet}");
            }
        }
    }
    else
    {
        Console.WriteLine("\n✅ Review completed with no findings!");
    }
}

static async Task RunAskAsync(CodeReviewPipeline pipeline, string[] args)
{
    if (args.Length < 4)
    {
        Console.WriteLine("Usage: ask <repo-path> <commit-hash> <question>");
        return;
    }

    var repoPath = args[1];
    var commitHash = args[2];
    var question = string.Join(" ", args.Skip(3));

    Console.WriteLine($"Question: {question}");

    // This would need a synthesis result - for now just show it needs review first
    Console.WriteLine("Note: Onboarding requires a prior review. Run 'review' first.");
}

static async Task RunDocsAsync(CodeReviewPipeline pipeline, string[] args)
{
    if (args.Length < 3)
    {
        Console.WriteLine("Usage: docs <repo-path> <commit-hash> [base-commit]");
        return;
    }

    var repoPath = args[1];
    var commitHash = args[2];
    var baseCommit = args.Length > 3 ? args[3] : "HEAD~1";

    Console.WriteLine($"Generating documentation for: {repoPath}");
    // Documentation generation would be triggered during review
}