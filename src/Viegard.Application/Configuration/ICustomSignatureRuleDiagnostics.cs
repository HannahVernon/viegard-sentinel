using Viegard.Domain.Configuration;

namespace Viegard.Application.Configuration;

public interface ICustomSignatureRuleDiagnostics
{
    void InvalidSignatureSkipped(CustomSignature signature, IReadOnlyList<string> errors);

    void RefreshFailed(Exception exception);
}

public sealed class NullCustomSignatureRuleDiagnostics : ICustomSignatureRuleDiagnostics
{
    public static NullCustomSignatureRuleDiagnostics Instance { get; } = new();

    private NullCustomSignatureRuleDiagnostics()
    {
    }

    public void InvalidSignatureSkipped(CustomSignature signature, IReadOnlyList<string> errors)
    {
    }

    public void RefreshFailed(Exception exception)
    {
    }
}
