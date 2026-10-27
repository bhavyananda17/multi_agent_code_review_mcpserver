# Benchmark: pipeline findings vs. human review comments

`bench` runs the full pipeline over real merged pull requests and scores its findings against the review comments human
reviewers left on them. This folder holds the dataset, the results of the runs so far, and what they do and do not show.
**The short version: on these 12 PRs the pipeline's findings rarely coincide with what the reviewers wrote, the measurement
itself is rough, and the most useful output of the exercise is the list of failure modes below, not a score.** Read the
**threats to validity** at the bottom before quoting any number.

## Files

| File | What it is |
|---|---|
| `real-prs.csv` | The 12 benchmark rows: repo, commit, the human comments (ground truth), PR URL |
| `select_prs.py` | How the candidates were found and the ground truth derived (needs `gh`; stdlib only; read-only) |
| `real-prs-before.json` / `.md` | Run 1: the code as it stood **before** the fixes listed below |
| `run-record.json` / `.md` | Run 2: the fixed code, with generations recorded (judge: `qwen/qwen3.8-27b`) |
| `snapshots/` | What the specialists found in run 2, one file per PR, so verification can be replayed without spending generation tokens |
| `replay-same-model.*`, `replay-same-family.*`, `replay-independent.*` | Run 2's findings re-verified with three different judges |
| `replay-independent-rerun.*` | The independent judge run a second time (9 of 12 rows scored; 3 clone failures) |

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

## Run 1: before the fixes

Models: specialists `openai/gpt-oss-120b`, triage `openai/gpt-oss-20b`, judge `openai/gpt-oss-120b` (the same model that wrote
the findings). 12 of 12 PRs scored. Pipeline time per PR: 8-79 s (mean 48 s). Token usage was not recorded in this run.

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

### What run 1 showed (each point measured or reproduced, not assumed)

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
   (8 quote member counts that only the Roslyn pass could know) and none matched a human comment.
5. **Category labels were unreliable:** 47 of 64 findings were labelled `Complexity`, the converter's fallback for any category
   it did not recognise.

### Changes made because of run 1

A diff window shared fairly across files and cut only at line boundaries; a deterministic grounding check that each finding's
quoted code exists in the commit; judge-independence reporting (the report says whether the judge is the same model, the same
family or a different family); an `Other` category instead of the silent `Complexity` fallback; merging of near-duplicate
findings without a line number; a larger output budget for the judge; retries for transient server errors and visible
"Incomplete review" warnings when a stage fails; token accounting; and record/replay of generations.

## Run 2: after the fixes

Same 12 PRs, specialists `openai/gpt-oss-120b`, triage `openai/gpt-oss-20b`, judge `qwen/qwen3.8-27b` (a different family).
12 of 12 scored, none degraded. 151,562 tokens in total (about 12.6k per PR); 6-84 s per PR (mean about 41 s).

| # | PR | Findings | Human comments | Precision | Recall | F1 | Tokens (k) |
|--:|----|--------:|---------------:|----------:|-------:|---:|-----------:|
| 1 | dotnet/extensions | 4 | 3 | 0.00 | 0.00 | 0.00 | 17.0 |
| 2 | dotnet/BenchmarkDotNet | 4 | 3 | 0.25 | 0.33 | 0.29 | 15.4 |
| 3 | DapperLib/Dapper | 1 | 2 | 0.00 | 0.00 | 0.00 | 13.7 |
| 4 | fluentassertions/fluentassertions | 2 | 2 | 0.00 | 0.00 | 0.00 | 9.5 |
| 5 | dotnet/aspire | 3 | 2 | 0.00 | 0.00 | 0.00 | 15.3 |
| 6 | ppy/osu | 6 | 2 | 0.00 | 0.00 | 0.00 | 16.9 |
| 7 | microsoft/kiota | 1 | 2 | 0.00 | 0.00 | 0.00 | 8.9 |
| 8 | nunit/nunit | 5 | 2 | 0.00 | 0.00 | 0.00 | 7.6 |
| 9 | nunit/nunit | 4 | 3 | 0.00 | 0.00 | 0.00 | 11.9 |
| 10 | nunit/nunit | 7 | 3 | 0.29 | 0.67 | 0.40 | 14.4 |
| 11 | MonoGame/MonoGame | 1 | 2 | 0.00 | 0.00 | 0.00 | 10.6 |
| 12 | MonoGame/MonoGame | 3 | 2 | 0.00 | 0.00 | 0.00 | 10.3 |

**Macro:** precision 0.045, recall 0.083, F1 0.057. 95% bootstrap intervals: precision [0.00, 0.11], recall [0.00, 0.22], F1 [0.00, 0.14].
**Micro:** precision 0.073 (3 of 41 findings), recall 0.107 (3 of 28 comments).

**Run 2 is not comparable to run 1 and shows no improvement.** The numbers are lower, but the runs differ in the code, the
judge model, the diff window and the findings reported after verification, and each is a single run of a non-deterministic
pipeline. The most that can be said is that the fixes did not produce a visible gain on this benchmark.

### Verification stages on run 2's findings

The pipeline produced 65 findings across the 12 PRs before verification.

- **Grounding flagged 22 of 65 (34%)** as quoting code that is not in the commit. This is deterministic, so it is identical
  in every replay. On PR 1 I checked the three flagged findings against the real file by hand: all three quote code that does
  not exist (one is a placeholder comment, `/* complex switch logic */`).
- **Judges.** The same 65 findings (snapshots) re-verified with three judges:

