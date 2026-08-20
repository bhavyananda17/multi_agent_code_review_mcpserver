using System.Diagnostics;
using DotNetEnv;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Reporting;

// Same .env loading pattern as Host/McpServer: project dir first, then repo root.
if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var builder = WebApplication.CreateBuilder(args);

// NOTE: deliberately NOT passing builder.Configuration into AddMultiAgentCodeReview.
// WebApplication.CreateBuilder's configuration includes UNPREFIXED environment
// variables, so our gate variable API_KEY would otherwise be picked up by the DI
// extension as the LLM key (it reads config["API_KEY"] -> PipelineConfig.ApiKey).
// The extension's own internal ConfigurationBuilder already reads MULTIAGENT_*
// prefixed env vars, and DotNetEnv has loaded .env into the process environment.
builder.Services.AddMultiAgentCodeReview();

var app = builder.Build();

// API key gate for /api/review. When API_KEY is unset we run in local-dev mode
// (no auth) with a loud startup warning; when set, every request must send a
// matching X-Api-Key header. NOTE: this is our own gate, NOT the Groq key
// (which comes from MULTIAGENT_API_KEY / GROQ_API_KEY).
var apiKey = Environment.GetEnvironmentVariable("API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    app.Logger.LogWarning(
        "API_KEY environment variable is not set — /api/review is running WITHOUT authentication " +
        "(local dev mode). Set API_KEY to enforce the X-Api-Key gate.");
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/review", async Task<IResult> (HttpRequest http, ReviewRequest request, CodeReviewPipeline pipeline) =>
{
    if (!string.IsNullOrEmpty(apiKey) &&
        !string.Equals(http.Headers["X-Api-Key"].FirstOrDefault(), apiKey, StringComparison.Ordinal))
    {
        return Results.Text("unauthorized", statusCode: StatusCodes.Status401Unauthorized);
    }

    if (string.IsNullOrWhiteSpace(request.RepoUrl))
        return Results.BadRequest("repoUrl is required.");

    if (!request.RepoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
        !request.RepoUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
        !request.RepoUrl.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("repoUrl must be an http(s) or SSH (git@...) git URL.");
    }

    var commitHash = string.IsNullOrWhiteSpace(request.CommitHash) ? "HEAD" : request.CommitHash;
    var baseCommit = string.IsNullOrWhiteSpace(request.BaseCommit) ? null : request.BaseCommit;

    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    try
    {
        var (exitCode, gitError) = await CloneRepoAsync(request.RepoUrl, tempDir);
        if (exitCode != 0)
            return Results.BadRequest($"git clone failed: {gitError}");

        // Same pipeline entry point CodeReviewMcpTools.ReviewRepo uses.
        var output = await pipeline.RunReviewAsync(tempDir, commitHash, baseCommit);

        var wantsJson = http.Headers.Accept.Any(a =>
            a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);

        return wantsJson
            ? Results.Ok(output.Result) // existing AgentResult/Finding records, serialized as-is
            : Results.Text(ReportFormatter.FormatReport(output), "text/plain");
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Review failed for {RepoUrl}@{CommitHash}", request.RepoUrl, commitHash);
        return Results.Problem(ex.Message);
    }
    finally
    {
        TryDeleteDirectory(tempDir);
    }
});

app.Run();

// Full clone on purpose: GitOperationsTool resolves diffs via `git merge-base`,
// which needs real commit history — a shallow clone would break it.
static async Task<(int ExitCode, string Error)> CloneRepoAsync(string repoUrl, string tempDir)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    psi.ArgumentList.Add("clone");
    psi.ArgumentList.Add(repoUrl);
    psi.ArgumentList.Add(tempDir);

    using var process = Process.Start(psi)!;
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await stdoutTask;
    return (process.ExitCode, (await stderrTask).Trim());
}

// Unconditional temp cleanup; never mask the request's own result/exception.
void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to delete temp directory {TempDir}", path);
    }
}

public record ReviewRequest(string? RepoUrl, string? CommitHash, string? BaseCommit);
