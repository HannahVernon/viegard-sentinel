namespace Viegard.Application.Stores;

public sealed record SearchQuery(IReadOnlyList<SearchGroup> Groups)
{
    public const int MaxTerms = 8;

    public static SearchQuery Empty { get; } = new([]);

    public bool IsEmpty => Groups.Count == 0;

    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Empty;
        }

        var groups = new List<SearchGroup>();
        var include = new List<string>();
        var exclude = new List<string>();
        var pendingNot = false;
        var termCount = 0;

        foreach (var token in Tokenize(text))
        {
            if (token.Text.Length == 0)
            {
                pendingNot = false;
                continue;
            }

            if (!token.IsQuoted
                && !token.IsNegated
                && string.Equals(token.Text, "OR", StringComparison.OrdinalIgnoreCase))
            {
                AddGroup(groups, include, exclude);
                include.Clear();
                exclude.Clear();
                pendingNot = false;
                continue;
            }

            if (!token.IsQuoted
                && !token.IsNegated
                && string.Equals(token.Text, "NOT", StringComparison.OrdinalIgnoreCase))
            {
                pendingNot = true;
                continue;
            }

            if (termCount >= MaxTerms)
            {
                break;
            }

            if (pendingNot || token.IsNegated)
            {
                exclude.Add(token.Text);
            }
            else
            {
                include.Add(token.Text);
            }

            pendingNot = false;
            termCount++;
        }

        AddGroup(groups, include, exclude);
        return groups.Count == 0 ? Empty : new SearchQuery(groups.ToArray());
    }

    private static void AddGroup(
        ICollection<SearchGroup> groups,
        IReadOnlyCollection<string> include,
        IReadOnlyCollection<string> exclude)
    {
        if (include.Count == 0 && exclude.Count == 0)
        {
            return;
        }

        groups.Add(new SearchGroup(include.ToArray(), exclude.ToArray()));
    }

    private static IEnumerable<SearchToken> Tokenize(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= text.Length)
            {
                yield break;
            }

            var negated = false;
            if (text[index] == '-')
            {
                negated = true;
                index++;
            }

            if (index >= text.Length || char.IsWhiteSpace(text[index]))
            {
                yield return new SearchToken(string.Empty, IsQuoted: false, negated);
                continue;
            }

            if (text[index] == '"')
            {
                index++;
                var start = index;
                while (index < text.Length && text[index] != '"')
                {
                    index++;
                }

                var value = text[start..index].Trim();
                if (index < text.Length && text[index] == '"')
                {
                    index++;
                }

                yield return new SearchToken(value, IsQuoted: true, negated);
                continue;
            }

            var termStart = index;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            yield return new SearchToken(text[termStart..index], IsQuoted: false, negated);
        }
    }

    private readonly record struct SearchToken(string Text, bool IsQuoted, bool IsNegated);
}

public sealed record SearchGroup(IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);
