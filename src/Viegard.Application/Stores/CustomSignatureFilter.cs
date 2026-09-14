using Viegard.Domain.Configuration;

namespace Viegard.Application.Stores;

public static class CustomSignatureFilter
{
    public static IReadOnlyList<CustomSignature> Apply(
        IReadOnlyList<CustomSignature> signatures,
        string? text)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        var normalized = ListFilterText.Normalize(text);
        if (normalized is null)
        {
            return signatures;
        }

        return signatures
            .Where(signature =>
                Contains(signature.Name, normalized)
                || Contains(signature.Pattern, normalized)
                || Contains(signature.Category, normalized))
            .ToList();
    }

    private static bool Contains(string value, string filter) =>
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
