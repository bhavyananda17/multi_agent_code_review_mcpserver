using System.Text.Json;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MultiAgentCodeReview.Agents;
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
          ask     Ask onboarding question about codebase (auto-runs review if needed)
          docs    Generate documentation (auto-runs review if needed)

        Environment Variables:
          MULTIAGENT_GROQ_API_KEY    Your Groq API key (required)
          MULTIAGENT_GROQ_BASE_URL   Groq API endpoint (default: https://api.groq.com/openai)
        """);
}

static string GetCachePath(string repoPath) => Path.Combine(repoPath, ".review-cache.json");

static async Task SaveCacheAsync(string repoPath, ReviewCache cache)
{
    var path = GetCachePath(repoPath);
    var options = new JsonSerializerOptions { WriteIndented = true };
    var json = JsonSerializer.Serialize(cache, options);
    await File.WriteAllTextAsync(path, json);
}

static async Task<ReviewCache?> LoadCacheAsync(string repoPath, string commitHash)
{
    var path = GetCachePath(repoPath);
    if (!File.Exists(path))
        return null;

    var json = await File.ReadAllTextAsync(path);
    var cache = JsonSerializer.Deserialize<ReviewCache>(json);
    if (cache?.Context == null || cache.SynthesisResult == null || cache.CommitHash != commitHash)
        return null;

    return cache;
}

static async Task<ReviewOutput> RunPipelineWithCacheAsync(
    CodeReviewPipeline pipeline, string repoPath, string commitHash, string baseCommit)
{
    var cache = await LoadCacheAsync(repoPath, commitHash);
    if (cache != null && cache.Context != null && cache.SynthesisResult != null)
    {
        Console.WriteLine($"Using cached review for commit {commitHash[..8]}");
        return new ReviewOutput(cache.Context, cache.SynthesisResult);
    }

    Console.WriteLine($"Running review pipeline for commit {commitHash}...");
    var output = await pipeline.RunReviewAsync(repoPath, commitHash, baseCommit);

    var reviewCache = new ReviewCache
    {
        Context = output.Context,
        SynthesisResult = output.Result,
        CommitHash = commitHash,
        Timestamp = DateTime.UtcNow
    };
    await SaveCacheAsync(repoPath, reviewCache);
    Console.WriteLine($"Review cached to {GetCachePath(repoPath)}");

    return output;
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

    var output = await pipeline.RunReviewAsync(repoPath, commitHash, baseCommit);

    var cache = new ReviewCache
    {
        Context = output.Context,
        SynthesisResult = output.Result,
        CommitHash = commitHash,
        Timestamp = DateTime.UtcNow
    };
    await SaveCacheAsync(repoPath, cache);

    Console.WriteLine("\n========== CODE REVIEW REPORT ==========");
    Console.WriteLine($"Repository: {repoPath}");
    Console.WriteLine($"Commit: {commitHash}");
    Console.WriteLine($"Base: {baseCommit}");
    Console.WriteLine($"Findings: {output.Result.Findings?.Count ?? 0}");
    Console.WriteLine();
    Console.WriteLine(output.Result.Summary ?? "No summary available");

    if (output.Result.Findings != null && output.Result.Findings.Count > 0)
    {
        Console.WriteLine("\n========== FINDINGS ==========");
        foreach (var finding in output.Result.Findings)
        {
            Console.WriteLine($"\n[{finding.Severity}] {finding.Category} in {finding.File}:{finding.Line}");
            if (!string.IsNullOrEmpty(finding.QuickFix))
            {
                Console.WriteLine($"  Quick fix: {finding.QuickFix}");
            }
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

    var output = await RunPipelineWithCacheAsync(pipeline, repoPath, commitHash, "HEAD~1");

    Console.WriteLine("Generating answer...");
    var onboardingAgent = pipeline.AgentFactory.CreateOnboardingAgent();
    var answer = await onboardingAgent.AnswerAsync(question, output.Context, output.Result);

    Console.WriteLine("\n========== ANSWER ==========");
    Console.WriteLine(answer);
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

    var output = await RunPipelineWithCacheAsync(pipeline, repoPath, commitHash, baseCommit);

    Console.WriteLine("Generating documentation...");
    var docAgent = pipeline.AgentFactory.CreateDocumentationAgent();
    var docs = await docAgent.GenerateDocumentationAsync(output.Context, output.Result);

    Console.WriteLine("\n========== DOCUMENTATION ==========");
    Console.WriteLine(docs);
}
