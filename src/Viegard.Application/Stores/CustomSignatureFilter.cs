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

        var query = SearchQuery.Parse(normalized);
        if (query.IsEmpty)
        {
            return signatures;
        }

        return signatures
            .Where(signature => Matches(
                query,
                SignatureSearchValues(signature)))
            .ToList();
    }

    private static string[] SignatureSearchValues(CustomSignature signature) =>
    [
        signature.Name,
        signature.Pattern,
        .. (signature.AdditionalPatterns ?? []),
        signature.Category,
    ];

    private static bool Matches(SearchQuery query, params string[] values) =>
        query.Groups.Any(group =>
            group.Include.All(term => values.Any(value => Contains(value, term)))
            && group.Exclude.All(term => values.All(value => !Contains(value, term))));

    private static bool Contains(string value, string filter) =>
        value.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
