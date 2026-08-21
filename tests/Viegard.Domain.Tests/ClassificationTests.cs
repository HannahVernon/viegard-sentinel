using Viegard.Domain.Classifications;

namespace Viegard.Domain.Tests;

public sealed class ClassificationTests
{
    private static Classification Create(double confidence = 0.9, int severity = 5, double? uncertainty = null) => new()
    {
        Id = Guid.NewGuid(),
        SubjectKind = ClassificationSubjectKind.Incident,
        SubjectId = Guid.NewGuid(),
        ClassifierId = "test-classifier",
        Category = "test-category",
        Confidence = confidence,
        Severity = severity,
        Reasons = ["reason one"],
        Uncertainty = uncertainty,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Valid_classification_is_constructed()
    {
        var classification = Create(confidence: 0.97, severity: 8, uncertainty: 0.1);

        Assert.Equal(0.97, classification.Confidence);
        Assert.Equal(8, classification.Severity);
        Assert.Equal(0.1, classification.Uncertainty);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Confidence_outside_unit_interval_is_rejected(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(confidence: confidence));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Severity_outside_range_is_rejected(int severity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(severity: severity));
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void Uncertainty_outside_unit_interval_is_rejected(double uncertainty)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(uncertainty: uncertainty));
    }

    [Fact]
    public void Uncertainty_may_be_null()
    {
        Assert.Null(Create(uncertainty: null).Uncertainty);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.0, 10)]
    public void Boundary_values_are_accepted(double confidence, int severity)
    {
        var classification = Create(confidence: confidence, severity: severity);

        Assert.Equal(confidence, classification.Confidence);
        Assert.Equal(severity, classification.Severity);
    }
}
