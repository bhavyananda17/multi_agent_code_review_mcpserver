using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Orchestration.Pipeline;

public record StageResult(
    bool Success,
    string StageName,
    object? Output = null,
    string? Error = null
);