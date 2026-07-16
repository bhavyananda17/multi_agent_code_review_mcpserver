using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using MultiAgentCodeReview.Agents;
using MultiAgentCodeReview.McpServer.Tools;
using MultiAgentCodeReview.Orchestration.DI;

if (File.Exists(".env")) Env.Load(".env");
else if (File.Exists("../.env")) Env.Load("../.env");

var builder = Host.CreateApplicationBuilder(args);

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

var app = builder.Build();

await app.RunAsync();
