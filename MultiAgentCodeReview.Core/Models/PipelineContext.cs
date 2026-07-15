using System.Text.Json.Serialization;

namespace MultiAgentCodeReview.Core.Models;

public record PipelineContext(
    string RepositoryPath,
    string CommitHash,
    string? BaseCommit,
    List<ChangedFile> ChangedFiles,
    GitDiff? Diff,
    DependencyGraph? DependencyGraph,
    Dictionary<string, string>? Metadata = null
);

public record ChangedFile(
    string Path,
    int Additions,
    int Deletions,
    ChangeType ChangeType
);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeType
{
    Added,
    Modified,
    Deleted,
    Renamed
}

public record GitDiff(
    string Summary,
    List<FileDiff> Files
);

public record FileDiff(
    string Path,
    string OldPath,
    ChangeType ChangeType,
    List<Hunk> Hunks
);

public record Hunk(
    int OldStart,
    int OldLines,
    int NewStart,
    int NewLines,
    string Content
);

public record DependencyGraph(
    Dictionary<string, List<string>> FileDependencies,
    Dictionary<string, List<string>> ReverseDependencies,
    List<string> EntryPoints
);

public record CodeSnippet(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content,
    double RelevanceScore,
    string Language
);

public record Document(
    string Title,
    string Content,
    string Source,
    double RelevanceScore
);

public record CodePattern(
    string Pattern,
    List<CodeSnippet> Examples,
    string Description
);

public record VulnerabilityInfo(
    string Id,
    string Title,
    string Description,
    string Severity,
    List<string> AffectedVersions,
    string FixedVersion,
    string Reference
);

public record CallSite(
    string FilePath,
    int Line,
    string MethodName,
    string CallingMethod
);

public record CodeSmell(
    string Type,
    string Description,
    string FilePath,
    int Line,
    Severity Severity
);

public record BlameLine(
    int Line,
    string Author,
    DateTime Date,
    string CommitHash,
    string Content
);

public record Commit(
    string Hash,
    string Author,
    DateTime Date,
    string Message,
    List<string> Files
);