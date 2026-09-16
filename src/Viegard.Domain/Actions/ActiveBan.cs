using System.Net;

namespace Viegard.Domain.Actions;

public sealed record ActiveBan
{
    private string _ip = string.Empty;

    public Guid Id { get; init; } = ViegardId.New();

    public required string Ip
    {
        get => _ip;
        init => _ip = CanonicalizeIp(value);
    }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required Guid DecisionId { get; init; }

    public required Guid ActionId { get; init; }

    public static string CanonicalizeIp(string value) =>
        IPAddress.Parse(value.Trim()).ToString();

    public static ActiveBan NormalizeForSave(ActiveBan activeBan)
    {
        ArgumentNullException.ThrowIfNull(activeBan);

        if (activeBan.Id == Guid.Empty)
        {
            throw new InvalidOperationException("Active ban id is required.");
        }

        if (activeBan.DecisionId == Guid.Empty)
        {
            throw new InvalidOperationException("Active ban decision id is required.");
        }

        if (activeBan.ActionId == Guid.Empty)
        {
            throw new InvalidOperationException("Active ban action id is required.");
        }

        if (activeBan.CreatedAt == default || activeBan.ExpiresAt == default)
        {
            throw new InvalidOperationException("Active ban timestamps are required.");
        }

        return activeBan with
        {
            Ip = CanonicalizeIp(activeBan.Ip),
            CreatedAt = activeBan.CreatedAt.ToUniversalTime(),
            ExpiresAt = activeBan.ExpiresAt.ToUniversalTime(),
        };
    }
}
