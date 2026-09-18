using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Tests;

public sealed class AppPasswordRequestsTests
{
    [Theory]
    [InlineData("Bearer viegard_ro_0123456789abcdefsecret", true)]
    [InlineData("bearer viegard_ro_0123456789abcdefsecret", true)]
    [InlineData("Bearer  viegard_ro_0123456789abcdefsecret", true)]
    [InlineData("", false)]
    [InlineData("Basic dXNlcjpwYXNz", false)]
    [InlineData("Bearer some-jwt-token", false)]
    [InlineData("Bearer VIEGARD_RO_0123456789abcdefsecret", false)]
    [InlineData("viegard_ro_0123456789abcdefsecret", false)]
    public void HasAppPasswordBearer_recognizes_only_our_bearer_format(string header, bool expected)
    {
        var context = new DefaultHttpContext();
        if (header.Length > 0)
        {
            context.Request.Headers.Authorization = header;
        }

        Assert.Equal(expected, AppPasswordRequests.HasAppPasswordBearer(context));
    }

    [Fact]
    public void EndpointAcceptsAppPasswords_requires_the_read_only_policy()
    {
        Assert.True(AppPasswordRequests.EndpointAcceptsAppPasswords(
            ContextWithEndpoint(new AuthorizeAttribute { Policy = AppPasswordDefaults.ReadOnlyApiPolicy })));
        Assert.False(AppPasswordRequests.EndpointAcceptsAppPasswords(
            ContextWithEndpoint(new AuthorizeAttribute())));
        Assert.False(AppPasswordRequests.EndpointAcceptsAppPasswords(
            ContextWithEndpoint(new AuthorizeAttribute { Policy = "some-other-policy" })));
        Assert.False(AppPasswordRequests.EndpointAcceptsAppPasswords(ContextWithEndpoint()));
        Assert.False(AppPasswordRequests.EndpointAcceptsAppPasswords(new DefaultHttpContext()));
    }

    private static DefaultHttpContext ContextWithEndpoint(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            null,
            new EndpointMetadataCollection(metadata),
            "test-endpoint"));
        return context;
    }
}
