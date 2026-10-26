using System.Text.Json;
using MultiAgentCodeReview.Core.Models;

namespace MultiAgentCodeReview.Tests;

public class CategoryConverterTests
{
    private static FindingCategory Read(string json) => JsonSerializer.Deserialize<FindingCategory>(json);

    [Theory]
    [InlineData("\"SQL_INJECTION\"", FindingCategory.SqlInjection)]
    [InlineData("\"n_plus_one_query\"", FindingCategory.NPlusOneQuery)]
    [InlineData("\"Blocking Async Call\"", FindingCategory.BlockingAsyncCall)]
    [InlineData("\"Naming\"", FindingCategory.Naming)]
    public void Known_categories_are_understood_in_several_spellings(string json, FindingCategory expected) =>
        Assert.Equal(expected, Read(json));

    [Theory]
    [InlineData("\"NullReferenceRisk\"")]
    [InlineData("\"some made up category\"")]
    [InlineData("\"\"")]
    public void Unrecognised_categories_become_Other_not_Complexity(string json) =>
        Assert.Equal(FindingCategory.Other, Read(json));

    [Fact]
    public void A_category_survives_a_write_and_read() =>
        Assert.Equal(FindingCategory.Other, Read(JsonSerializer.Serialize(FindingCategory.Other)));
}
