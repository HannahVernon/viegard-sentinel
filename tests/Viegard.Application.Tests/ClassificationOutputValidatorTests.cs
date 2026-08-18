using Viegard.Application.Inference.Validation;

namespace Viegard.Application.Tests;

public sealed class ClassificationOutputValidatorTests
{
    private readonly ClassificationOutputValidator _validator = new();

    private const string ValidJson = """
        {
          "classification": "spam",
          "confidence": 0.97,
          "severity": 8,
          "reasons": ["Known phishing pattern", "Suspicious link"],
          "recommended_action": "move_to_spam",
          "uncertainty": 0.05
        }
        """;

    [Fact]
    public void Valid_output_is_accepted()
    {
        var outcome = _validator.Validate(ValidJson);

        Assert.True(outcome.Succeeded);
        Assert.Equal("spam", outcome.Output!.Category);
        Assert.Equal(0.97, outcome.Output.Confidence);
        Assert.Equal(8, outcome.Output.Severity);
        Assert.Equal(2, outcome.Output.Reasons.Count);
        Assert.Equal("move_to_spam", outcome.Output.RecommendedAction);
        Assert.Equal(0.05, outcome.Output.Uncertainty);
    }

    [Fact]
    public void Code_fenced_json_is_accepted()
    {
        var outcome = _validator.Validate($"```json\n{ValidJson}\n```");
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void Optional_fields_may_be_absent()
    {
        var outcome = _validator.Validate("""
            { "classification": "ham", "confidence": 0.6, "severity": 0, "reasons": [] }
            """);

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Output!.RecommendedAction);
        Assert.Null(outcome.Output.Uncertainty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_output_is_rejected(string? raw)
    {
        Assert.False(_validator.Validate(raw).Succeeded);
    }

    [Fact]
    public void Natural_language_output_is_rejected()
    {
        var outcome = _validator.Validate("This email is definitely spam, you should delete it!");
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Truncated_json_is_rejected()
    {
        var outcome = _validator.Validate("""{ "classification": "spam", "confidence": 0.9, """);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Unknown_top_level_properties_are_rejected()
    {
        var outcome = _validator.Validate("""
            { "classification": "spam", "confidence": 0.9, "severity": 5, "reasons": [],
              "execute_command": "rm -rf /" }
            """);

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Errors, e => e.Contains("execute_command", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("-0.1")]
    [InlineData("1.1")]
    [InlineData("\"high\"")]
    public void Invalid_confidence_is_rejected(string confidence)
    {
        var outcome = _validator.Validate($$"""
            { "classification": "spam", "confidence": {{confidence}}, "severity": 5, "reasons": [] }
            """);

        Assert.False(outcome.Succeeded);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("3.7")]
    [InlineData("\"severe\"")]
    public void Invalid_severity_is_rejected(string severity)
    {
        var outcome = _validator.Validate($$"""
            { "classification": "spam", "confidence": 0.9, "severity": {{severity}}, "reasons": [] }
            """);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Missing_required_fields_are_all_reported()
    {
        var outcome = _validator.Validate("{}");

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Errors, e => e.Contains("classification", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.Contains("confidence", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.Contains("severity", StringComparison.Ordinal));
        Assert.Contains(outcome.Errors, e => e.Contains("reasons", StringComparison.Ordinal));
    }

    [Fact]
    public void Reasons_with_non_string_entries_are_rejected()
    {
        var outcome = _validator.Validate("""
            { "classification": "spam", "confidence": 0.9, "severity": 5, "reasons": ["ok", 42] }
            """);

        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void Oversized_output_is_rejected()
    {
        var huge = "{ \"classification\": \"" + new string('a', ClassificationOutputValidator.MaxOutputLength) + "\" }";
        var outcome = _validator.Validate(huge);

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Errors, e => e.Contains("exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void Json_array_is_rejected()
    {
        Assert.False(_validator.Validate("[1, 2, 3]").Succeeded);
    }

    [Fact]
    public void Failure_never_carries_partial_output()
    {
        var outcome = _validator.Validate("""
            { "classification": "spam", "confidence": 2.0, "severity": 5, "reasons": [] }
            """);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Output);
    }
}
