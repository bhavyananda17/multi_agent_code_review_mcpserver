# Benchmark summary

- **Generated:** 2026-10-26 10:39:28 UTC
- **Rows:** 12 (12 scored, 0 failed)
- **Matching:** text similarity ≥ 0.250 (Dice coefficient on stop-word-filtered, stemmed, camelCase-split tokens; >= 2 shared tokens required); mode `any-match`
- **Findings scored:** reported findings only (flagged ones excluded)
- **Pipeline:** static analysis on; grounding check on; self-evaluation on (threshold 5/9)
- **Model overrides:** documentation=openai/gpt-oss-20b, logic=openai/gpt-oss-120b, modernization=openai/gpt-oss-120b, onboarding=openai/gpt-oss-20b, performance=openai/gpt-oss-120b, security=openai/gpt-oss-120b, synthesis=openai/gpt-oss-120b, triage=openai/gpt-oss-20b

| # | Repo | Commit | Findings | Ground truth | Precision | Recall | F1 | Time (s) | Tokens (k) |
|--:|------|--------|---------:|-------------:|----------:|-------:|---:|---------:|-----------:|
| 1 | dotnet/extensions | `2e537166` | 7 | 3 | 0.000 | 0.000 | 0.000 | 52.5 | - |
| 2 | dotnet/BenchmarkDotNet | `5a545385` | 9 | 3 | 0.222 | 1.000 | 0.364 | 65.6 | - |
| 3 | DapperLib/Dapper | `d56340b8` | 7 | 2 | 0.000 | 0.000 | 0.000 | 54.3 | - |
| 4 | fluentassertions/fluentassertions | `6ea70708` | 3 | 2 | 0.333 | 0.500 | 0.400 | 38.5 | - |
| 5 | dotnet/aspire | `45651ce2` | 10 | 2 | 0.100 | 0.500 | 0.167 | 64.4 | - |
| 6 | ppy/osu | `61e42030` | 4 | 2 | 0.000 | 0.000 | 0.000 | 39.6 | - |
| 7 | microsoft/kiota | `8657cefa` | 4 | 2 | 0.000 | 0.000 | 0.000 | 7.9 | - |
| 8 | nunit/nunit | `b6611848` | 4 | 2 | 0.000 | 0.000 | 0.000 | 37.8 | - |
| 9 | nunit/nunit | `8502a74d` | 5 | 3 | 0.000 | 0.000 | 0.000 | 59.3 | - |
| 10 | nunit/nunit | `62098a97` | 4 | 3 | 0.000 | 0.000 | 0.000 | 79.4 | - |
| 11 | MonoGame/MonoGame | `54c93ddc` | 5 | 2 | 0.000 | 0.000 | 0.000 | 24.2 | - |
| 12 | MonoGame/MonoGame | `04d9799f` | 2 | 2 | 0.500 | 0.500 | 0.500 | 56.6 | - |
| | **Macro average** (12 PRs) | | | | **0.096** | **0.208** | **0.119** | | |
| | **Micro (pooled)** | | 64 | 28 | **0.078** | **0.214** | **0.115** | | |

**Macro 95% bootstrap interval** (resampling the 12 PRs): precision [0.017, 0.194], recall [0.042, 0.417], F1 [0.028, 0.228]. Intervals this wide mean small differences between two runs are mostly noise; use `bench-compare` for a paired test.

## How to read this

- **Precision** = pipeline findings that match a human finding ÷ pipeline findings. **Recall** = human findings matched by a pipeline finding ÷ human findings. **F1** is their harmonic mean.
- A PR with no pipeline findings scores precision 0 (so F1 0). Failed PRs are left out of the averages, not counted as zero.
- **Macro** averages the per-PR numbers (every PR weighs the same); **micro** pools all findings first (PRs with more findings weigh more).
- Matching is lexical overlap, not understanding: it misses paraphrases and can match unrelated comments that share vocabulary. Review the `matches` and `unmatched*` lists in the JSON file before trusting a number.
- Ground truth only holds what reviewers happened to write, so valid pipeline findings they didn't mention count as false positives; treat precision as a lower bound.
