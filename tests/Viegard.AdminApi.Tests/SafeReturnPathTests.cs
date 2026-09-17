using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Tests;

public sealed class SafeReturnPathTests
{
    [Theory]
    [InlineData("/signatures", "/signatures")]
    [InlineData("/account", "/account")]
    [InlineData("/account#totp", "/account#totp")]
    [InlineData("/signatures?status=ok", "/signatures?status=ok")]
    [InlineData(null, "/account")]
    [InlineData("", "/account")]
    [InlineData("   ", "/account")]
    [InlineData("https://evil.example/", "/account")]
    [InlineData("//evil.example/phish", "/account")]
    [InlineData("/\\evil.example", "/account")]
    [InlineData("javascript:alert(1)", "/account")]
    [InlineData("signatures", "/account")]
    [InlineData("/x\r\nSet-Cookie: a=b", "/account")]
    public void Return_paths_are_constrained_to_local_relative_paths(string? candidate, string expected) =>
        Assert.Equal(expected, AdminAuthEndpoints.SafeReturnPath(candidate));
}
