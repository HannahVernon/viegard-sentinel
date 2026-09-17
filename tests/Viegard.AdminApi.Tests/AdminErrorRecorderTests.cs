using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Viegard.AdminApi.Errors;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class AdminErrorRecorderTests
{
    [Fact]
    public async Task Records_the_exception_with_bounded_fields()
    {
        var store = new InMemoryAdminErrorStore();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimsIdentity.DefaultNameClaimType, "hannah")], "test"));
        var oversizedMessage = new string('m', AdminError.MaxMessageLength + 50);
        context.Features.Set<IExceptionHandlerPathFeature>(
            new ExceptionHandlerFeature
            {
                Error = new InvalidOperationException(oversizedMessage, new TimeoutException("inner")),
                Path = "/events",
            });

        await AdminErrorRecorder.TryRecordAsync(context, store, NullLogger.Instance);

        var error = Assert.Single(await store.ListRecentAsync(10));
        Assert.Equal("/events", error.Path);
        Assert.Equal("GET", error.Method);
        Assert.Equal("hannah", error.Username);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.ExceptionType);
        Assert.Equal(AdminError.MaxMessageLength, error.Message.Length);
        Assert.Contains("TimeoutException", error.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_nothing_without_an_exception_feature()
    {
        var store = new InMemoryAdminErrorStore();

        await AdminErrorRecorder.TryRecordAsync(new DefaultHttpContext(), store, NullLogger.Instance);

        Assert.Empty(await store.ListRecentAsync(10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Timeout_detection_walks_the_inner_exception_chain(bool wrapped)
    {
        Exception exception = wrapped
            ? new InvalidOperationException(
                "transient failure",
                new Exception("Exception while reading from stream", new TimeoutException("Timeout during reading attempt")))
            : new InvalidOperationException("something else entirely");

        Assert.Equal(wrapped, AdminQueryTimeout.IsTimeout(exception));
    }
}
