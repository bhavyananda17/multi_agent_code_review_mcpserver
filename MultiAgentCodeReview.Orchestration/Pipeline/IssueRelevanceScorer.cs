using System.Text.RegularExpressions;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

/// <summary>
/// Deterministic (no LLM call) ranking of changed files by how relevant they look to a linked
/// GitHub issue. Used by <see cref="FilterStage"/> to decide which files survive the file cap and
/// which diffs reach the specialists first (their prompt budget truncates the diff in order).
/// Ranking is a stable sort, so files with equal scores keep their original git order.
/// </summary>
public static class IssueRelevanceScorer
{
    private const int FullPathMentionScore = 20;
    private const int FileNameMentionScore = 15;
    private const int StemTokenScore = 10;
    private const int PathTokenScore = 2;
    private const int DiffTokenScore = 1;
    private const int DiffTokenCap = 10;

    private static readonly Regex WordRegex = new(@"[A-Za-z][A-Za-z0-9_]*", RegexOptions.Compiled);
    private static readonly Regex CaseBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])|_+", RegexOptions.Compiled);

    // Generic English plus C# keywords: both appear in nearly every diff and would make every
    // file look equally "relevant".
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "when", "not", "but", "are", "was", "has", "have", "can",
        "does", "doesn", "didn", "should", "would", "could", "from", "into", "than", "then", "there", "their",
        "they", "them", "its", "also", "just", "only", "some", "any", "all", "you", "your", "our", "out",
        "issue", "bug", "error", "fix", "fixes", "fail", "fails", "failing", "please", "using", "use", "used",
        "get", "set", "new", "add", "see", "etc", "like", "work", "works", "expected", "actual", "steps",
        "null", "true", "false", "var", "string", "int", "void", "return", "public", "private", "protected",
        "internal", "class", "static", "async", "await", "task", "namespace", "readonly", "override",
        "else", "bool", "object", "list", "value", "name", "type", "data"
    };

    /// <summary>Distinct lowercase tokens (whole identifiers plus their camel/snake sub-words).</summary>
    public static HashSet<string> ExtractKeywords(string text)
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WordRegex.Matches(text))
        {
            AddToken(keywords, match.Value);
            foreach (var part in CaseBoundary.Split(match.Value))
                AddToken(keywords, part);
        }
        return keywords;
    }

    private static void AddToken(HashSet<string> set, string token)
    {
        if (token.Length >= 3 && !Stopwords.Contains(token))
            set.Add(token.ToLowerInvariant());
    }

    public static int Score(string filePath, FileDiff? fileDiff, IssueContext issue)
        => Score(filePath, fileDiff, IssueSignals.From(issue));

    /// <summary>
    /// Returns <paramref name="paths"/> ordered by descending issue relevance (stable).
    /// <paramref name="diff"/> supplies the changed lines used for content matching.
    /// </summary>
    public static List<string> RankPaths(IEnumerable<string> paths, GitDiff diff, IssueContext issue)
    {
        var signals = IssueSignals.From(issue);
        return paths
            .Select((path, index) => (path, index, score: Score(path, FindDiff(diff, path), signals)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.path)
            .ToList();
    }

    /// <summary>Returns the diff's files ordered by descending issue relevance (stable).</summary>
    public static List<FileDiff> RankDiffs(GitDiff diff, IssueContext issue)
    {
        var signals = IssueSignals.From(issue);
        return diff.Files
            .Select((fd, index) => (fd, index, score: Score(fd.Path, fd, signals)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Select(x => x.fd)
            .ToList();
    }

    // Same fuzzy path match FilterStage.FilterRelevantFiles uses, so a changed file and its diff
    // pair up identically in both places.
    internal static FileDiff? FindDiff(GitDiff diff, string path) =>
        diff.Files.FirstOrDefault(fd => fd.Path == path || fd.Path.EndsWith(path) || path.EndsWith(fd.Path));

    private static int Score(string filePath, FileDiff? fileDiff, IssueSignals signals)
    {
        var normalized = filePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var stem = Path.GetFileNameWithoutExtension(normalized);
        var score = 0;

        // 1. The issue names the file explicitly.
        if (normalized.Length > 0 && signals.TextLower.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            score += FullPathMentionScore;
        else if (fileName.Length > 0 && signals.TextLower.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            score += FileNameMentionScore;

        // 2. The issue names the type the file is named after (e.g. "UserRepository").
        if (stem.Length >= 3 && signals.Keywords.Contains(stem.ToLowerInvariant()))
            score += StemTokenScore;

        // 3. Words of the issue appear in the file's path (namespace/folder/file sub-words).
        score += PathTokenScore * ExtractKeywords(normalized).Count(signals.Keywords.Contains);

        // 4. Words of the issue appear in the lines this commit actually changed.
        if (fileDiff != null)
        {
            var hits = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hunk in fileDiff.Hunks)
            {
                foreach (var line in hunk.Content.Split('\n'))
                {
                    if (line.Length < 2 || (line[0] != '+' && line[0] != '-'))
                        continue;
                    foreach (var token in ExtractKeywords(line))
                    {
                        if (signals.Keywords.Contains(token))
                            hits.Add(token);
                    }
                }
            }
            score += DiffTokenScore * Math.Min(hits.Count, DiffTokenCap);
        }

        return score;
    }

    private sealed record IssueSignals(string TextLower, HashSet<string> Keywords)
    {
        public static IssueSignals From(IssueContext issue)
        {
            var text = string.Join("\n", new[] { issue.Title, issue.Body, string.Join(" ", issue.Labels ?? new List<string>()) });
            return new IssueSignals(text.ToLowerInvariant(), ExtractKeywords(text));
        }
    }
}
