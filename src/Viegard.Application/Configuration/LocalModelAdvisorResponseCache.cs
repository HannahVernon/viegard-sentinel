using System.Security.Cryptography;
using System.Text;
using Viegard.Application.Inference;
using Viegard.Application.Inference.Validation;
using Viegard.Domain;

namespace Viegard.Application.Configuration;

public sealed record LocalModelAdvisorResponseCacheEntry
{
    public Guid Id { get; init; } = ViegardId.New();

    public required string CacheKey { get; init; }

    public required string ModelId { get; init; }

    public required string TemplateVersion { get; init; }

    public required int Severity { get; init; }

    public required double Confidence { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public required DateTimeOffset ExpiresAt { get; init; }

    public ValidatedClassificationOutput ToOutput() => new()
    {
        Category = string.Empty,
        Severity = Severity,
        Confidence = Confidence,
        Reasons = Reasons,
    };

    public static LocalModelAdvisorResponseCacheEntry FromOutput(
        string cacheKey,
        string modelId,
        string templateVersion,
        ValidatedClassificationOutput output,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt) => new()
        {
            CacheKey = cacheKey,
            ModelId = modelId,
            TemplateVersion = templateVersion,
            Severity = output.Severity,
            Confidence = output.Confidence,
            Reasons = output.Reasons,
            CreatedAt = createdAt.ToUniversalTime(),
            ExpiresAt = expiresAt.ToUniversalTime(),
        };
}

public interface ILocalModelAdvisorResponseCacheStore
{
    ValueTask<LocalModelAdvisorResponseCacheEntry?> GetAsync(
        string cacheKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync(
        LocalModelAdvisorResponseCacheEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask<int> PruneExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

public static class LocalModelAdvisorResponseCacheKey
{
    public static string Build(
        string templateVersion,
        string modelId,
        IReadOnlyList<PromptVariable> variables)
    {
        ArgumentNullException.ThrowIfNull(templateVersion);
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(variables);

        var builder = new StringBuilder();
        AppendPart(builder, templateVersion);
        AppendPart(builder, modelId);
        builder.Append(variables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        foreach (var variable in variables)
        {
            AppendPart(builder, variable.Name);
            AppendPart(builder, variable.Trust.ToString());
            AppendPart(builder, variable.Value);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AppendPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}
