# MCP server setup

`MultiAgentCodeReview.McpServer` exposes the review pipeline as three MCP tools. It speaks stdio by default (the client
spawns it) or HTTP when `MCP_TRANSPORT=http`.

## Prerequisites

- .NET 10 SDK
- A Groq API key in `.env` (`GROQ_API_KEY`, see `.env.example`)

## Register it with a client (stdio)

Build once, then point your MCP client at the project. For OpenCode, add this to `opencode.json`:

```json
{
  "mcp": {
    "code-review-agents": {
      "type": "local",
      "command": ["dotnet", "run", "--project", "/path/to/MultiAgentCodeReview/MultiAgentCodeReview.McpServer"],
      "enabled": true
    }
  }
}
```

Other clients take the same command in their own format (`dotnet run --project <path>`). The server logs to
`MCP_LOG_FILE` (default `/tmp/mcp-server.log`), because stdout is reserved for the protocol.

## Tools

| Tool | Arguments | Returns |
|------|-----------|---------|
| `review_repo` | `repo_path`, `commit_hash`, optional `base_commit` (default `commit_hash~1`), optional `issue` (GitHub URL or number) | Markdown report, plus a modernization quick-scan |
| `ask_codebase` | `repo_path`, `question` | Answer grounded in the Roslyn analysis and a review of the repo |
| `generate_docs` | `repo_path`, `commit_hash`, optional `base_commit` | Generated documentation (also saved under `~/Documents/CodeReviewReports`) |

`repo_path` is a local path, or a git URL when the server runs somewhere that does not share your filesystem.

## HTTP transport

```bash
MCP_TRANSPORT=http MCP_API_KEY=<secret> dotnet run --project MultiAgentCodeReview.McpServer
```

Clients send the secret in an `X-Api-Key` header. Without `MCP_API_KEY` the endpoint runs unauthenticated, which is only
suitable for local development. See the Deployment section of the README for the AWS setup.

## Configuration

All settings are environment variables; the model per role and the verification stages are listed in the README's
Configuration section and in `.env.example`. A role pointing at a model Groq has retired fails with a 404, so pin the
models you rely on with `MULTIAGENT_MODEL_<ROLE>`.

## Rate limits

Groq's free tier caps requests and tokens per minute and per day. A review makes one triage call, up to three specialist
calls and one judge call per eight findings. The specialists are started 15 seconds apart and retried with backoff on
429s; see the README's Performance section.

## Troubleshooting

- **Server does not start:** `dotnet build MultiAgentCodeReview.McpServer`, confirm `GROQ_API_KEY` is set, and read
  `MCP_LOG_FILE`.
- **Tools do not appear:** check the client's MCP config points at the right path and restart the client.
