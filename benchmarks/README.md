# Benchmark: pipeline findings vs. human review comments

`bench` runs the full pipeline over real merged pull requests and scores its findings against the review comments human
reviewers left on them. This folder holds the dataset, the results of the one complete run so far, and the plan for
finishing the experiment. Read the **threats to validity** at the bottom before quoting any number.

## Files

| File | What it is |
|---|---|
| `real-prs.csv` | The 12 benchmark rows: repo, commit, the human comments (ground truth), PR URL |
| `select_prs.py` | How the candidates were found and the ground truth derived (needs `gh`; stdlib only; read-only) |
| `real-prs-before.json` / `.md` | The complete run on the code as it stood **before** the fixes listed below (the summary was re-rendered with `bench-summary`; the per-PR data is as saved) |

## Dataset

12 merged, squash-merged C# pull requests from 9 public repositories (`dotnet/extensions`, `BenchmarkDotNet`, `Dapper`,
`fluentassertions`, `dotnet/aspire`, `ppy/osu`, `microsoft/kiota`, `nunit/nunit` (3), `MonoGame` (2)). Each has 2-3 human
review comments, 28 in total.

- **Ground truth** is the first comment of each review thread that is on a `.cs` file, still applies to the merged code (not
  "outdated"), was written by a human other than the PR author, and is at least 40 characters after removing code blocks,
  links and @mentions.
- **Why squash-merged:** the merge commit then has one parent, so `<sha>~1..<sha>` is exactly the PR.
- **Selection:** the script found 20 candidates; the 12 used were chosen by hand because their comments are about the code
  (not process, tooling or an AI-review bot posing as a user). That choice is a source of bias.

## Method

```bash
dotnet run --project MultiAgentCodeReview.Host -- bench benchmarks/real-prs.csv --out results.json --md summary.md
dotnet run --project MultiAgentCodeReview.Host -- bench-compare before.json after.json
```

For each row the repo is cloned, the commit checked out, and the full pipeline run. A pipeline finding "matches" a human
comment when their wording overlaps (Dice similarity on stemmed tokens, at least 0.25, at least 2 shared tokens). Precision =
matching findings / findings; recall = matched comments / comments. Aggregates are macro (mean over PRs) and micro (pooled),
with a 95% bootstrap interval over PRs; `bench-compare` does a paired test between two runs.

## Results so far: one complete run, on the code *before* the fixes

Models: specialists `openai/gpt-oss-120b`, triage `openai/gpt-oss-20b`, judge `openai/gpt-oss-120b` (the same model that wrote
the findings). 12 of 12 PRs scored. Pipeline time per PR: 8-79 s (mean 48 s). Token usage was not
recorded in this run.

| # | PR | Findings | Human comments | Precision | Recall | F1 | Time (s) |
|--:|----|--------:|---------------:|----------:|-------:|---:|--------:|
| 1 | dotnet/extensions | 7 | 3 | 0.00 | 0.00 | 0.00 | 52 |
| 2 | dotnet/BenchmarkDotNet | 9 | 3 | 0.22 | 1.00 | 0.36 | 66 |
| 3 | DapperLib/Dapper | 7 | 2 | 0.00 | 0.00 | 0.00 | 54 |
| 4 | fluentassertions/fluentassertions | 3 | 2 | 0.33 | 0.50 | 0.40 | 38 |
| 5 | dotnet/aspire | 10 | 2 | 0.10 | 0.50 | 0.17 | 64 |
| 6 | ppy/osu | 4 | 2 | 0.00 | 0.00 | 0.00 | 40 |
| 7 | microsoft/kiota | 4 | 2 | 0.00 | 0.00 | 0.00 | 8 |
| 8 | nunit/nunit | 4 | 2 | 0.00 | 0.00 | 0.00 | 38 |
| 9 | nunit/nunit | 5 | 3 | 0.00 | 0.00 | 0.00 | 59 |
| 10 | nunit/nunit | 4 | 3 | 0.00 | 0.00 | 0.00 | 79 |
| 11 | MonoGame/MonoGame | 5 | 2 | 0.00 | 0.00 | 0.00 | 24 |
| 12 | MonoGame/MonoGame | 2 | 2 | 0.50 | 0.50 | 0.50 | 57 |

**Macro:** precision 0.096, recall 0.208, F1 0.119. 95% bootstrap intervals over the 12 PRs: precision [0.02, 0.19], recall [0.04, 0.42], F1 [0.03, 0.23].
**Micro:** precision 0.078 (5 of 64 findings), recall 0.214 (6 of 28 comments).

These numbers are low and, on 12 PRs, imprecise. They are not an accuracy claim. Reading the matches by hand, the matcher both
paired some unrelated comments and missed real ones; a rough manual count found about 5-7 of the 28 comments with a genuinely
corresponding finding, which is consistent with the automatic recall but not with the same rows.

