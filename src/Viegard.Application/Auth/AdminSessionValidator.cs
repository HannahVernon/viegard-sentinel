using System.Net;
using Viegard.Domain.Admin;

namespace Viegard.Application.Auth;

public sealed record AdminSessionValidationResult(
    bool IsValid,
    bool ShouldRefreshActivity,
    DateTimeOffset? RefreshedIdleExpiresAt,
    AdminIpBindingDecision? IpBinding);

public static class AdminSessionValidator
{
    public static AdminSessionValidationResult Validate(
        AdminSession? session,
        Guid expectedUserId,
        IPAddress? remoteAddress,
        DateTimeOffset now,
        TimeSpan idleTimeout)
    {
        if (session is null
            || session.UserId != expectedUserId
            || session.RevokedAt is not null
            || session.AbsoluteExpiresAt <= now
            || session.IdleExpiresAt <= now
            || remoteAddress is null)
        {
            return new AdminSessionValidationResult(false, false, null, null);
        }

        var binding = AdminIpBinding.Evaluate(session.IpBindingMode, session.Ip, remoteAddress);
        if (!binding.Allowed)
        {
            return new AdminSessionValidationResult(false, false, null, binding);
        }

        var shouldRefresh = now - session.LastSeenAt >= TimeSpan.FromMinutes(1);
        return new AdminSessionValidationResult(
            true,
            shouldRefresh,
            shouldRefresh ? now.Add(idleTimeout) : null,
            binding);
    }
}
