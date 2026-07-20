using System.Diagnostics;
using MultiAgentCodeReview.Core.Interfaces;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Tools;

public class GitOperationsTool : IGitOperationsTool
{
    private readonly string _repoPath;

    public GitOperationsTool(string repoPath = ".")
    {
        _repoPath = repoPath;
    }

    public async Task<GitDiff> GetDiffAsync(string fromRef, string toRef = "HEAD")
    {
        var output = await RunGitCommandAsync($"diff {fromRef} {toRef} --no-color");
        return ParseDiffOutput(output);
    }

    public async Task<List<string>> GetChangedFilesAsync(string fromRef = "HEAD~1", string toRef = "HEAD")
    {
        var output = await RunGitCommandAsync($"diff --name-only {fromRef} {toRef}");
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
        FileDiff? currentFileDiff = null;
        var currentHunks = new List<Hunk>();
        var currentHunkLines = new List<string>();
        int oldStart = 0, oldLines = 0, newStart = 0, newLines = 0;
        bool inHunk = false;

        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("diff --git"))
            {
                if (currentFileDiff != null)
                {
                    currentFileDiff = currentFileDiff with { Hunks = currentHunks };
                    fileDiffs.Add(currentFileDiff);
                }

                var parts = line.Split(' ');
                var file = parts.Length > 3 ? parts[3].TrimStart('a', '/', 'b', '/') : "";
                currentFileDiff = new FileDiff
                (
                    Path: file,
                    OldPath: file,
                    ChangeType: ChangeType.Modified,
                    Hunks: new List<Hunk>()
                );
                currentHunks = new List<Hunk>();
                inHunk = false;
            }
            else if (line.StartsWith("@@"))
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
                    currentHunkLines.Clear();
                }

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
            else if (inHunk)
            {
                currentHunkLines.Add(line);
            }
        }

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

        if (currentFileDiff != null)
        {
            currentFileDiff = currentFileDiff with { Hunks = currentHunks };
            fileDiffs.Add(currentFileDiff);
        }

        var summary = $"{fileDiffs.Count} files changed";
        return new GitDiff(summary, fileDiffs);
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