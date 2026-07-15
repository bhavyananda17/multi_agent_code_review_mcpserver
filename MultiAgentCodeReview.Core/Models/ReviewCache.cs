using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Core.Models;

public class ReviewCache
{
    public PipelineContext? Context { get; set; }
    public AgentResult? SynthesisResult { get; set; }
    public string CommitHash { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
