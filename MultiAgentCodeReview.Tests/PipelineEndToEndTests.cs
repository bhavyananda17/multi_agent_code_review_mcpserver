using Microsoft.Extensions.DependencyInjection;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.DI;
using MultiAgentCodeReview.Orchestration.Pipeline;
using MultiAgentCodeReview.Orchestration.Reporting;

namespace MultiAgentCodeReview.Tests;

[CollectionDefinition("pipeline", DisableParallelization = true)]
public class PipelineCollection;

/// <summary>
/// The real pipeline (AutoGen + the OpenAI SDK + every stage) against a fake OpenAI-compatible server, so the stages
/// are tested working together without a network or an API key.
/// </summary>
[Collection("pipeline")]
public class PipelineEndToEndTests : IDisposable
{
    private static readonly string[] Variables =
    {
        "MULTIAGENT_BASE_URL", "MULTIAGENT_API_KEY", "MULTIAGENT_STATIC_ANALYSIS", "MULTIAGENT_GROUNDING_ENABLED", "MULTIAGENT_EVAL_ENABLED",
        "MULTIAGENT_EVAL_THRESHOLD", "MULTIAGENT_MODEL_EVAL", "MULTIAGENT_MODEL_SYNTHESIS", "MULTIAGENT_MODEL_SECURITY",
        "MULTIAGENT_MODEL_PERFORMANCE", "MULTIAGENT_MODEL_LOGIC", "MULTIAGENT_MODEL_TRIAGE"
    };
    private readonly Dictionary<string, string?> _saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);

    public PipelineEndToEndTests()
    {
        foreach (var v in Variables) Environment.SetEnvironmentVariable(v, null);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    private const string Findings = """
        {"findings":[
          {"severity":"HIGH","category":"SQL_INJECTION","file":"src/UserRepo.cs","line":4,"description":"SQL injection by string concatenation [GOOD]","recommendation":"Use parameters.","codeSnippet":"public string Q(string id) => \"SELECT \" + id;","confidence":0.9,"quickFix":"Parameterize the query."},
          {"severity":"CRITICAL","category":"CODE_SMELL","file":"src/UserRepo.cs","line":5,"description":"Invented problem about a database helper [GOOD]","recommendation":"Remove it.","codeSnippet":"var db = new FakeDatabase();","confidence":0.9,"quickFix":"Remove it."},
          {"severity":"LOW","category":"NAMING","file":"src/UserRepo.cs","line":6,"description":"Method A is empty [WEAK]","recommendation":"r","codeSnippet":"public void A() { }","confidence":0.8,"quickFix":"q"}
        ],"summary":"s"}
        """;

    private static string Respond(string system, string user)
    {
        if (system.Contains("Triage Router")) return """{"selected_agents":["SECURITY"]}""";
        if (system.Contains("Security Agent")) return Findings;
        if (system.Contains("Quality Evaluator"))
        {
            var entries = System.Text.RegularExpressions.Regex.Matches(user, @"### Finding id=(\d+)[\s\S]*?\[(GOOD|WEAK)\]")
                .Select(m => $"{{\"id\":{m.Groups[1].Value},\"relevance\":{(m.Groups[2].Value == "GOOD" ? 3 : 1)},\"severityAccuracy\":{(m.Groups[2].Value == "GOOD" ? 3 : 1)},\"actionability\":2,\"rationale\":\"x\"}}");
            return "{\"evaluations\":[" + string.Join(",", entries) + "]}";
        }
        return "ok";
    }

    private static TempRepo Repo(out string sha)
    {
        var repo = new TempRepo();
        repo.Write("README.md", "x"); repo.Commit("base");
        repo.Write("src/UserRepo.cs", "namespace N;\npublic class UserRepo\n{\n    public string Q(string id) => \"SELECT \" + id;\n    public void A() { }\n}\n");
        sha = repo.Commit("change");
        return repo;
    }

    private static ServiceProvider Provider(FakeLlm llm)
    {
        Environment.SetEnvironmentVariable("MULTIAGENT_BASE_URL", llm.BaseUrl);
        Environment.SetEnvironmentVariable("MULTIAGENT_API_KEY", "test-key");
        return new ServiceCollection().AddMultiAgentCodeReview().BuildServiceProvider();
    }

    private static Task<ReviewOutput> Review(FakeLlm llm, TempRepo repo, string sha) =>
        Provider(llm).GetRequiredService<CodeReviewPipeline>().RunReviewAsync(repo.Path, sha, "HEAD~1");

    [Fact]
    public async Task Every_stage_runs_and_the_report_separates_what_it_does_not_trust()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);

        var output = await Review(llm, repo, sha);
        var findings = output.Result.Findings;

        Assert.Equal(3, findings.Count);                                              // nothing is silently dropped

        var fabricated = findings.Single(f => f.Description.StartsWith("Invented"));
        Assert.False(fabricated.Grounded);                                            // its quoted code is not in the commit
        Assert.True(findings.Single(f => f.Description.StartsWith("SQL")).Grounded);

        var weak = findings.Single(f => f.Description.StartsWith("Method A"));
        Assert.True(weak.Evaluation!.LowConfidence);                                  // the judge scored it 4/9
        Assert.Equal(2, findings.Count(f => f.IsFlagged()));

        Assert.Contains("Grounding check: 1 of 3 finding(s) quote code that could not be found", output.Result.Summary);
        Assert.Contains("Self-evaluation scored 3/3 findings", output.Result.Summary);
        Assert.NotNull(output.Context.EvaluationSummary);
    }

    [Fact]
    public async Task The_report_counts_only_trusted_findings_and_lists_the_rest_with_their_reasons()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);

        var report = ReportFormatter.FormatReport(await Review(llm, repo, sha));

        Assert.Contains("**Total Findings:** 1", report);
        Assert.Contains("**Low-confidence findings:** 2", report);
        Assert.Contains("| **Score** | 90/100 |", report);                       // 100 - 10 for the one trusted High finding
        Assert.Contains("## Low-Confidence Findings", report);
        Assert.Contains("Unverified", report);                                    // the fabricated quote
        Assert.DoesNotContain("## CRITICAL - Must Fix Before Merge", report);     // the invented Critical never reaches the headline
    }

    [Fact]
    public async Task The_judge_is_reported_as_the_same_model_that_wrote_the_findings_unless_configured_otherwise()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);

        var summary = (await Review(llm, repo, sha)).Context.EvaluationSummary!;
        Assert.True(summary.JudgeSameModelAsGenerators);   // with no model settings every role runs on the same default model

        Environment.SetEnvironmentVariable("MULTIAGENT_MODEL_EVAL", "qwen/qwen3.8-27b");
        var independent = (await Review(llm, repo, sha)).Context.EvaluationSummary!;
        Assert.False(independent.JudgeSameModelAsGenerators);
        Assert.False(independent.JudgeSameFamilyAsGenerators);
        Assert.Contains(llm.Requests, r => r.Model == "qwen/qwen3.8-27b" && r.System.Contains("Quality Evaluator"));
    }

    [Fact]
    public async Task A_judge_that_returns_nothing_usable_is_reported_on_the_review_and_in_the_report()
    {
        using var llm = new FakeLlm { Responder = (system, user) => system.Contains("Quality Evaluator") ? "I'm sorry, I can't score these." : Respond(system, user) };
        using var repo = Repo(out var sha);

        var output = await Review(llm, repo, sha);

        Assert.All(output.Result.Findings, f => Assert.Null(f.Evaluation));
        var warning = Assert.Single(output.Context.Warnings!);
        Assert.Contains("The judge could not score 3 of 3 findings", warning);

        var report = ReportFormatter.FormatReport(output);
        Assert.Contains("Incomplete review", report);
        Assert.Contains("The judge could not score 3 of 3 findings", report);
    }

    [Fact]
    public async Task A_clean_review_carries_no_warnings_and_no_incomplete_banner()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);

        var output = await Review(llm, repo, sha);

        Assert.Null(output.Context.Warnings);
        Assert.DoesNotContain("Incomplete review", ReportFormatter.FormatReport(output));
    }

    private static bool IsJudgeRequest(LlmRequest r) => r.System.Contains("Quality Evaluator");

    [Fact]
    public async Task Token_usage_is_counted_from_the_responses_and_matches_the_calls_made()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);

        var usage = (await Review(llm, repo, sha)).Context.TokenUsage!;

        Assert.Equal(llm.Requests.Count, usage.Calls);                 // triage + specialist + judge
        Assert.Equal(2 * llm.Requests.Count, usage.Total);             // the fake reports 1 prompt + 1 completion token per call
        Assert.Equal(usage.Calls, usage.ByModel.Values.Sum(m => m.Calls));
    }

    [Fact]
    public async Task Generate_then_verify_gives_the_same_review_as_running_it_in_one_go()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        var pipeline = Provider(llm).GetRequiredService<CodeReviewPipeline>();

        var whole = await pipeline.RunReviewAsync(repo.Path, sha, "HEAD~1");
        var snapshot = await pipeline.GenerateAsync(repo.Path, sha, "HEAD~1");
        var split = await pipeline.VerifyAsync(snapshot);

        Assert.Equal(whole.Result.Summary, split.Result.Summary);
        Assert.Equal(whole.Result.Findings.Select(f => (f.Description, f.Grounded, f.Evaluation?.Total)),
                     split.Result.Findings.Select(f => (f.Description, f.Grounded, f.Evaluation?.Total)));
        Assert.Equal(whole.Context.TokenUsage!.Total, split.Context.TokenUsage!.Total);   // generation + verification adds up either way
    }

    [Fact]
    public async Task A_recorded_generation_can_be_verified_again_without_calling_the_specialists()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        var pipeline = Provider(llm).GetRequiredService<CodeReviewPipeline>();

        var snapshot = await pipeline.GenerateAsync(repo.Path, sha, "HEAD~1");
        var saved = System.Text.Json.JsonSerializer.Serialize(snapshot);               // what a --record run writes to disk
        var restored = System.Text.Json.JsonSerializer.Deserialize<GenerationSnapshot>(saved)!;

        llm.Requests.Clear();
        var replayed = await pipeline.VerifyAsync(restored, includeGenerationTokens: false);

        Assert.All(llm.Requests, r => Assert.True(IsJudgeRequest(r), "replay must only call the judge"));
        Assert.Equal(3, replayed.Result.Findings.Count);
        Assert.True(replayed.Result.Findings.Single(f => f.Description.StartsWith("Method A")).Evaluation!.LowConfidence);
        Assert.Equal(llm.Requests.Count, replayed.Context.TokenUsage!.Calls);          // verification spend only
    }

    [Fact]
    public async Task Replaying_on_the_same_findings_with_a_different_judge_changes_only_the_judge()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        var snapshot = await Provider(llm).GetRequiredService<CodeReviewPipeline>().GenerateAsync(repo.Path, sha, "HEAD~1");

        var sameModel = await Provider(llm).GetRequiredService<CodeReviewPipeline>().VerifyAsync(snapshot);
        Environment.SetEnvironmentVariable("MULTIAGENT_MODEL_EVAL", "qwen/qwen3.8-27b");
        var independent = await Provider(llm).GetRequiredService<CodeReviewPipeline>().VerifyAsync(snapshot);

        Assert.Equal(sameModel.Result.Findings.Select(f => f.Description), independent.Result.Findings.Select(f => f.Description));   // identical findings
        Assert.True(sameModel.Context.EvaluationSummary!.JudgeSameModelAsGenerators);
        Assert.False(independent.Context.EvaluationSummary!.JudgeSameModelAsGenerators);
        Assert.Equal("qwen/qwen3.8-27b", independent.Context.EvaluationSummary!.JudgeModel);
    }

    private sealed class LocalRepos(string path) : MultiAgentCodeReview.Orchestration.Benchmarking.IBenchmarkRepoProvider
    {
        public Task<string> PrepareAsync(string repoUrl, string commitSha, CancellationToken cancellationToken = default) => Task.FromResult(path);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<MultiAgentCodeReview.Orchestration.Benchmarking.BenchmarkResult> Bench(
        FakeLlm llm, TempRepo repo, string sha, MultiAgentCodeReview.Orchestration.Benchmarking.BenchmarkOptions options)
    {
        var services = Provider(llm);
        var runner = new MultiAgentCodeReview.Orchestration.Benchmarking.BenchmarkRunner(
            new LocalRepos(repo.Path), services.GetRequiredService<CodeReviewPipeline>(),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MultiAgentCodeReview.Core.Configuration.PipelineConfig>>());
        var row = new MultiAgentCodeReview.Orchestration.Benchmarking.BenchmarkRow(1, "https://github.com/acme/widgets", sha, new List<string> { "SQL injection by string concatenation" });
        return await runner.RunAsync(new[] { row }, options);
    }

    [Fact]
    public async Task The_benchmark_can_record_generations_and_replay_them_for_the_price_of_the_judge_only()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        var directory = Path.Combine(Path.GetTempPath(), "mar-snapshots-" + Guid.NewGuid());
        try
        {
            var recorded = await Bench(llm, repo, sha, new() { RecordDirectory = directory });
            Assert.Equal("record", recorded.Settings.Mode);
            Assert.Single(Directory.GetFiles(directory, "acme-widgets-*.json"));
            var recordedRow = recorded.Results[0];
            Assert.Equal("ok", recordedRow.Status);

            llm.Requests.Clear();
            var replayed = await Bench(llm, repo, sha, new() { ReplayDirectory = directory });

            Assert.Equal("replay", replayed.Settings.Mode);
            Assert.All(llm.Requests, r => Assert.True(IsJudgeRequest(r)));          // no triage, no specialists
            var replayedRow = replayed.Results[0];
            Assert.Equal("ok", replayedRow.Status);
            Assert.Equal(recordedRow.PredictedCount, replayedRow.PredictedCount);   // same findings, same scoring
            Assert.Equal(recordedRow.F1, replayedRow.F1);
            Assert.True(replayedRow.Tokens!.Total < recordedRow.Tokens!.Total);     // only the judge's tokens
            Assert.Equal(llm.Requests.Count, replayedRow.Tokens.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Replaying_a_row_that_was_never_recorded_fails_that_row_with_a_clear_message()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        var empty = Path.Combine(Path.GetTempPath(), "mar-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(empty);
        try
        {
            var result = await Bench(llm, repo, sha, new() { ReplayDirectory = empty });
            Assert.Equal("error", result.Results[0].Status);
            Assert.Contains("No recorded generation for this row", result.Results[0].Error);
            Assert.Empty(llm.Requests);
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public async Task The_configured_key_is_what_reaches_the_server()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        await Review(llm, repo, sha);
        Assert.All(llm.Requests, r => Assert.Equal("Bearer test-key", r.Authorization));
    }

    [Fact]
    public async Task Stages_can_be_switched_off_and_then_leave_no_trace()
    {
        using var llm = new FakeLlm { Responder = Respond };
        using var repo = Repo(out var sha);
        Environment.SetEnvironmentVariable("MULTIAGENT_GROUNDING_ENABLED", "false");
        Environment.SetEnvironmentVariable("MULTIAGENT_EVAL_ENABLED", "false");
        Environment.SetEnvironmentVariable("MULTIAGENT_STATIC_ANALYSIS", "false");

        var output = await Review(llm, repo, sha);

        Assert.All(output.Result.Findings, f => { Assert.Null(f.Grounded); Assert.Null(f.Evaluation); });
        Assert.Null(output.Context.EvaluationSummary);
        Assert.Null(output.Context.CodeAnalysis);
        Assert.DoesNotContain(llm.Requests, r => r.System.Contains("Quality Evaluator"));
        Assert.DoesNotContain("Grounding check", output.Result.Summary);
    }
}
