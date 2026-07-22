using System.Text.Json.Serialization;

namespace MultiAgentCodeReview.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity
{
    Critical,
    High,
    Medium,
    Low
}

[JsonConverter(typeof(FindingCategoryConverter))]
public enum FindingCategory
{
    SqlInjection,
    Xss,
    BrokenAccessControl,
    SensitiveDataExposure,
    SecurityMisconfiguration,
    WeakCryptography,
    InputValidation,
    DependencyVulnerability,
    Complexity,
    CodeSmell,
    SolidViolation,
    Naming,
    ErrorHandling,
    Testability,
    NPlusOneQuery,
    BlockingAsyncCall,
    MemoryLeak,
    AlgorithmicComplexity,
    MissingCaching,
    ResourceLeak,
    LegacyPattern,
    OutdatedFramework,
    MissingModernLanguageFeatures,
    OutdatedDependencies,
    ArchitectureDebt,
    MissingTests
}

public record Finding(
    Severity Severity,
    FindingCategory Category,
    string File,
    int Line,
    string Description,
    string Recommendation,
    string CodeSnippet,
    FixExample FixExample,
    double Confidence,
    string? QuickFix = null,
    string? Summary = null,
    Dictionary<string, object>? Metrics = null,
    Dictionary<string, object>? Impact = null,
    Dictionary<string, object>? ModernizationContext = null,
    List<string>? References = null,
    List<string>? Agents = null
);

public record FixExample(
    string Before,
    string After
);

public record AgentResult(
    List<Finding> Findings,
    string Summary
);

public record TriageResult(
    List<string> Classifications,
    List<string> RouteTo,
    string Priority,
    string Reasoning
);