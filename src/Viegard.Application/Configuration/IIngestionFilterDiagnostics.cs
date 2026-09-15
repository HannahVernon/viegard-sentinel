namespace Viegard.Application.Configuration;

public interface IIngestionFilterDiagnostics
{
    void InvalidFilterSkipped(IngestionFilter filter, string reason);

    void RefreshFailed(Exception exception);
}

public sealed class NullIngestionFilterDiagnostics : IIngestionFilterDiagnostics
{
    public static NullIngestionFilterDiagnostics Instance { get; } = new();

    private NullIngestionFilterDiagnostics()
    {
    }

    public void InvalidFilterSkipped(IngestionFilter filter, string reason)
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}
