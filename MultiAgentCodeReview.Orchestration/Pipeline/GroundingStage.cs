using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiAgentCodeReview.Core.Configuration;
using MultiAgentCodeReview.Core.Models;
using MultiAgentCodeReview.Orchestration.Tools;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

/// <summary>
/// Deterministic anti-hallucination check, no LLM involved. Every finding is asked to quote the code it is about
/// (<see cref="Finding.CodeSnippet"/>); this stage verifies that code really exists, in the file as it is at the
/// reviewed commit or in the diff (so quotes of removed lines pass). A finding that quotes code that is not there
/// gets <see cref="Finding.Grounded"/> = false and is flagged.
/// This catches fabricated or misquoted code; it does NOT prove the reasoning about real code is right.
/// Quotes are matched on whitespace-normalised text and on identifier boundaries, so a fragment cut off
/// mid-token ("is not nul" for "is not null") does not count as found.
/// </summary>
public class GroundingStage
{
    private const int MinSegmentChars = 8;

    private static readonly Regex LineMarker = new(@"^\s*\[Line \d+\]\s?", RegexOptions.Compiled);
    private static readonly Regex RemovedMarker = new(@"^\s*\[-\]\s?-?", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Ellipsis = new(@"\.\.\.|…", RegexOptions.Compiled);

    private readonly GroundingConfig _config;
    private readonly ILogger<GroundingStage>? _logger;

    public GroundingStage(IOptions<PipelineConfig> config, ILogger<GroundingStage>? logger = null)
    {
        _config = config.Value.Grounding;
        _logger = logger;
    }

    public async Task<AgentResult> ExecuteAsync(AgentResult result, PipelineContext context, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || result.Findings.Count == 0)
            return result;

        var diffText = Normalize(string.Join("\n", (context.Diff?.Files ?? new List<FileDiff>())
            .SelectMany(f => f.Hunks)
            .SelectMany(h => h.Content.Split('\n'))
            .Where(l => l.Length > 0)
            .Select(l => l[1..])));              // drop the leading '+', '-' or ' '

        var fileCache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var updated = new List<Finding>(result.Findings.Count);
        int grounded = 0, ungrounded = 0, unchecked_ = 0;

        foreach (var finding in result.Findings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verdict = await VerifyAsync(finding, context, diffText, fileCache, cancellationToken);
            updated.Add(finding with { Grounded = verdict });
            if (verdict == true) grounded++;
            else if (verdict == false) ungrounded++;
            else unchecked_++;
        }

        _logger?.LogInformation("Grounding check: {Grounded} grounded, {Ungrounded} not found in the commit, {Unchecked} not checkable",
            grounded, ungrounded, unchecked_);

        var note = ungrounded == 0
            ? $"Grounding check: all {grounded} checkable finding(s) quote code that exists in the commit" +
              (unchecked_ > 0 ? $"; {unchecked_} quoted no code and could not be checked." : ".")
            : $"Grounding check: {ungrounded} of {updated.Count} finding(s) quote code that could not be found in the commit and are flagged" +
              (unchecked_ > 0 ? $"; {unchecked_} quoted no code and could not be checked." : ".");

        return new AgentResult(updated, string.IsNullOrWhiteSpace(result.Summary) ? note : $"{result.Summary} {note}");
    }

    /// <returns>true = quoted code found, false = quoted code not found, null = nothing to check.</returns>
    private async Task<bool?> VerifyAsync(
        Finding finding, PipelineContext context, string diffText, Dictionary<string, string?> fileCache, CancellationToken ct)
    {
        var segments = ExtractSegments(finding.CodeSnippet);
        if (segments.Count == 0 || string.IsNullOrEmpty(finding.File))
            return null;

        var path = ResolvePath(finding.File, context);
        if (!fileCache.TryGetValue(path, out var fileText))
        {
            fileText = await ReadFileAtCommitAsync(context.RepositoryPath, context.CommitHash, path, ct);
            fileCache[path] = fileText;
        }

        // A path that does not exist at this commit and is not part of the diff is itself a hallucination.
        if (fileText == null && !IsInDiff(path, context))
            return false;

        return segments.All(s => ContainsToken(fileText ?? "", s) || ContainsToken(diffText, s));
    }

    /// <summary>
    /// The lines of a quoted snippet, cleaned of the diff decoration models copy along ("[Line 97] +", "[-] -"),
    /// whitespace-normalised, split at "..." and with trivial fragments (braces, "else") ignored.
    /// </summary>
    public static List<string> ExtractSegments(string? snippet)
    {
        var segments = new List<string>();
        if (string.IsNullOrWhiteSpace(snippet))
            return segments;

        foreach (var raw in snippet.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var hadMarker = false;
            if (RemovedMarker.IsMatch(line)) { line = RemovedMarker.Replace(line, "", 1); hadMarker = true; }
            else if (LineMarker.IsMatch(line)) { line = LineMarker.Replace(line, "", 1); hadMarker = true; }

            var trimmed = line.TrimStart();
            if (hadMarker && (trimmed.StartsWith('+') || trimmed.StartsWith('-')))
                line = trimmed[1..];
            else if (!hadMarker && (trimmed.StartsWith("+ ") || trimmed.StartsWith("- ")))
                line = trimmed[2..];

            foreach (var part in Ellipsis.Split(line))
            {
                var normalized = Normalize(part);
                if (normalized.Length >= MinSegmentChars)
                    segments.Add(normalized);
            }
        }
        return segments;
    }

    /// <summary>
    /// True if <paramref name="segment"/> occurs in <paramref name="text"/> as a whole: a match that starts or ends
    /// inside an identifier (e.g. "nul" inside "null") does not count.
    /// </summary>
    public static bool ContainsToken(string text, string segment)
    {
        if (text.Length == 0 || segment.Length == 0)
            return false;

        var from = 0;
        while ((from = text.IndexOf(segment, from, StringComparison.Ordinal)) >= 0)
        {
            var end = from + segment.Length;
            var startsClean = from == 0 || !IsIdentifierChar(text[from - 1]) || !IsIdentifierChar(segment[0]);
            var endsClean = end == text.Length || !IsIdentifierChar(text[end]) || !IsIdentifierChar(segment[^1]);
            if (startsClean && endsClean)
                return true;
            from++;
        }
        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Normalize(string text) => Whitespace.Replace(text, " ").Trim();

    // Models sometimes drop or shorten the directory; match the way the rest of the pipeline matches paths.
    private static string ResolvePath(string reported, PipelineContext context)
    {
        var known = context.Diff?.Files.Select(f => f.Path).Concat(context.ChangedFiles.Select(c => c.Path)) ?? Enumerable.Empty<string>();
        var match = known.FirstOrDefault(p => p == reported)
                    ?? known.FirstOrDefault(p => p.Length > 0 && (p.EndsWith(reported) || reported.EndsWith(p)));
        return match ?? reported;
    }

    private static bool IsInDiff(string path, PipelineContext context) =>
        context.Diff?.Files.Any(f => f.Path == path) == true;

    /// <summary>The file as it is AT the reviewed commit (not the working tree, which may be somewhere else).</summary>
    private static async Task<string?> ReadFileAtCommitAsync(string repositoryPath, string commit, string path, CancellationToken ct)
    {
        // The ref goes into a single "<commit>:<path>" argument, so it must not be able to look like an option.
        if (!GitOperationsTool.IsSafeRef(commit))
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add($"{commit}:{path}");

        try
        {
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var text = await stdout;
            await stderr;
            return process.ExitCode == 0 ? Normalize(text) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
