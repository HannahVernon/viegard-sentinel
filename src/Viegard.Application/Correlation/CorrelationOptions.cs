namespace Viegard.Application.Correlation;

public sealed class CorrelationOptions
{
    public const string SectionName = "Viegard:Correlation";

    public TimeSpan WindowDuration { get; set; } = TimeSpan.FromMinutes(10);

    public int MaxEventIdsPerIncident { get; set; } = 500;

    public int MaxEvidenceItemsPerIncident { get; set; } = 200;
}
