# Benchmark summary

- **Generated:** 2026-10-26 16:37:01 UTC
- **Rows:** 12 (12 scored, 0 failed)
- **Matching:** text similarity ≥ 0.250 (Dice coefficient on stop-word-filtered, stemmed, camelCase-split tokens; >= 2 shared tokens required); mode `any-match`
- **Findings scored:** reported findings only (flagged ones excluded)
- **Pipeline:** static analysis on; grounding check on; self-evaluation on (threshold 5/9); diff window 6000 characters; agent timeout 300s per attempt
- **Mode:** record - generations were saved so verification settings can later be replayed on identical findings
- **Tokens:** 1,51,562 in total, about 12,630 per scored PR (counted from each response's own usage field)
- **Judge:** `qwen/qwen3.8-27b` (independent family)
- **Model overrides:** documentation=openai/gpt-oss-20b, eval=qwen/qwen3.8-27b, logic=openai/gpt-oss-120b, modernization=openai/gpt-oss-120b, onboarding=openai/gpt-oss-20b, performance=openai/gpt-oss-120b, security=openai/gpt-oss-120b, synthesis=openai/gpt-oss-120b, triage=openai/gpt-oss-20b

| # | Repo | Commit | Findings | Ground truth | Precision | Recall | F1 | Time (s) | Tokens (k) |
|--:|------|--------|---------:|-------------:|----------:|-------:|---:|---------:|-----------:|
| 1 | dotnet/extensions | `2e537166` | 4 | 3 | 0.000 | 0.000 | 0.000 | 38.2 | 17.0 |
| 2 | dotnet/BenchmarkDotNet | `5a545385` | 4 | 3 | 0.250 | 0.333 | 0.286 | 41.7 | 15.4 |
| 3 | DapperLib/Dapper | `d56340b8` | 1 | 2 | 0.000 | 0.000 | 0.000 | 53.5 | 13.7 |
| 4 | fluentassertions/fluentassertions | `6ea70708` | 2 | 2 | 0.000 | 0.000 | 0.000 | 37.9 | 9.5 |
| 5 | dotnet/aspire | `45651ce2` | 3 | 2 | 0.000 | 0.000 | 0.000 | 38.1 | 15.3 |
| 6 | ppy/osu | `61e42030` | 6 | 2 | 0.000 | 0.000 | 0.000 | 41.5 | 16.9 |
| 7 | microsoft/kiota | `8657cefa` | 1 | 2 | 0.000 | 0.000 | 0.000 | 21.9 | 8.9 |
| 8 | nunit/nunit | `b6611848` | 5 | 2 | 0.000 | 0.000 | 0.000 | 5.9 | 7.6 |
| 9 | nunit/nunit | `8502a74d` | 4 | 3 | 0.000 | 0.000 | 0.000 | 57.6 | 11.9 |
| 10 | nunit/nunit | `62098a97` | 7 | 3 | 0.286 | 0.667 | 0.400 | 83.6 | 14.4 |
| 11 | MonoGame/MonoGame | `54c93ddc` | 1 | 2 | 0.000 | 0.000 | 0.000 | 22.1 | 10.6 |
| 12 | MonoGame/MonoGame | `04d9799f` | 3 | 2 | 0.000 | 0.000 | 0.000 | 46.8 | 10.3 |
| | **Macro average** (12 PRs) | | | | **0.045** | **0.083** | **0.057** | | |
| | **Micro (pooled)** | | 41 | 28 | **0.073** | **0.107** | **0.087** | | |

**Macro 95% bootstrap interval** (resampling the 12 PRs): precision [0.000, 0.110], recall [0.000, 0.222], F1 [0.000, 0.138]. Intervals this wide mean small differences between two runs are mostly noise; use `bench-compare` for a paired test.

## How to read this

- **Precision** = pipeline findings that match a human finding ÷ pipeline findings. **Recall** = human findings matched by a pipeline finding ÷ human findings. **F1** is their harmonic mean.
- A PR with no pipeline findings scores precision 0 (so F1 0). Failed PRs are left out of the averages, not counted as zero.
- **Macro** averages the per-PR numbers (every PR weighs the same); **micro** pools all findings first (PRs with more findings weigh more).
- Matching is lexical overlap, not understanding: it misses paraphrases and can match unrelated comments that share vocabulary. Review the `matches` and `unmatched*` lists in the JSON file before trusting a number.
- Ground truth only holds what reviewers happened to write, so valid pipeline findings they didn't mention count as false positives; treat precision as a lower bound.
