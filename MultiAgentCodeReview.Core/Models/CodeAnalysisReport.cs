namespace MultiAgentCodeReview.Core.Models;

public record MethodComplexity(string FilePath, string MethodName, int StartLine, int Complexity);

/// <summary>Callers of one method found by the Roslyn pass. Only callers in the SAME file are detected.</summary>
public record MethodCallSites(string FilePath, string MethodName, int StartLine, List<CallSite> Callers);

/// <summary>
/// Roslyn facts about the methods a change touches, gathered once in the Filter stage and handed to the
/// specialist agents alongside the diff. Measured, not LLM-estimated.
/// </summary>
public record CodeAnalysisReport(
    List<MethodComplexity> Complexity,
    List<MethodCallSites> CallSites,
    List<CodeSmell> CodeSmells,
    List<string>? Notes = null);
