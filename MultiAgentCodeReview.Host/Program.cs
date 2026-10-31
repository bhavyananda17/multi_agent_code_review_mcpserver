using System.Globalization;
using System.Text.Json;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Benchmarking;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Tools;

if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var builder = Host.CreateApplicationBuilder(args);

// Load configuration from environment variables
builder.Configuration.AddEnvironmentVariables(prefix: "MULTIAGENT_");

// Add MultiAgent services
// Deliberately NOT passing builder.Configuration: it includes UNPREFIXED environment variables, so an
// unrelated API_KEY (e.g. the REST API's X-Api-Key gate secret sitting in .env) would be picked up as the
// LLM key and every request would 401. The extension reads MULTIAGENT_* variables itself, plus GROQ_API_KEY.
// Same fix already applied in the Api and McpServer projects.
builder.Services.AddMultiAgentCodeReview();

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
            await RunReviewAsync(pipeline, app.Services.GetRequiredService<IGitHubIssueService>(), args);
            break;
        case "ask":
            await RunAskAsync(pipeline, args);
            break;
        case "docs":
            await RunDocsAsync(pipeline, args);
            break;
        case "bench":
            await RunBenchAsync(app.Services.GetRequiredService<BenchmarkRunner>(), args);
            break;
        case "bench-compare":
            await RunBenchCompareAsync(args);
            break;
        case "bench-summary":
            await RunBenchSummaryAsync(args);
            break;
        default:
            Console.WriteLine($"Unknown command: {command}");
            ShowHelp();
            break;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"\nError: {ex.Message}");
    if (ex.InnerException != null)
        Console.WriteLine($"Details: {ex.InnerException.Message}");
    Environment.Exit(1);
}

