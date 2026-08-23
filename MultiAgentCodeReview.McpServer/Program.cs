using DotNetEnv;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MultiAgentCodeReview.McpServer.Logging;
using MultiAgentCodeReview.McpServer.Tools;
using MultiAgentCodeReview.Orchestration.DI;

if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var logPath = Environment.GetEnvironmentVariable("MCP_LOG_FILE") ?? "/tmp/mcp-server.log";

// Two entirely separate builder/host paths, not a unified WebApplication for both modes.
// WebApplication always starts Kestrel, and stdio transport is EXTREMELY sensitive to any
// extraneous output on stdout (it's the JSON-RPC protocol stream) — even a Kestrel startup
// banner would corrupt it. So stdio mode keeps the exact original Host.CreateApplicationBuilder
// path, byte-for-byte, and only http mode uses WebApplication/Kestrel.
var transportMode = Environment.GetEnvironmentVariable("MCP_TRANSPORT")?.ToLowerInvariant() ?? "stdio";

if (transportMode == "http")
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureCommon(builder, logPath);

    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<CodeReviewMcpTools>();

    var app = builder.Build();

    // Simple shared-secret gate (same X-Api-Key pattern already used by MultiAgentCodeReview.Api),
    // not the SDK's built-in OAuth-based McpAuthenticationHandler — that's real OAuth machinery
    // meant for connecting to OAuth-secured resources, overkill for "keep strangers off my Groq
    // quota". /health stays open for infra health checks.
    var apiKey = Environment.GetEnvironmentVariable("MCP_API_KEY");
    if (string.IsNullOrEmpty(apiKey))
    {
        app.Logger.LogWarning(
            "MCP_API_KEY environment variable is not set — the MCP endpoint is running WITHOUT " +
            "authentication (local dev mode). Set MCP_API_KEY to enforce the X-Api-Key gate.");
    }

    app.Use(async (context, next) =>
    {
        if (!string.IsNullOrEmpty(apiKey) &&
            context.Request.Path.StartsWithSegments("/mcp") &&
            !string.Equals(context.Request.Headers["X-Api-Key"].FirstOrDefault(), apiKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("unauthorized");
            return;
        }
        await next();
    });

    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    app.MapMcp("/mcp");

    Console.Error.WriteLine($"MCP server (HTTP transport) logging to: {logPath}");
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureCommon(builder, logPath);

    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<CodeReviewMcpTools>();

    Console.Error.WriteLine($"MCP server (stdio transport) logging to: {logPath}");

    var app = builder.Build();
    await app.RunAsync();
}

static void ConfigureCommon(IHostApplicationBuilder builder, string logPath)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddFileLogging(logPath);
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    builder.Configuration.AddEnvironmentVariables(prefix: "MULTIAGENT_");

    // Deliberately NOT passing builder.Configuration through — it includes UNPREFIXED
    // environment variables (both WebApplicationBuilder.Configuration and
    // HostApplicationBuilder.Configuration do this), which could let an unrelated
    // unprefixed API_KEY/BASE_URL env var silently override the Groq config. Same fix
    // already applied in MultiAgentCodeReview.Api/Program.cs — the extension's own
    // internal ConfigurationBuilder already reads MULTIAGENT_*-prefixed vars correctly.
    builder.Services.AddMultiAgentCodeReview();
}
