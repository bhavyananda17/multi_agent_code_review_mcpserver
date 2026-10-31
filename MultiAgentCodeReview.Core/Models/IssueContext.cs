namespace MultiAgentCodeReview.Core.Models;

/// <summary>
/// A GitHub issue linked to a review. Optional input: when absent the pipeline behaves
/// exactly as the commit-only flow. Title/Body/Labels are UNTRUSTED text written by
/// whoever filed the issue - agents must treat them as data, never as instructions.
/// </summary>
public record IssueContext(
    string Owner,
    string Repo,
    int Number,
    string Title,
    string Body,
    string Url,
    List<string>? Labels = null
);
