# Multi-Agent Code Review System

> **Intelligent, automated code review powered by multi-agent AI**

A production-grade multi-agent code review system built with Microsoft AutoGen and Groq's Llama models. Analyzes C# and Python projects with specialized agents for security, performance, and modernization analysis. Exposed as an **MCP server** for seamless integration with OpenCode, VS Code, Claude Desktop, and other MCP clients.

**Current Status:** ✅ Core pipeline fully functional | 🚧 Python/Ruff integration (in progress) | 🔮 RAG search, enhanced rate limiting, comprehensive tests (planned)

---

## Table of Contents

1. [How It Works](#how-it-works)
2. [Current Architecture](#current-architecture)
3. [Python/Ruff Integration](#pythonruff-integration)
4. [The 4 MCP Tools](#the-4-mcp-tools)
5. [Pipeline Stages](#pipeline-stages)
6. [Setup & Configuration](#setup--configuration)
7. [Usage Examples](#usage-examples)
8. [Performance Metrics](#performance-metrics)
9. [Status & Future Work](#status--future-work)

---

## How It Works

```
User Command: dotnet run -- review /repo HEAD
        ↓
Stage 1: FILTER (Git diff + Roslyn)
  ├─ Identify changed files
  ├─ Build dependency graph
  └─ Select top 30 relevant files
        ↓
Stage 2: TRIAGE (8B LLM, 2-3s)
  ├─ Analyze: what type of changes?
  └─ Route: which agents should review?
        ↓
Stage 3: PARALLEL SPECIALISTS (70B LLM, 5-8s)
  ├─ SecurityAgent  → find vulnerabilities
  ├─ PerformanceAgent → find bottlenecks
  └─ ModernizationAgent → find tech debt
        ↓
Stage 4: SYNTHESIS (C# Dedup, <1ms)
  ├─ Group findings by file+line
  ├─ Boost if 2+ agents agree
  └─ Sort by severity
        ↓
Stage 5: PYTHON ANALYSIS [IF .py files exist]
  ├─ Auto-detect Python files
  ├─ Call Python/Ruff Service
  └─ Merge Python findings
        ↓
OUTPUT: Markdown report with all findings
```

---

## Current Architecture

### The 5 Projects

| Project | Purpose |
|---------|---------|
| **Core** | Models, interfaces, prompts, config |
| **Agents** | AutoGen agent implementations (Triage, 3 Specialists, Docs, Onboarding) |
| **Orchestration** | Pipeline logic, DI, Git/Roslyn tools |
| **Host** | CLI entry point (commands: review, ask, docs) |
| **McpServer** | MCP server for OpenCode/Claude integration (4 tools) |

### Data Flow

```
┌─────────────────────────────────────────────────────┐
│  CLI / MCP Client                                   │
│  (User runs: dotnet run -- review /repo HEAD)      │
└──────────────┬──────────────────────────────────────┘
               │
               ↓
┌──────────────────────────────────┐
│  CodeReviewPipeline              │
├──────────────────────────────────┤
│ 1. FilterStage                   │
│ 2. TriageAgent (8B)              │
│ 3. Parallel Specialists (70B)    │
│ 4. C# Synthesis (Dedup)          │
│ 5. Python/Ruff Detection         │
└──────────────┬───────────────────┘
               │
               ↓
┌──────────────────────────────────┐
│  Groq API                        │
│  (Llama 3.1-8B and 3.3-70B)      │
└──────────────────────────────────┘

Plus (if Python files exist):
               ↓
┌──────────────────────────────────┐
│  Python/Ruff Service             │
│  (Separate process, local)       │
└──────────────────────────────────┘

Final output:
               ↓
            Report
      (markdown + JSON)
```

### The 6 Agents

| Agent | Role | Model | Speed |
|-------|------|-------|-------|
| Triage | Route to specialists | 8B | 2-3s |
| **Security** | Find vulnerabilities (SQLi, XSS, secrets, crypto) | 70B | 2-3s |
| **Performance** | Find bottlenecks (N+1, blocking, memory, O(n²)) | 70B | 2-3s |
| **Modernization** | Find tech debt (SOLID, legacy, complexity) | 70B | 2-3s |
| Documentation | Generate README | 8B | 4-5s |
| Onboarding | Answer Q&A | 8B | 3-4s |

---

## Python/Ruff Integration

### Architecture Mind Map

```
review_repo() called with mixed repo
        ↓
CodeReviewPipeline.RunReviewAsync()
        ├─ Stage 1: FilterStage
        │   ├─ Git diff: identify all changed files
        │   ├─ Categorize files
        │   │   ├─ C# files (.cs, .csproj) → Go to C# pipeline
        │   │   └─ Python files (.py) → Mark for Ruff
        │   └─ Build Roslyn dependency graph (C# only)
        │
        ├─ Stage 2-4: C# Pipeline
        │   ├─ Triage (8B)
        │   ├─ Parallel Specialists (70B)
        │   └─ Synthesis (Dedup)
        │   └─ OUTPUT: C# findings
        │
        └─ [NEW] Python Analysis
            ├─ Check: Any .py files in ChangedFiles?
            ├─ YES → Call Python/Ruff Service
            │   ├─ Service receives: repo_path, list of .py files
            │   ├─ Service runs: ruff check --output-format json
            │   ├─ Service parses: JSON → Finding objects
            │   └─ Service returns: List<Finding>
            └─ Merge Results
                ├─ C# findings (from agents)
                ├─ Python findings (from Ruff)
                └─ Return combined list
```

### How It Works: Concrete Example

**Input:** Mixed repository
- 15 C# files (Controllers, Services, Models)
- 8 Python files (API utilities, ML helpers)
- 4 config files

**Processing:**

```
FILTER STAGE:
  Identified: 12 relevant C# files + 8 Python files

TRIAGE STAGE (8B):
  Decision: "This has security + performance issues"
  Route to: SecurityAgent, PerformanceAgent

C# ANALYSIS (Parallel):
  SecurityAgent → Found: SQL injection (1 CRITICAL)
  PerformanceAgent → Found: N+1 query (2 HIGH)
  Result: 3 C# findings

[Python Detection]
  CheckedFiles: Found 8 .py files
  Action: Call Python/Ruff Service

PYTHON ANALYSIS (Ruff):
  Ruff runs locally: ruff check *.py --output-format json
  Found: 4 Python issues
    - Unused import (1 MEDIUM)
    - Line too long (2 LOW)
    - Security pattern (1 MEDIUM)
  Result: 4 Python findings

MERGE:
  Combined list: 7 total findings (3 C# + 4 Python)
  Sorted by severity
  Return final report

OUTPUT:
  [CRITICAL] SQLi in UserController.cs:42 (C#)
  [HIGH] N+1 in OrderService.cs:78 (C#)
  [HIGH] N+1 in PaymentService.cs:120 (C#)
  [MEDIUM] Unused import in api.py:5 (Python)
  [MEDIUM] Security pattern in utils.py:42 (Python)
  [LOW] Line too long in config.py:156 (Python)
  [LOW] Line too long in helpers.py:89 (Python)
```

### Integration Points

**Inside `CodeReviewPipeline.cs` (after synthesis, before return):**

```csharp
// After C# synthesis completes...

if (context.ChangedFiles.Any(f => f.Path.EndsWith(".py")))
{
    var pythonFiles = context.ChangedFiles
        .Where(f => f.Path.EndsWith(".py"))
        .Select(f => f.Path)
        .ToList();
    
    var pythonFindings = await _pythonRuffClient.AnalyzeAsync(
        context.RepositoryPath,
        pythonFiles
    );
    
    finalResult.Findings.AddRange(pythonFindings);
}

return new ReviewOutput(context, finalResult);
```

**No changes to:**
- TriageAgent, SecurityAgent, PerformanceAgent, ModernizationAgent (untouched)
- MCP tool signatures (still the same 4 tools)
- Groq API integration (still only for C# analysis)
- DI setup (minimal new registration)

### Python/Ruff Service (Separate)

**Location:** External process/service (can be Python, Node, Go, etc.)

**API:**
```http
POST http://localhost:5000/analyze-python
Content-Type: application/json

Request:
{
  "repo_path": "/absolute/path/to/repo",
  "python_files": ["src/api.py", "tests/test_api.py"]
}

Response:
{
  "findings": [
    {
      "file": "src/api.py",
      "line": 42,
      "severity": "HIGH",
      "category": "security_pattern",
      "message": "Potential SQL injection",
      "rule_code": "S610"
    }
  ]
}
```

---

## The 4 MCP Tools

These are the tools available through the MCP server:

### 1. `review_repo` — Full Code Review

```
Input:  repo_path (string)
        commit_hash (string)
        base_commit (string, optional)

Output: Markdown report with all findings sorted by severity
```

**What it does:**
- Runs all pipeline stages
- Auto-detects project type (C#, Python, mixed)
- Calls appropriate agents
- Merges findings
- Returns prioritized report

**Use when:** "Review this commit", "Check this PR"

### 2. `ask_codebase` — Natural Language Q&A

```
Input:  repo_path (string)
        question (string)

Output: AI-powered answer grounded in code analysis
```

**Use when:** "Where is authentication handled?", "What calls IUserRepository?"

### 3. `get_last_report` — Cached Results

```
Input:  repo_path (string)

Output: Last report for this repo (instant, no re-run)
```

**Use when:** "Show me the previous review"

### 4. `generate_docs` — Auto Documentation

```
Input:  repo_path (string)
        commit_hash (string)
        base_commit (string, optional)

Output: Comprehensive README, API docs, architecture guide
```

**Use when:** "Generate documentation"

---

## Pipeline Stages

### Stage 1: Filter

- Runs `git diff`
- Filters by source file extensions (.cs, .py, .csproj, etc.)
- Builds Roslyn dependency graph
- Selects top 30 most relevant files

### Stage 2: Triage (8B LLM)

- Sees: Changed files with line counts
- Decides: Which agents should review? (Security, Performance, Modernization)
- Returns: `["SECURITY", "PERFORMANCE"]` (example)
- Speed: 2-3 seconds

### Stage 3: Specialist Agents (70B LLM)

**All 3 run in parallel via `Task.WhenAll`:**

- **SecurityAgent:** SQLi, XSS, hardcoded secrets, weak crypto, auth bypass
- **PerformanceAgent:** N+1 queries, blocking async calls, memory leaks, O(n²)
- **ModernizationAgent:** SOLID violations, legacy patterns, complexity, tech debt

**Speed:** 5-8 seconds total (faster than sequential)

### Stage 4: Synthesis (C# Dedup)

- Groups findings by file + line
- If 2+ agents flag same line → boost to CRITICAL
- Sorts by severity
- No LLM calls (pure C# logic)

### Stage 5: Python/Ruff (If .py Files Exist)

- Auto-detects .py files
- Calls Ruff service
- Merges Python findings into final list

---

## Setup & Configuration

### Quick Start (5 minutes)

```bash
# Clone
git clone https://github.com/bhavyananda17/MultiAgentCodeReview.git
cd MultiAgentCodeReview

# Build
dotnet build

# Configure
cp .env.example .env
# Edit .env, add your GROQ_API_KEY

# Test
dotnet run --project MultiAgentCodeReview.Host -- review . HEAD

# Done!
```

### Environment Variables

```bash
# REQUIRED
GROQ_API_KEY=gsk_your_key_here

# OPTIONAL (these have sensible defaults)
GROQ_BASE_URL=https://api.groq.com/openai
MODEL_TRIAGE=llama-3.1-8b-instant
MODEL_SECURITY=llama-3.3-70b-versatile
MODEL_PERFORMANCE=llama-3.3-70b-versatile
MODEL_MODERNIZATION=llama-3.3-70b-versatile
```

---

## Usage Examples

### CLI

```bash
# Review current commit
dotnet run --project MultiAgentCodeReview.Host -- review /path/to/repo HEAD

# Review against specific base
dotnet run --project MultiAgentCodeReview.Host -- review /path/to/repo HEAD origin/main

# Ask a question
dotnet run --project MultiAgentCodeReview.Host -- ask /path/to/repo HEAD "Where is auth handled?"

# Generate docs
dotnet run --project MultiAgentCodeReview.Host -- docs /path/to/repo HEAD
```

### MCP Server (OpenCode)

```bash
# Build the server
dotnet build MultiAgentCodeReview.McpServer

# Add to opencode.json:
{
  "mcp": {
    "code-review-agents": {
      "type": "local",
      "command": ["dotnet", "run", "--project", "./MultiAgentCodeReview.McpServer"]
    }
  }
}

# In OpenCode chat, use the tools:
@code-review-agents Review /path/to/repo at HEAD
```

---

## Performance Metrics

### Timing (C# Projects)

| Stage | Time |
|-------|------|
| Filter | 1-2s |
| Triage (8B) | 2-3s |
| Specialists (parallel) | 5-8s |
| Synthesis | <1ms |
| **Total** | **8-14 seconds** |

### With Python/Ruff (Mixed Projects)

| Stage | Time |
|-------|------|
| C# Pipeline | 8-14s |
| Python Detection | <0.5s |
| Ruff Analysis | 1-3s |
| Merge | <1ms |
| **Total** | **9-18 seconds** |

### API Costs

- **Per review:** ~$0.0007 (4 Groq API calls)
- **1000 reviews:** ~$0.70
- **Note:** Ruff is local, no API costs

---

## Status & Future Work

### What Works Now ✅

✅ Full C# code review pipeline
✅ 4 MCP tools (review_repo, ask_codebase, get_last_report, generate_docs)
✅ CLI commands (review, ask, docs)
✅ Parallel agent execution (3x faster than sequential)
✅ Cross-agent deduplication (boosts important issues)
✅ Caching (in-memory + disk)
✅ Line number injection (LLMs know exact file:line)
✅ Groq API integration
✅ MCP stdio transport (OpenCode/Claude ready)

### In Progress 🚧

🚧 Python/Ruff integration
  - Auto-detection of .py files (~30% done)
  - Service endpoint (~20% done)
  - Finding conversion (~10% done)
  - Testing (not started)

### Planned 🔮

🔮 **RAG (Retrieval Augmented Generation)** — Context-aware answers from docs
🔮 **Full Rate Limiting** — Token budget enforcement across all agents
🔮 **Comprehensive Tests** — Unit + integration test coverage
🔮 **Web Dashboard** — Browse reports, filter issues, track trends
🔮 **CI/CD Integration** — GitHub Actions, GitLab CI plugins
🔮 **Custom Rules** — Organization-specific security/performance rules

---

## Architecture Decisions

### Why 8B LLM for Triage?

Routing is simpler than analysis. 8B model is fast (2-3s) and cheap. 70B reserved for deep analysis.

### Why Parallel Specialists?

Sequential (15-20s) vs Parallel (5-8s) = 3x faster. Uses 3x quota simultaneously, but worth it.

### Why C# Dedup Instead of LLM?

LLM synthesis: 5-8s, expensive. C# dedup: <1ms, free. Deterministic and reliable.

### Why Separate Python/Ruff Service?

Decoupled = independent scaling, upgrading, disabling. No Python dependency on C# machines.

---

## License

MIT

---

**Questions or Issues?**
- GitHub: https://github.com/bhavyananda17/MultiAgentCodeReview
- Email: bhavyananda17@email.com
- Docs: See MCP_SETUP.md for MCP configuration

**Version:** 1.0 (Core pipeline functional, Python/Ruff integration in progress)
**Last Updated:** July 2026
