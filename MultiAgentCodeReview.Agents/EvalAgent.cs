using System.Text.Json;
using AutoGen;
using AutoGen.Core;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Agents;

/// <summary>
/// Self-evaluation agent: scores findings on relevance, severity accuracy and actionability (1-3 each).
/// Scoring is best-effort - anything the model fails to score comes back as null so the caller can
/// fail open instead of hiding findings because the evaluator hiccuped.
/// </summary>
public class EvalAgent : MultiAgentCodeReview.Core.Interfaces.IEvalAgent
{
    public string Name => "EvalAgent";

    private readonly IAgent _agent;

    public EvalAgent(IAgent agent)
    {
        _agent = agent;
    }

    public async Task<List<FindingEvaluation?>> EvaluateAsync(
        PipelineContext context,
        IReadOnlyList<Finding> findings,
        CancellationToken cancellationToken = default)
    {
        if (findings.Count == 0)
            return new List<FindingEvaluation?>();

        var response = await _agent.GenerateReplyAsync(
            new[] { new TextMessage(Role.User, BuildPrompt(context, findings)) },
            cancellationToken: cancellationToken);
        var content = response is TextMessage tm ? tm.Content : response.ToString();
        return ParseEvaluations(content ?? "", findings.Count);
    }

    public Task<AgentResult> AnalyzeAsync(PipelineContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new AgentResult(
            new List<Finding>(),
            "Eval agent scores existing findings; it does not produce any"));
    }

    public static string BuildPrompt(PipelineContext context, IReadOnlyList<Finding> findings)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Commit: {context.CommitHash}");
        sb.AppendLine("Changed files: " + string.Join(", ", context.ChangedFiles.Take(30).Select(f => f.Path)));
        sb.AppendLine();
        sb.AppendLine($"Score the following {findings.Count} findings. Use each finding's id in your answer.");

        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            sb.AppendLine();
            sb.AppendLine($"### Finding id={i}");
            sb.AppendLine($"Severity: {f.Severity}");
            sb.AppendLine($"Category: {f.Category}");
            sb.AppendLine($"Location: {DescribeLocation(context, f)}");
            if (f.Agents is { Count: > 0 })
                sb.AppendLine($"Reported by: {string.Join(", ", f.Agents)}");
            sb.AppendLine($"Reviewer-reported confidence: {f.Confidence:0.##}");
            sb.AppendLine($"Description: {Clip(f.Description, 300)}");
            if (!string.IsNullOrWhiteSpace(f.CodeSnippet))
                sb.AppendLine($"Code snippet: {Clip(f.CodeSnippet, 200)}");
            sb.AppendLine($"Recommendation: {Clip(f.Recommendation, 200)}");
            if (!string.IsNullOrWhiteSpace(f.QuickFix))
                sb.AppendLine($"Quick fix: {Clip(f.QuickFix, 150)}");

            var excerpt = DiffExcerpt(context, f);
            sb.AppendLine(excerpt.Length > 0
                ? $"Changed code near the finding:\n{excerpt}"
                : "Changed code near the finding: (not available)");
        }

        return sb.ToString();
    }

    public static List<FindingEvaluation?> ParseEvaluations(string response, int count)
    {
        var results = Enumerable.Repeat<FindingEvaluation?>(null, count).ToList();
        if (count == 0 || string.IsNullOrWhiteSpace(response))
            return results;

        var json = ExtractJson(AgentHelpers.StripThinkingTrace(response));
        if (json == null)
            return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array)
                items = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     TryGet(root, out items, "evaluations", "scores") &&
                     items.ValueKind == JsonValueKind.Array)
            { /* items assigned by TryGet */ }
            else
                return results;

            var position = 0;
            foreach (var item in items.EnumerateArray())
            {
                var fallbackId = position++;
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var id = TryGet(item, out var idEl, "id", "index") && TryToInt(idEl, out var parsedId)
                    ? parsedId
                    : fallbackId;
                if (id < 0 || id >= count)
                    continue;

                if (!TryScore(item, out var relevance, "relevance") ||
                    !TryScore(item, out var severity, "severityAccuracy") ||
                    !TryScore(item, out var actionability, "actionability"))
                    continue;

                string? rationale = null;
                if (TryGet(item, out var ratEl, "rationale", "reason") && ratEl.ValueKind == JsonValueKind.String)
                    rationale = Clip(ratEl.GetString(), 200);

                results[id] = new FindingEvaluation(relevance, severity, actionability, rationale);
            }
        }
        catch (JsonException)
        {
            // Unparseable output: leave everything unscored.
        }

        return results;
    }

    private static string DescribeLocation(PipelineContext context, Finding f)
    {
        if (string.IsNullOrEmpty(f.File))
            return "no location given";

        var where = f.Line > 0 ? $"{f.File}:{f.Line}" : f.File;
        return IsInChangedSet(context, f.File)
            ? $"{where} (file is part of this change)"
            : $"{where} (file is NOT part of this change)";
    }

    private static bool IsInChangedSet(PipelineContext context, string file) =>
        context.ChangedFiles.Any(c => PathsMatch(c.Path, file));

    // Same loose path match FilterStage uses; diff paths and LLM-reported paths are not always identical.
    private static bool PathsMatch(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a == b || a.EndsWith(b) || b.EndsWith(a));

    private static string DiffExcerpt(PipelineContext context, Finding f)
    {
        if (context.Diff == null || string.IsNullOrEmpty(f.File))
            return "";

        var fileDiff = context.Diff.Files.FirstOrDefault(d => PathsMatch(d.Path, f.File));
        if (fileDiff == null || fileDiff.Hunks.Count == 0)
            return "";

        var hunk = f.Line > 0
            ? fileDiff.Hunks.FirstOrDefault(h => f.Line >= h.NewStart && f.Line < h.NewStart + Math.Max(h.NewLines, 1))
              ?? fileDiff.Hunks[0]
            : fileDiff.Hunks[0];
        return Clip(hunk.Content, 500);
    }

    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var t = text.Replace("\r", "").Trim();
        return t.Length > max ? t[..max] + "..." : t;
    }

    private static string? ExtractJson(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```"))
        {
            var firstNewline = t.IndexOf('\n');
            if (firstNewline >= 0)
                t = t[(firstNewline + 1)..];
            var closing = t.LastIndexOf("```", StringComparison.Ordinal);
            if (closing >= 0)
                t = t[..closing];
        }

        var start = t.IndexOfAny(new[] { '{', '[' });
        if (start < 0)
            return null;
        var end = t[start] == '{' ? t.LastIndexOf('}') : t.LastIndexOf(']');
        return end > start ? t.Substring(start, end - start + 1) : null;
    }

    // Property lookup tolerant of case and snake_case ("severity_accuracy"), since models drift from the schema.
    private static bool TryGet(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var normalized = prop.Name.Replace("_", "");
            if (names.Any(n => string.Equals(n.Replace("_", ""), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryToInt(JsonElement el, out int value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
        {
            value = (int)Math.Round(d);
            return true;
        }
        return el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out value);
    }

    private static bool TryScore(JsonElement obj, out int score, string name)
    {
        score = 0;
        if (!TryGet(obj, out var el, name) || !TryToInt(el, out var raw))
            return false;
        score = Math.Clamp(raw, FindingEvaluation.MinScore, FindingEvaluation.MaxScore);
        return true;
    }
}
