# MCP Server Setup Guide

This guide explains how to configure and use the Multi-Agent Code Review MCP server with OpenCode.

## Prerequisites

- .NET 10 SDK installed
- Groq API key (set in `.env`)
- OpenCode installed

## Quick Setup

### 1. Build the MCP Server

```bash
cd /Users/bhavyananda17/Documents/coding/MultiAgentCodeReview
dotnet build MultiAgentCodeReview.McpServer
```

### 2. Configure OpenCode

Add to your `opencode.json` (project root) or `~/.config/opencode/opencode.jsonc` (global):

```json
{
  "mcp": {
    "code-review-agents": {
      "type": "local",
      "command": ["dotnet", "run", "--project", "./MultiAgentCodeReview.McpServer"],
      "enabled": true
    }
  }
}
```

### 3. Restart OpenCode

Quit and reopen OpenCode from the project directory.

## Available Tools

### 1. `review_repo`

Run a full multi-agent code review on a git repository.

**Example prompts:**
- "Review the current repo at HEAD"
- "Run a code review on commit abc123"
- "Check this PR for security issues"

**Parameters:**
| Parameter | Required | Description |
|-----------|----------|-------------|
| `repo_path` | Yes | Absolute path to the git repo |
| `commit_hash` | Yes | Commit to review (HEAD, sha, branch) |
| `base_commit` | No | Base to diff against (default: HEAD~1) |

### 2. `ask_codebase`

Ask a natural language question about the codebase.

**Example prompts:**
- "Where is authentication handled in this project?"
- "What calls the IUserRepository interface?"
- "Which files would break if I change the IAgent interface?"
- "Explain the pipeline flow"

**Parameters:**
| Parameter | Required | Description |
|-----------|----------|-------------|
| `repo_path` | Yes | Absolute path to the git repo |
| `question` | Yes | Natural language question |

### 3. `get_last_report`

Get the most recent review report without re-running the pipeline.

**Example prompts:**
- "Get the last code review report"
- "Show me the previous review results"

**Parameters:**
| Parameter | Required | Description |
|-----------|----------|-------------|
| `repo_path` | Yes | Absolute path to the git repo |

### 4. `generate_docs`

Generate project documentation for a codebase.

**Example prompts:**
- "Generate documentation for this project"
- "Create a README for this repo"
- "Update the API documentation"

**Parameters:**
| Parameter | Required | Description |
|-----------|----------|-------------|
| `repo_path` | Yes | Absolute path to the git repo |
| `commit_hash` | Yes | Commit to document (HEAD, sha, branch) |
| `base_commit` | No | Base to diff against (default: HEAD~1) |

## Testing the Server

### Manual Test (Terminal)

Start the server in one terminal:
```bash
dotnet run --project MultiAgentCodeReview.McpServer
```

The server will wait for MCP protocol messages on stdin.

### Verify Build

```bash
dotnet build MultiAgentCodeReview.McpServer
```

Should complete with 0 errors.

## Rate Limits

The default Groq API configuration:
- **Rate limit:** 1000 requests per day
- **Agents per review:** 8 (Triage + 4 Specialists + Synthesis + Documentation + Onboarding)
- **Reviews per day:** ~125 (1000 / 8)

To increase limits, upgrade your Groq API plan or configure per-role model overrides.

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `MULTIAGENT_GROQ_API_KEY` | **Required** Groq API key | — |
| `MULTIAGENT_GROQ_BASE_URL` | Groq API endpoint | `https://api.groq.com/openai` |
| `MULTIAGENT_MODEL_TRIAGE` | Override triage model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_SECURITY` | Override security model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_LOGIC` | Override logic model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_PERFORMANCE` | Override performance model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_MODERNIZATION` | Override modernization model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_SYNTHESIS` | Override synthesis model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_DOCUMENTATION` | Override documentation model | `llama-3.3-70b-versatile` |
| `MULTIAGENT_MODEL_ONBOARDING` | Override onboarding model | `llama-3.1-8b-instant` |

## Troubleshooting

### Server doesn't start

1. Check `.env` file exists with `GROQ_API_KEY` set
2. Run `dotnet build MultiAgentCodeReview.McpServer` to verify build
3. Check OpenCode logs for MCP server errors

### Tools don't appear in OpenCode

1. Verify `opencode.json` has the correct MCP config
2. Restart OpenCode (quit and reopen)
3. Check that the `command` path is correct

### "No report found" error

Run `review_repo` first to generate a report, then `get_last_report` will return it.

## Architecture

```
OpenCode (MCP Client)
    ↓ stdio
McpServer (ModelContextProtocol)
    ↓ DI
CodeReviewPipeline (Orchestration)
    ↓
8 AutoGen Agents (via AgentFactory)
    ↓
Groq API (Llama 3.3)
```
