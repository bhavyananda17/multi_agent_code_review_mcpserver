using System.Diagnostics;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Tools;

public class GitOperationsTool : IGitOperationsTool
{
    // Characters a ref may contain. Anything else (spaces, quotes, ';', leading '-') is refused, because refs reach
    // git as part of one argument string and a value like "--output=/some/file" would be parsed as an option.
    private static readonly System.Text.RegularExpressions.Regex SafeRef = new(@"^[A-Za-z0-9._/~^@{}-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool IsSafeRef(string value) => !string.IsNullOrWhiteSpace(value) && value[0] != '-' && SafeRef.IsMatch(value);

    private static void RequireSafeRef(string value, string what)
    {
        if (!IsSafeRef(value))
            throw new ArgumentException($"{what} is not a valid git ref (it may only contain letters, digits and . _ / ~ ^ @ {{ }} -, and cannot start with '-').");
    }

    private readonly string _repoPath;

    public GitOperationsTool(string repoPath = ".")
    {
        _repoPath = repoPath;
    }

    public async Task<GitDiff> GetDiffAsync(string fromRef, string toRef = "HEAD")
    {
        RequireSafeRef(fromRef, "from ref");
        RequireSafeRef(toRef, "to ref");
        var resolvedFrom = await ResolveMergeBaseAsync(fromRef, toRef);
        var output = await RunGitCommandAsync($"diff {resolvedFrom} {toRef} --no-color");
        return ParseDiffOutput(output);
    }

    public async Task<List<string>> GetChangedFilesAsync(string fromRef = "HEAD~1", string toRef = "HEAD")
    {
        RequireSafeRef(fromRef, "from ref");
        RequireSafeRef(toRef, "to ref");
        var resolvedFrom = await ResolveMergeBaseAsync(fromRef, toRef);
        var output = await RunGitCommandAsync($"diff --name-only {resolvedFrom} {toRef}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public async Task<List<BlameLine>> GetBlameAsync(string filePath)
    {
        var output = await RunGitCommandAsync($"blame -L 1,1000 --date=short {filePath}");
        var lines = output.Split('\n');
        var result = new List<BlameLine>();

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            
            var parts = line.Split(new[] { '(', ')' }, 3);
            if (parts.Length >= 3)
            {
                var hash = parts[0].Trim();
                var meta = parts[1].Trim();
                var content = parts[2].Trim();
                
                var metaParts = meta.Split(' ');
                var author = metaParts.Length > 0 ? metaParts[0] : "";
                var date = metaParts.Length > 1 ? metaParts[1] : "";
                
                if (DateTime.TryParse(date, out var parsedDate))
                {
                    result.Add(new BlameLine(
                        result.Count + 1,
                        author,
                        parsedDate,
                        hash.Length > 7 ? hash.Substring(0, 7) : hash,
                        content
                    ));
                }
            }
        }
        return result;
    }

    public async Task<List<Commit>> GetFileHistoryAsync(string filePath, int limit = 10)
    {
        var output = await RunGitCommandAsync($"log --oneline --date=short --pretty=format:\"%H|%an|%ad|%s\" -- {filePath} | head -{limit}");
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<Commit>();

        foreach (var line in lines)
        {
            var parts = line.Split('|', 4);
            if (parts.Length >= 4)
            {
                var hash = parts[0];
                var author = parts[1];
                var dateStr = parts[2];
                var message = parts[3];
                
                if (DateTime.TryParse(dateStr, out var date))
                {
                    var commits = await GetChangedFilesForCommitAsync(hash);
                    result.Add(new Commit(hash, author, date, message, commits));
                }
            }
        }
        return result;
    }

    private async Task<List<string>> GetChangedFilesForCommitAsync(string commitHash)
    {
        var output = await RunGitCommandAsync($"show --name-only --pretty=format: {commitHash}");
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("commit") && !l.StartsWith("Author") && !l.StartsWith("Date") && !l.StartsWith("    "))
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    private GitDiff ParseDiffOutput(string output)
    {
        var fileDiffs = new List<FileDiff>();
        string? currentPath = null;
        string? currentOldPath = null;
        var currentChange = ChangeType.Modified;
        var currentHunks = new List<Hunk>();
        var currentHunkLines = new List<string>();
        int oldStart = 0, oldLines = 0, newStart = 0, newLines = 0;
        bool inHunk = false;

        // A hunk is only complete once the next "@@", the next "diff --git" or the end of the output is
        // reached, so all three must flush it. (Flushing only on "@@" silently dropped every file's last
        // hunk and glued its lines onto the start of the next file's first hunk.)
        void FlushHunk()
        {
            if (inHunk && currentHunkLines.Count > 0)
            {
                currentHunks.Add(new Hunk
                (
                    OldStart: oldStart,
                    OldLines: oldLines,
                    NewStart: newStart,
                    NewLines: newLines,
                    Content: string.Join("\n", currentHunkLines) + "\n"
                ));
            }
            currentHunkLines.Clear();
            inHunk = false;
        }

        void FlushFile()
        {
            FlushHunk();
            if (currentPath != null)
                fileDiffs.Add(new FileDiff(currentPath, currentOldPath ?? currentPath, currentChange, currentHunks));
            currentPath = null;
            currentOldPath = null;
            currentChange = ChangeType.Modified;
            currentHunks = new List<Hunk>();
        }

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("diff --git "))
            {
                FlushFile();
                currentPath = ParseDiffGitPath(line);
            }
            else if (line.StartsWith("@@"))
            {
                FlushHunk();

                var hunkHeader = line.Substring(3, line.IndexOf("@@", 3) - 3).Trim();
                var parts = hunkHeader.Split(' ');
                if (parts.Length >= 2)
                {
                    var oldPart = parts[0].TrimStart('-');
                    var newPart = parts[1].TrimStart('+');

                    var oldParts = oldPart.Split(',');
                    var newParts = newPart.Split(',');

                    oldStart = int.Parse(oldParts[0]);
                    oldLines = oldParts.Length > 1 ? int.Parse(oldParts[1]) : 1;
                    newStart = int.Parse(newParts[0]);
                    newLines = newParts.Length > 1 ? int.Parse(newParts[1]) : 1;
                }
                inHunk = true;
            }
            // File-level header lines come before the first hunk.
            else if (!inHunk && line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                currentChange = ChangeType.Added;
            }
            else if (!inHunk && line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                currentChange = ChangeType.Deleted;
            }
            else if (!inHunk && line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                currentChange = ChangeType.Renamed;
                currentOldPath = line["rename from ".Length..];
            }
            else if (inHunk)
            {
                currentHunkLines.Add(line);
            }
        }

        FlushFile();

        var summary = $"{fileDiffs.Count} files changed";
        return new GitDiff(summary, fileDiffs);
    }

