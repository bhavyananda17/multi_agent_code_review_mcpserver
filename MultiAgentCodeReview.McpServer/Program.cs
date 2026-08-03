using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.McpServer.Logging;
using MultiAgentCodeReview.McpServer.Tools;
using MultiAgentCodeReview.Orchestration.DI;

if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var logPath = Environment.GetEnvironmentVariable("MCP_LOG_FILE") ?? "/tmp/mcp-server.log";

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddFileLogging(logPath);
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Configuration.AddEnvironmentVariables(prefix: "MULTIAGENT_");

builder.Services.AddMultiAgentCodeReview(configuration: builder.Configuration);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CodeReviewMcpTools>();

Console.Error.WriteLine($"MCP server logging to: {logPath}");

var app = builder.Build();

await app.RunAsync();
