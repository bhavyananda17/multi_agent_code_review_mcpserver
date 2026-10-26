namespace MultiAgentCodeReview.Core.Models;

public static class FindingExtensions
{
    /// <summary>
    /// True when a verification stage cast doubt on the finding: the self-evaluation judge scored it below the
    /// threshold, or the code it quotes could not be found in the commit. Flagged findings are kept (never
    /// silently dropped) but listed separately and left out of the health score.
    /// </summary>
    public static bool IsFlagged(this Finding finding) =>
        finding.Evaluation?.LowConfidence == true || finding.Grounded == false;
}
