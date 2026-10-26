using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

/// <summary>
/// Reads the benchmark CSV: repo URL, commit SHA, and the human reviewers' findings for that commit.
/// A header row is optional (columns are then positional: url, sha, findings). Findings go in ONE cell
/// (quote it if it contains commas), either as a JSON array of strings or separated by '|' or newlines.
/// </summary>
public static class BenchmarkCsv
{
    private static readonly Regex Sha = new(@"^[0-9a-fA-F]{7,64}$", RegexOptions.Compiled);

    private static readonly string[] RepoNames = { "repourl", "repo", "url", "repository", "repositoryurl" };
    private static readonly string[] ShaNames = { "commitsha", "commit", "sha", "commithash" };
    private static readonly string[] FindingNames =
        { "findings", "groundtruth", "groundtruthfindings", "humanfindings", "reviewerfindings", "comments", "reviewercomments" };

    public static bool IsRepoUrl(string value) =>
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("git@", StringComparison.OrdinalIgnoreCase);

    public static bool IsCommitSha(string value) => Sha.IsMatch(value);

    public static List<BenchmarkRow> ParseRows(string csvText)
    {
        var records = ParseRecords(csvText);
        if (records.Count == 0)
            throw new FormatException("The CSV file is empty.");

        int repoCol = 0, shaCol = 1, findingsCol = 2, firstData = 0;

        // No header if the first cell already looks like a repo URL.
        if (!IsRepoUrl(records[0].Fields[0].Trim()))
        {
            firstData = 1;
            var header = records[0].Fields.Select(Normalize).ToList();
            repoCol = header.FindIndex(h => RepoNames.Contains(h));
            shaCol = header.FindIndex(h => ShaNames.Contains(h));
            findingsCol = header.FindIndex(h => FindingNames.Contains(h));
            if (repoCol < 0 || shaCol < 0 || findingsCol < 0)
            {
                throw new FormatException(
                    $"CSV header (line {records[0].Line}) must name a repo column (repo_url), a commit column (commit_sha) " +
                    "and a ground-truth column (findings). Or omit the header and use the order: repo_url, commit_sha, findings.");
            }
        }

        var rows = new List<BenchmarkRow>();
        var errors = new List<string>();
        var need = Math.Max(repoCol, Math.Max(shaCol, findingsCol)) + 1;

        for (var i = firstData; i < records.Count; i++)
        {
            var (line, fields) = (records[i].Line, records[i].Fields);
            if (fields.Count < need)
            {
                errors.Add($"line {line}: expected at least {need} columns (repo_url, commit_sha, findings), found {fields.Count}.");
                continue;
            }

            var url = fields[repoCol].Trim();
            var sha = fields[shaCol].Trim();
            var findings = ParseFindingsCell(fields[findingsCol]);

            if (!IsRepoUrl(url))
                errors.Add($"line {line}: '{Truncate(url)}' is not a git URL (expected https://..., http://... or git@...).");
            if (!IsCommitSha(sha))
                errors.Add($"line {line}: '{Truncate(sha)}' is not a commit SHA (7-64 hex characters).");
            if (findings.Count == 0)
                errors.Add($"line {line}: no ground-truth findings (recall would be undefined).");

            if (IsRepoUrl(url) && IsCommitSha(sha) && findings.Count > 0)
                rows.Add(new BenchmarkRow(rows.Count + 1, url, sha, findings));
        }

        if (errors.Count > 0)
            throw new FormatException("Invalid benchmark CSV:\n  " + string.Join("\n  ", errors.Take(20)) +
                                      (errors.Count > 20 ? $"\n  ...and {errors.Count - 20} more" : ""));
        if (rows.Count == 0)
            throw new FormatException("The CSV has a header but no data rows.");

        return rows;
    }

    /// <summary>JSON array of strings if the cell is one; otherwise split on '|' and newlines.</summary>
    public static List<string> ParseFindingsCell(string cell)
    {
        var text = cell.Trim();
        IEnumerable<string>? items = null;

        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
                {
                    items = doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                }
            }
            catch (JsonException)
            {
                // Not JSON (e.g. a comment that merely starts with "[nit]"): treat as delimited text.
            }
        }

        items ??= text.Split(new[] { '\n', '\r', '|' }, StringSplitOptions.None);

        return items
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed record CsvRecord(int Line, List<string> Fields);

    /// <summary>Minimal RFC 4180 reader: quoted fields, "" escapes, commas/newlines inside quotes, CRLF or LF.</summary>
    public static List<CsvRecord> ParseRecords(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var i = 0;

        void EndField() { fields.Add(field.ToString()); field.Clear(); }
        void EndRecord()
        {
            EndField();
            if (!(fields.Count == 1 && fields[0].Trim().Length == 0)) // skip blank lines
                records.Add(new CsvRecord(recordLine, fields));
            fields = new List<string>();
        }

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"' && field.Length == 0)
            {
                var startLine = line;
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                        i++; closed = true; break;
                    }
                    if (text[i] == '\n') line++;
                    field.Append(text[i]);
                    i++;
                }
                if (!closed)
                    throw new FormatException($"Unterminated quoted field starting on line {startLine}.");
                if (i < text.Length && text[i] != ',' && text[i] != '\n' && text[i] != '\r')
                    throw new FormatException($"Unexpected character '{text[i]}' after a closing quote on line {line}.");
                continue;
            }

            if (c == ',') { EndField(); i++; continue; }
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                EndRecord();
                line++;
                recordLine = line;
                continue;
            }

            field.Append(c);
            i++;
        }

        if (field.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }

    private static string Normalize(string header) =>
        new string(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Truncate(string s) => s.Length > 60 ? s[..60] + "..." : s;
}
