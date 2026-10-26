using System.Text.Json.Serialization;

namespace MultiAgentCodeReview.Core.Configuration;

public record ModelConfig(
    string Role,
    string Provider,
    string ModelId,
    double Temperature,
    int MaxTokens,
    int RpmLimit,
    int TpmLimit
);

public class PipelineConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.groq.com/openai";
    public Dictionary<string, ModelConfig> Models { get; set; } = new();
    public StageConfig Pipeline { get; set; } = new();
    public FilterConfig Filtering { get; set; } = new();
    public RagConfig Rag { get; set; } = new();
    public OutputConfig Output { get; set; } = new();
    public GitHubConfig GitHub { get; set; } = new();
    public EvaluationConfig Evaluation { get; set; } = new();
}

public class EvaluationConfig
{
    /// <summary>Runs the self-evaluation stage after synthesis (one extra LLM call per batch of findings).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A finding whose total score (3 dimensions x 1-3, max 9) is below this is flagged low-confidence.</summary>
    public int Threshold { get; set; } = 5;

    /// <summary>false: flagged findings go in a separate report section. true: dropped from the report.</summary>
    public bool ExcludeLowConfidence { get; set; } = false;

    /// <summary>Findings scored per LLM call; keeps each prompt inside the free-tier token budget.</summary>
    public int BatchSize { get; set; } = 8;
}

public class GitHubConfig
{
    /// <summary>
    /// Optional. Only needed to read issues from private repos (or to lift the 60 req/hr
    /// unauthenticated rate limit). Sent solely to api.github.com. Never logged.
    /// </summary>
    public string Token { get; set; } = "";
}

public class StageConfig
{
    public List<string> Stages { get; set; } = ["filter", "triage", "specialists", "synthesis", "documentation"];
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxConcurrentAgents { get; set; } = 4;
}

public class FilterConfig
{
    public int MaxFiles { get; set; } = 30;
    public int MinFiles { get; set; } = 5;
    public bool UseDependencyGraph { get; set; } = true;
    public bool IncludeTests { get; set; } = false;
}

public class RagConfig
{
    public bool Enabled { get; set; } = true;
    public string EmbeddingModel { get; set; } = "all-MiniLM-L6-v2";
    public int TopK { get; set; } = 5;
    public List<string> KnowledgeSources { get; set; } = ["codebase", "documentation", "standards", "owasp", "history"];
}

public class OutputConfig
{
    public string Format { get; set; } = "markdown";
    public bool IncludeCodeSnippets { get; set; } = true;
    public bool IncludeFixExamples { get; set; } = true;
    public bool GroupByModule { get; set; } = true;
}