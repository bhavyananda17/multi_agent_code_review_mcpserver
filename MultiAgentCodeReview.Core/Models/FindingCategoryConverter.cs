using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAgentCodeReview.Core.Models;

public class FindingCategoryConverter : JsonConverter<FindingCategory>
{
    private static readonly Dictionary<string, FindingCategory> _mapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sqlinjection"] = FindingCategory.SqlInjection,
        ["sql_injection"] = FindingCategory.SqlInjection,
        ["xss"] = FindingCategory.Xss,
        ["crosssitescripting"] = FindingCategory.Xss,
        ["brokenaccesscontrol"] = FindingCategory.BrokenAccessControl,
        ["broken_access_control"] = FindingCategory.BrokenAccessControl,
        ["sensitivedataexposure"] = FindingCategory.SensitiveDataExposure,
        ["sensitive_data_exposure"] = FindingCategory.SensitiveDataExposure,
        ["securitymisconfiguration"] = FindingCategory.SecurityMisconfiguration,
        ["security_misconfiguration"] = FindingCategory.SecurityMisconfiguration,
        ["weakcryptography"] = FindingCategory.WeakCryptography,
        ["weak_cryptography"] = FindingCategory.WeakCryptography,
        ["inputvalidation"] = FindingCategory.InputValidation,
        ["input_validation"] = FindingCategory.InputValidation,
        ["dependencyvulnerability"] = FindingCategory.DependencyVulnerability,
        ["dependency_vulnerability"] = FindingCategory.DependencyVulnerability,
        ["complexity"] = FindingCategory.Complexity,
        ["codesmell"] = FindingCategory.CodeSmell,
        ["code_smell"] = FindingCategory.CodeSmell,
        ["solidviolation"] = FindingCategory.SolidViolation,
        ["solid_violation"] = FindingCategory.SolidViolation,
        ["naming"] = FindingCategory.Naming,
        ["errorhandling"] = FindingCategory.ErrorHandling,
        ["error_handling"] = FindingCategory.ErrorHandling,
        ["testability"] = FindingCategory.Testability,
        ["nplusonequery"] = FindingCategory.NPlusOneQuery,
        ["n_plus_one_query"] = FindingCategory.NPlusOneQuery,
        ["blockingasynccall"] = FindingCategory.BlockingAsyncCall,
        ["blocking_async_call"] = FindingCategory.BlockingAsyncCall,
        ["memoryleak"] = FindingCategory.MemoryLeak,
        ["memory_leak"] = FindingCategory.MemoryLeak,
        ["algorithmiccomplexity"] = FindingCategory.AlgorithmicComplexity,
        ["algorithmic_complexity"] = FindingCategory.AlgorithmicComplexity,
        ["missingcaching"] = FindingCategory.MissingCaching,
        ["missing_caching"] = FindingCategory.MissingCaching,
        ["resourceleak"] = FindingCategory.ResourceLeak,
        ["resource_leak"] = FindingCategory.ResourceLeak,
        ["legacypattern"] = FindingCategory.LegacyPattern,
        ["legacy_pattern"] = FindingCategory.LegacyPattern,
        ["outdatedframework"] = FindingCategory.OutdatedFramework,
        ["outdated_framework"] = FindingCategory.OutdatedFramework,
        ["missingmodernlanguagefeatures"] = FindingCategory.MissingModernLanguageFeatures,
        ["missing_modern_language_features"] = FindingCategory.MissingModernLanguageFeatures,
        ["outdateddependencies"] = FindingCategory.OutdatedDependencies,
        ["outdated_dependencies"] = FindingCategory.OutdatedDependencies,
        ["architecturedebt"] = FindingCategory.ArchitectureDebt,
        ["architecture_debt"] = FindingCategory.ArchitectureDebt,
        ["missingtests"] = FindingCategory.MissingTests,
        ["missing_tests"] = FindingCategory.MissingTests
    };

    public override FindingCategory Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrEmpty(value))
            return FindingCategory.Complexity;

        if (_mapping.TryGetValue(value.Replace(" ", "_").ToLowerInvariant(), out var category))
            return category;

        if (Enum.TryParse<FindingCategory>(value, true, out var parsed))
            return parsed;

        return FindingCategory.Complexity;
    }

    public override void Write(Utf8JsonWriter writer, FindingCategory value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}