### What the run showed (each point measured or reproduced, not assumed)

1. **The specialists saw a small part of most diffs.** They received the first 4,000 characters of the diff. 10 of the 12 PRs
   were larger; for those the window covered 15-48% of the diff. 3 of the 28 comments were on files that began beyond the
   window and 16 more on files cut off partway.
2. **The cut was mid-line and produced a false `Critical`.** On `nunit` PR 5442 the prompt ended at `return partitionFilter is not nul`
   and a finding reported a "misspelled identifier `nul`" as a compile error. The real code says `is not null`. Reproduced by
   rebuilding the exact prompt.
3. **The judge did not discriminate.** It scored 49 of the 64 findings and flagged none; 32 of the 49 scores were 8 or 9 out of 9,
   including the false `Critical` above (9/9). Judge and generator were the same model. The judge also returned no usable scores
   for some batches (8 of the 10 findings on one PR), probably because a reasoning model used up the 1,500 output tokens; the
   cause was not confirmed.
4. **Many findings restated Roslyn metrics.** 22 of 64 findings were about class size, method length or cyclomatic complexity
   (8 quote member counts that only the Roslyn pass could know) and none matched a human comment. They are mostly pre-existing
   debt flagged because the change touched the class. Whether the static-analysis context caused this was not isolated.
5. **Category labels were unreliable:** 47 of 64 findings were labelled `Complexity`, the converter's fallback for any category
   it did not recognise.

### Changes made because of this run

A diff window shared fairly across files and cut only at line boundaries; a deterministic grounding check that each finding's
quoted code exists in the commit; judge-independence reporting (the report says whether the judge is the same model, the same
family or a different family); an `Other` category instead of the silent `Complexity` fallback; merging of near-duplicate
findings without a line number; a larger output budget for the judge; retries for transient server errors and visible
"Incomplete review" warnings when a stage fails; token accounting; and record/replay of generations (below).

**None of these has been shown to improve the scores.** A rerun on the fixed code was started twice and could not be
completed (see below), so there is no after-measurement and no claim that the fixes helped.

## Why the rerun is not finished: the free-tier daily token limit

Groq's free tier caps `openai/gpt-oss-120b` at **200,000 tokens per day**. Probing the API directly returned HTTP 429
("tokens per day (TPD): Limit 200000, Used 197004") for every request. The earlier benchmark runs, including aborted ones, had used
the day's quota; the specialist "timeouts" seen in the aborted reruns were most likely the client waiting out these limits. Because
token usage was not recorded, the cost of a full run was not known in advance; it is now recorded per PR.

## How to finish the experiment

Record the expensive part once, then compare verification settings on identical findings for (almost) free:

```bash
# 1. Spend the generation tokens once and save what the specialists found (about one day of free-tier quota)
bench benchmarks/real-prs.csv --record benchmarks/snapshots --out benchmarks/run-record.json --md benchmarks/run-record.md

# 2. Replay: only grounding and the judge run, on the same findings. Compare judges:
bench benchmarks/real-prs.csv --replay benchmarks/snapshots --out benchmarks/replay-same-model.json
MULTIAGENT_MODEL_EVAL=openai/gpt-oss-20b  bench ... --replay benchmarks/snapshots --out benchmarks/replay-same-family.json
MULTIAGENT_MODEL_EVAL=qwen/qwen3.8-27b    bench ... --replay benchmarks/snapshots --out benchmarks/replay-independent.json

# 3. Paired comparisons (findings are identical, so any difference comes from the judge/settings)
bench-compare benchmarks/replay-same-model.json benchmarks/replay-independent.json
```

Turning static analysis off needs a second generation run, so it can only be compared across two days of quota. Use
`MULTIAGENT_AGENT_TIMEOUT_SECONDS=300` so a throttling wait is not counted as a failure, and `bench-compare --exclude-degraded`
to leave out rows where a stage failed.

## Threats to validity

- **Small and hand-picked:** 12 PRs, chosen by hand from 20 candidates; one run per condition; the pipeline is non-deterministic.
- **The matcher is lexical.** It pairs comments that share vocabulary and misses paraphrase. It has not been validated against human
  labels (the planned check: hand-label the roughly 150 finding/comment pairs and measure the matcher against them).
- **Ground truth is incomplete.** Reviewers do not write everything, so a valid finding they did not mention counts as a false
  positive; precision is a lower bound. Some comments are questions or style nits that no automated review would match.
- **Comments were written against earlier revisions.** Only threads still applicable to the merged code were kept, which reduces
  but does not remove this.
- **The default same-model judge is a known bias risk** (LLM judges tend to favour their own model's output); the independent
  judge comparison above is designed to measure it on this data.
- **Free-tier infrastructure** (rate limits, timeouts, retired models) can degrade a run; rows where it did are flagged.
