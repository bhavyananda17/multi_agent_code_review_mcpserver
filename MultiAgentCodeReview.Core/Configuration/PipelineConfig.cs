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