# Multi-Agent Code Review System

A multi-agent code review system built with Microsoft AutoGen and Groq's Llama models. Automated code review pipeline with specialized agents for security, logic, performance, and modernization analysis.

## Architecture

```
Host (CLI) → Orchestration (Pipeline) → Agents (AutoGen) → Groq API (Llama 3.1/3.3)
```

### Projects

| Project | Purpose |
|---------|---------|
| `MultiAgentCodeReview.Core` | Domain models, interfaces, config, prompts, rate limiting |
| `MultiAgentCodeReview.Agents` | 8 AutoGen agents (Triage, 4 Specialists, Synthesis, Docs, Onboarding) |
| `MultiAgentCodeReview.Orchestration` | DI container, pipeline orchestrator, Roslyn/Git tools |
| `MultiAgentCodeReview.Host` | Console entry point |

## Agents

| Agent | Role | Model (default) |
|-------|------|-----------------|
| **Triage** | Classifies changes, routes to specialists | llama-3.3-70b-versatile |
| **Security** | SQLi, XSS, auth bypass, crypto, secrets | llama-3.3-70b-versatile |
| **Logic** | Logic errors, complexity, SOLID, naming | llama-3.3-70b-versatile |
| **Performance** | N+1, blocking calls, memory, O(n²), caching | llama-3.3-70b-versatile |
| **Modernization** | Legacy patterns, outdated frameworks, missing C# features | llama-3.3-70b-versatile |
| **Synthesis** | Deduplicates, prioritizes, sequences fixes | llama-3.3-70b-versatile |
| **Documentation** | Generates README, API docs, Architecture | llama-3.3-70b-versatile |
| **Onboarding** | Answers developer questions from codebase context | llama-3.3-70b-versatile |

## Quick Start

```bash
# 1. Clone and build
git clone https://github.com/bhavyananda17/multi_agent_code_reviewsystem-hclfinalproject-.git
cd multi_agent_code_reviewsystem-hclfinalproject-
dotnet build

# 2. Configure
cp .env.example .env
# Edit .env and add your GROQ_API_KEY

# 3. Run
dotnet run --project MultiAgentCodeReview.Host -- <repo-path> [base-commit]
```

## Configuration

All settings via environment variables (prefix `MULTIAGENT_`):

| Variable | Description | Default |
|----------|-------------|---------|
| `GROQ_API_KEY` | **Required** Groq API key | — |
| `GROQ_BASE_URL` | Groq OpenAI-compatible endpoint | `https://api.groq.com/openai` |
| `MODEL_<ROLE>` | Override model per role (e.g., `MODEL_SECURITY`) | `llama-3.3-70b-versatile` |
| `MODEL_<ROLE>_TEMP` | Temperature override | Role-specific |
| `MODEL_<ROLE>_TOKENS` | Max tokens override | Role-specific |

Example `.env`:
```bash
GROQ_API_KEY=gsk_xxx
GROQ_BASE_URL=https://api.groq.com/openai
MODEL_TRIAGE=llama-3.1-8b-instant
MODEL_SYNTHESIS=llama-3.3-70b-versatile
```

## Pipeline Stages

1. **Filter** — Git diff + dependency graph → relevant files
2. **Triage** — Classify changes, route to specialists
3. **Specialists** — Run routed agents in parallel (sequential for rate limits)
4. **Synthesis** — Merge findings, deduplicate, prioritize
5. **Documentation** — Generate project docs (optional)

## Status

**First working draft** — Core pipeline functional. Known gaps:
- Rate limiting infrastructure built but not fully wired
- Onboarding agent CLI command stubbed
- Output formatting uses LLM prompt instead of `OutputConfig`
- RAG/knowledge search interfaces defined but unimplemented
- No tests yet

## License

MIT