static void ShowHelp()
{
    Console.WriteLine("""
        Multi-Agent Code Review System

        Usage:
          dotnet run -- review <repo-path> <commit-hash> [base-commit] [--issue <url|number>]
          dotnet run -- ask <repo-path> <commit-hash> <question>
          dotnet run -- docs <repo-path> <commit-hash> [base-commit]
          dotnet run -- bench <input.csv> [--out results.json] [--md summary.md] [--threshold 0.25]
                              [--one-to-one] [--include-low-confidence] [--record <dir> | --replay <dir>]
          dotnet run -- bench-compare <a-results.json> <b-results.json> [--exclude-degraded] [--out comparison.md]
          dotnet run -- bench-summary <results.json> [--md summary.md] [--json recomputed.json]

        Commands:
          review  Run full code review pipeline (--issue links a GitHub issue to focus the review)
          ask     Ask onboarding question about codebase (auto-runs review if needed)
          docs    Generate documentation (auto-runs review if needed)
          bench   Run the pipeline over a CSV of (repo URL, commit SHA, human findings) and report precision/recall/F1
          bench-compare  Paired comparison of two bench results files (before/after, or two judges)

        Environment Variables:
          MULTIAGENT_GROQ_API_KEY    Your Groq API key (required)
          MULTIAGENT_GROQ_BASE_URL   Groq API endpoint (default: https://api.groq.com/openai)
          MULTIAGENT_GITHUB_TOKEN    Optional GitHub token, only needed for --issue on private repos
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

// Pulls "--issue <value>" / "--issue=<value>" out of the args so the existing positional
// parsing (repo, commit, base) is unaffected when the flag is absent.
static (string? Issue, string[] Remaining) ExtractIssueOption(string[] args)
{
    string? issue = null;
    var rest = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--issue")
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException("--issue requires a value (an issue URL or number).");
            issue = args[++i];
        }
        else if (args[i].StartsWith("--issue=", StringComparison.Ordinal))
        {
            issue = args[i]["--issue=".Length..];
        }
        else
        {
            rest.Add(args[i]);
        }
    }
    return (issue, rest.ToArray());
}

static async Task RunReviewAsync(CodeReviewPipeline pipeline, IGitHubIssueService issues, string[] args)
{
    var (issueRef, positional) = ExtractIssueOption(args);
    args = positional;

    if (args.Length < 3)
    {
        Console.WriteLine("Usage: review <repo-path> <commit-hash> [base-commit] [--issue <url|number>]");
        return;
    }

    var repoPath = args[1];
    var commitHash = args[2];
    var baseCommit = args.Length > 3 ? args[3] : "HEAD~1";

    Console.WriteLine($"Starting code review for: {repoPath}");
    Console.WriteLine($"Commit: {commitHash}, Base: {baseCommit}");

    // Fetched before the pipeline runs: a bad/unreachable issue fails fast, before any LLM quota is spent.
    var issue = await issues.GetIssueAsync(issueRef, repoPath);
    if (issue != null)
        Console.WriteLine($"Linked issue: #{issue.Number} {issue.Title}");

    var output = await pipeline.RunReviewAsync(repoPath, commitHash, baseCommit, issue);

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
            var evalTag = finding.Evaluation is { } ev
                ? $" [self-eval {ev.Total}/{FindingEvaluation.MaxTotal}{(ev.LowConfidence ? ", LOW CONFIDENCE" : "")}]"
                : "";
            Console.WriteLine($"\n[{finding.Severity}] {finding.Category} in {finding.File}:{finding.Line}{evalTag}");
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

static async Task RunBenchAsync(BenchmarkRunner runner, string[] args)
{
    string? csvPath = null, outPath = null, mdPath = null;
    var options = new BenchmarkOptions();

    for (var i = 1; i < args.Length; i++)
    {
        var arg = args[i];
        string? inline = null;
        var eq = arg.StartsWith("--", StringComparison.Ordinal) ? arg.IndexOf('=') : -1;
        if (eq > 0)
        {
            inline = arg[(eq + 1)..];
            arg = arg[..eq];
        }

        string Value() => inline ?? (i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{arg} requires a value."));

        switch (arg)
        {
            case "--out": outPath = Value(); break;
            case "--md": mdPath = Value(); break;
            case "--threshold":
                if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) || threshold <= 0 || threshold > 1)
                    throw new ArgumentException("--threshold must be a number greater than 0 and at most 1.");
                options.SimilarityThreshold = threshold;
                break;
            case "--record": options.RecordDirectory = Value(); break;
            case "--replay": options.ReplayDirectory = Value(); break;
            case "--one-to-one": options.OneToOne = true; break;
            case "--include-low-confidence": options.IncludeLowConfidence = true; break;
            default:
                if (arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unknown option '{arg}'.");
                if (csvPath != null)
                    throw new ArgumentException($"Unexpected argument '{arg}'.");
                csvPath = arg;
                break;
        }
    }

    if (csvPath == null)
    {
        Console.WriteLine("Usage: bench <input.csv> [--out results.json] [--md summary.md] [--threshold 0.25] [--one-to-one] [--include-low-confidence] [--record <dir> | --replay <dir>]");
        return;
    }
    if (!File.Exists(csvPath))
        throw new FileNotFoundException($"CSV file not found: {csvPath}");

    if (options.RecordDirectory != null && options.ReplayDirectory != null)
        throw new ArgumentException("--record and --replay cannot be used together.");

    outPath ??= "bench-results.json";
    mdPath ??= Path.ChangeExtension(outPath, ".md");
    if (string.Equals(Path.GetFullPath(outPath), Path.GetFullPath(mdPath), StringComparison.Ordinal))
        throw new ArgumentException("--out and --md must be different files.");

    // Validates every row up front, so a typo on row 40 fails now rather than after 39 paid-for reviews.
    var rows = BenchmarkCsv.ParseRows(await File.ReadAllTextAsync(csvPath));

    Console.WriteLine($"Benchmark: {rows.Count} row(s) from {csvPath}");
    Console.WriteLine("Each row clones the repo and runs the full pipeline (often a minute or more per PR).");
    Console.WriteLine("Ctrl+C stops cleanly and keeps the partial results; a second Ctrl+C force-quits.\n");

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        if (cts.IsCancellationRequested)
            return; // second Ctrl+C: let the process die
        e.Cancel = true;
        cts.Cancel();
        Console.WriteLine("\nInterrupted: cleaning up and writing partial results...");
    };
    Console.CancelKeyPress += onCancel;

    try
    {
        // Rewritten after every row, so a crash or Ctrl+C never loses finished work.
        var result = await runner.RunAsync(rows, options, Console.WriteLine,
            partial => WriteBenchOutputs(partial, outPath, mdPath), cts.Token);
        WriteBenchOutputs(result, outPath, mdPath);

        Console.WriteLine();
        Console.WriteLine(BenchmarkReportWriter.ToMarkdown(result));
        Console.WriteLine($"Results JSON:     {Path.GetFullPath(outPath)}");
        Console.WriteLine($"Markdown summary: {Path.GetFullPath(mdPath)}");

        if (result.Aggregate.Succeeded == 0)
            Environment.ExitCode = 1;
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

// Re-renders a saved results file with the current tooling: the per-PR rows are used exactly as saved, and the
// aggregates (averages, bootstrap intervals, warning counts) are recomputed from them. Handy for results written by an
// older version of the tool.
static async Task RunBenchSummaryAsync(string[] args)
{
    string? file = null, mdPath = null, jsonPath = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--md") mdPath = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--md requires a value.");
        else if (args[i] == "--json") jsonPath = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--json requires a value.");
        else if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
        else file = file == null ? args[i] : throw new ArgumentException($"Unexpected argument '{args[i]}'.");
    }
    if (file == null)
    {
        Console.WriteLine("Usage: bench-summary <results.json> [--md summary.md] [--json recomputed.json]");
        return;
    }
    if (!File.Exists(file))
        throw new FileNotFoundException($"Results file not found: {file}");

    var saved = BenchmarkComparer.Load(await File.ReadAllTextAsync(file));
    var result = saved with { Aggregate = BenchmarkRunner.Aggregate(saved.Results) };
    var markdown = BenchmarkReportWriter.ToMarkdown(result);

    Console.WriteLine(markdown);
    if (mdPath != null) await File.WriteAllTextAsync(mdPath, markdown);
    if (jsonPath != null) await File.WriteAllTextAsync(jsonPath, BenchmarkReportWriter.ToJson(result));
}

static async Task RunBenchCompareAsync(string[] args)
{
    string? outPath = null;
    var excludeDegraded = false;
    var files = new List<string>();
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--exclude-degraded")
            excludeDegraded = true;
        else if (args[i] == "--out")
            outPath = i + 1 < args.Length ? args[++i] : throw new ArgumentException("--out requires a value.");
        else if (args[i].StartsWith("--out=", StringComparison.Ordinal))
            outPath = args[i]["--out=".Length..];
        else if (args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Unknown option '{args[i]}'.");
        else
            files.Add(args[i]);
    }

    if (files.Count != 2)
    {
        Console.WriteLine("Usage: bench-compare <a-results.json> <b-results.json> [--exclude-degraded] [--out comparison.md]");
        return;
    }
    foreach (var f in files)
        if (!File.Exists(f))
            throw new FileNotFoundException($"Results file not found: {f}");

    var a = BenchmarkComparer.Load(await File.ReadAllTextAsync(files[0]));
    var b = BenchmarkComparer.Load(await File.ReadAllTextAsync(files[1]));
    var markdown = BenchmarkComparer.ToMarkdown(Path.GetFileName(files[0]), Path.GetFileName(files[1]), a, b, BenchmarkComparer.Compare(a, b, excludeDegraded));

    Console.WriteLine(markdown);
    if (outPath != null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outPath, markdown);
        Console.WriteLine($"Comparison written to {Path.GetFullPath(outPath)}");
    }
}

static void WriteBenchOutputs(BenchmarkResult result, string jsonPath, string mdPath)
{
    foreach (var path in new[] { jsonPath, mdPath })
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
    File.WriteAllText(jsonPath, BenchmarkReportWriter.ToJson(result));
    File.WriteAllText(mdPath, BenchmarkReportWriter.ToMarkdown(result));
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