| Judge | Relation to generators | Flagged low-confidence by the judge | Findings reported | Macro F1 |
|---|---|---:|---:|---:|
| `openai/gpt-oss-120b` | same model | 1 | 43 | 0.057 |
| `openai/gpt-oss-20b` | same family | 0 | 43 | 0.057 |
| `qwen/qwen3.8-27b` | different family | 6 | 40 | 0.061 |

`bench-compare` (same-model vs independent): F1 +0.004, interval [+0.000, +0.011], sign-flip p = 1.0.
Judge noise: the independent judge was run a second time on the same findings (`replay-independent-rerun.*`). Three of its 12 rows
failed on network errors while cloning (`git clone` disconnects), so only 9 PRs were scored. On those 9, the same judge on the
same findings differed by F1 -0.005 (interval [-0.015, +0.000]), the same size as the +0.004 between judges. That
is, the between-judge difference in this benchmark is no larger than a judge disagreeing with itself. **The judge choice does
not change the benchmark score.** What differs is how many findings each judge flags: the same-model and same-family judges
flagged 1 and 0 of 65, the independent judge 6. That is consistent with the known tendency of LLM judges to favour their own
family's output, but it is one small run with one generator setup, not a measurement of that effect.

### What the misses look like (read by hand)

I read every unmatched finding/comment pair in run 2. One reader's judgement, not validated labels.

- **Mostly the two lists are about different things.** Human comments are mostly questions and design nits ("why is this
  static?", "can this be an enum?", "these overloads differ only by a parameter"). The pipeline mostly produced speculative
  security, null-check and "class has N members" findings.
- **The matcher is wrong in both directions.** The one PR with a plausible hit, PR 2, is correct ("duplicate paths are not
  detected" against "duplicate paths should be rejected"). Both matches on PR 10, which produce its F1 of 0.40, are false:
  they share vocabulary ("run settings", "null") but are about different things. On PR 8 a real match is missed: the finding
  "multiple `Assert.That` overloads contain identical logic" and the comment "these methods differ only by a parameter" are the
  same point and score 0. Two more (PRs 7 and 12) are related but not the same point. By this reading the true number of
  matches is nearer 2 than 3, and PR 10 is about 0.
- **A false `Critical` passed every check.** On PR 10 the pipeline reported twice, at `Critical`, that `runSettings` "is not
  defined" and causes a compile error. At that commit it is a parameter of both `RunTests` (line 271) and `ReportResults`
  (line 295) in `TextRunner.cs`. Grounding passed it (the quoted line exists) and the judge scored it 9/9 in all three judge
  runs. Grounding checks that the quote exists, not that the claim is true; a likely cause is that the diff window shows the
  call but not the signature of the enclosing method.
- **Row 8's 5.9 s is not a bug:** triage routed that PR to one specialist, so there was no stagger.

## What this does and does not establish

- It shows the harness works end to end on real repositories, and which failure modes the pipeline has (above).
- It does not show how good the pipeline is: 12 hand-picked PRs, a lexical matcher with known errors, and human comments that
  are an incomplete record of what is wrong with a change.
- It does not show that any fix helped, or that judge independence changes finding quality.

## How to reproduce

```bash
# Spend the generation tokens once and save what the specialists found
bench benchmarks/real-prs.csv --record benchmarks/snapshots --out benchmarks/run-record.json --md benchmarks/run-record.md

# Replay only grounding and the judge on the same findings, once per judge
MULTIAGENT_MODEL_EVAL=openai/gpt-oss-120b bench benchmarks/real-prs.csv --replay benchmarks/snapshots --out benchmarks/replay-same-model.json
MULTIAGENT_MODEL_EVAL=openai/gpt-oss-20b  bench benchmarks/real-prs.csv --replay benchmarks/snapshots --out benchmarks/replay-same-family.json
MULTIAGENT_MODEL_EVAL=qwen/qwen3.8-27b    bench benchmarks/real-prs.csv --replay benchmarks/snapshots --out benchmarks/replay-independent.json

# Paired comparison (findings are identical, so any difference comes from the judge)
bench-compare benchmarks/replay-same-model.json benchmarks/replay-independent.json
```

Free-tier notes: Groq caps `openai/gpt-oss-120b` at 200,000 tokens per day, and a full recording run used about 150,000 tokens
across all models, so it fits once a day. Set `MULTIAGENT_AGENT_TIMEOUT_SECONDS=300` so a throttling wait is not counted as a failure, and use
`bench-compare --exclude-degraded` to leave out rows where a stage failed. A replay still clones each repository, which is most
of its 15-25 minutes. Groq retires models; check that the models above are still listed for your key.

## Threats to validity

- **Small and hand-picked:** 12 PRs, chosen by hand from 20 candidates; one run per condition; the pipeline is non-deterministic.
- **The matcher is lexical.** It pairs comments that share vocabulary and misses paraphrase (both seen above). It has not been
  validated against human labels; the planned check is to hand-label roughly 150 finding/comment pairs and measure the matcher
  against them.
- **Ground truth is incomplete.** Reviewers do not write everything, so a valid finding they did not mention counts as a false
  positive; precision is a lower bound. Some comments are questions or style nits that no automated review would match.
- **Comments were written against earlier revisions.** Only threads still applicable to the merged code were kept, which reduces
  but does not remove this.
- **Judge comparison:** one generator setup, one run per judge, 65 findings.
- **Free-tier infrastructure** (rate limits, timeouts, retired models) can degrade a run; rows where it did are flagged.
