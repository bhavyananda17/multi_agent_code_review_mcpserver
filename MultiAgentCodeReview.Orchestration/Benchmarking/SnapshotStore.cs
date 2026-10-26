using System.Text.Json;
using System.Text.RegularExpressions;
using MultiAgentCodeReview.Orchestration.Pipeline;

namespace MultiAgentCodeReview.Orchestration.Benchmarking;

/// <summary>
/// Saves and loads the output of the pipeline's generation stages (one JSON file per benchmark row). A replay run
/// reads these instead of calling the specialists again, so judge and grounding settings can be compared on the very
/// same findings, and only the verification stages spend LLM tokens.
/// </summary>
public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string FileFor(string directory, BenchmarkRow row)
    {
        var url = BenchmarkRunner.Redact(row.RepoUrl).TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        var tail = string.Join("-", url.Split('/', ':').Where(p => p.Length > 0).TakeLast(2));
        var slug = Regex.Replace(tail, @"[^A-Za-z0-9_.-]", "-");
        return Path.Combine(directory, $"{slug}-{row.CommitSha[..Math.Min(12, row.CommitSha.Length)]}.json");
    }

    public static void Save(string directory, BenchmarkRow row, GenerationSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FileFor(directory, row), JsonSerializer.Serialize(snapshot, Options));
    }

    public static GenerationSnapshot Load(string directory, BenchmarkRow row)
    {
        var file = FileFor(directory, row);
        if (!File.Exists(file))
            throw new FileNotFoundException($"No recorded generation for this row ({Path.GetFileName(file)}). Run `bench --record {directory}` first.");

        return JsonSerializer.Deserialize<GenerationSnapshot>(File.ReadAllText(file), Options)
               ?? throw new FormatException($"{Path.GetFileName(file)} is not a recorded generation.");
    }
}