    /// <summary>
    /// Extracts the (new) file path from a "diff --git a/X b/X" header. When the file was not renamed the
    /// two halves are identical, which pins the path exactly even if it contains spaces; renames fall back
    /// to the last " b/" marker. (The old TrimStart('a','/','b') ate leading a/b characters of the path
    /// itself, e.g. "app/" became "pp/" and "bin/" became "in/".)
    /// </summary>
    private static string ParseDiffGitPath(string headerLine)
    {
        var header = headerLine["diff --git ".Length..];

        var pathLength = (header.Length - 5) / 2;
        if ((header.Length - 5) % 2 == 0 && pathLength > 0 &&
            header.StartsWith("a/", StringComparison.Ordinal) &&
            string.CompareOrdinal(header, 2, header, pathLength + 5, pathLength) == 0 &&
            string.CompareOrdinal(header, pathLength + 2, " b/", 0, 3) == 0)
        {
            return header.Substring(2, pathLength);
        }

        var marker = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return marker >= 0 ? header[(marker + 3)..] : header;
    }

    private async Task<string> ResolveMergeBaseAsync(string fromRef, string toRef)
    {
        try
        {
            var output = await RunGitCommandAsync($"merge-base {fromRef} {toRef}");
            if (!string.IsNullOrWhiteSpace(output))
                return output.Trim();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GitOperationsTool] merge-base failed for {fromRef}..{toRef}, falling back to fromRef: {ex.Message}");
        }
        return fromRef;
    }

    private async Task<string> RunGitCommandAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return "";
        
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim();
    }
}