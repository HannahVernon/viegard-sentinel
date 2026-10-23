using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Tests;

public sealed class TotpEnrollmentHardeningTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Setup_cookie_keeps_the_secret_server_issued_across_requests()
    {
        var cookie = new TotpSetupCookie(new EphemeralDataProtectionProvider());
        var first = new DefaultHttpContext();
        var secret = cookie.GetOrCreate(first, UserId, () => "JBSWY3DPEHPK3PXP");

        var second = RequestCarrying(first);
        Assert.True(cookie.TryRead(second, UserId, out var read));
        Assert.Equal(secret, read);
        Assert.Equal(secret, cookie.GetOrCreate(second, UserId, () => "AAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void Setup_cookie_is_bound_to_the_user_and_rejects_tampering()
    {
        var cookie = new TotpSetupCookie(new EphemeralDataProtectionProvider());
        var first = new DefaultHttpContext();
        cookie.GetOrCreate(first, UserId, () => "JBSWY3DPEHPK3PXP");

        Assert.False(cookie.TryRead(RequestCarrying(first), Guid.NewGuid(), out _));
        Assert.False(cookie.TryRead(RequestCarrying(first, tamper: true), UserId, out _));
        Assert.False(cookie.TryRead(new DefaultHttpContext(), UserId, out _));
    }

    [Fact]
    public void Setup_cookie_expires()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cookie = new TotpSetupCookie(new EphemeralDataProtectionProvider(), time);
        var first = new DefaultHttpContext();
        cookie.GetOrCreate(first, UserId, () => "JBSWY3DPEHPK3PXP");

        time.Now = time.Now.AddMinutes(16);
        Assert.False(cookie.TryRead(RequestCarrying(first), UserId, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Second_factor_failures_below_threshold_do_not_lock(int failures) =>
        Assert.Null(AdminAuthEndpoints.SecondFactorLockoutUntil(failures, DateTimeOffset.UnixEpoch));

    [Theory]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(8, 8)]
    [InlineData(40, 60)]
    public void Second_factor_failures_back_off_exponentially_up_to_an_hour(int failures, int minutes) =>
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddMinutes(minutes),
            AdminAuthEndpoints.SecondFactorLockoutUntil(failures, DateTimeOffset.UnixEpoch));

    private static DefaultHttpContext RequestCarrying(HttpContext response, bool tamper = false)
    {
        var setCookie = response.Response.Headers.SetCookie.ToString();
        var value = setCookie.Split(';')[0][(setCookie.IndexOf('=') + 1)..];
        if (tamper)
        {
            value = value[..^2] + (value.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");
        }

        var request = new DefaultHttpContext();
        request.Request.Headers.Cookie = $"{AdminCookieNames.TotpSetup}={value}";
        return request;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
