using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Viegard.AdminApi.Auth;

namespace Viegard.AdminApi.Tests;

public sealed class AdminFlashMessagesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_message_round_trips_through_the_protected_cookie()
    {
        using var services = BuildServices();
        var producer = CreateContext(services);

        AdminFlashMessages.Set(producer, status: "Retention settings saved.", timeProvider: new FixedTimeProvider(Now));
        var consumer = CreateConsumerContext(services, producer);
        var flash = AdminFlashMessages.Consume(consumer, new FixedTimeProvider(Now.AddSeconds(5)));

        Assert.NotNull(flash);
        Assert.Equal("Retention settings saved.", flash.Message);
        Assert.False(flash.IsError);
    }

    [Fact]
    public void Error_message_round_trips_with_the_error_flag()
    {
        using var services = BuildServices();
        var producer = CreateContext(services);

        AdminFlashMessages.Set(producer, error: "Step-up verification is required.", timeProvider: new FixedTimeProvider(Now));
        var consumer = CreateConsumerContext(services, producer);
        var flash = AdminFlashMessages.Consume(consumer, new FixedTimeProvider(Now.AddSeconds(5)));

        Assert.NotNull(flash);
        Assert.Equal("Step-up verification is required.", flash.Message);
        Assert.True(flash.IsError);
    }

    [Fact]
    public void Status_takes_precedence_over_error()
    {
        using var services = BuildServices();
        var producer = CreateContext(services);

        AdminFlashMessages.Set(producer, status: "Saved.", error: "Ignored.", timeProvider: new FixedTimeProvider(Now));
        var consumer = CreateConsumerContext(services, producer);
        var flash = AdminFlashMessages.Consume(consumer, new FixedTimeProvider(Now));

        Assert.NotNull(flash);
        Assert.Equal("Saved.", flash.Message);
        Assert.False(flash.IsError);
    }

    [Fact]
    public void Tampered_cookie_is_rejected()
    {
        using var services = BuildServices();
        var consumer = CreateContext(services);
        consumer.Request.Headers.Cookie = $"{AdminFlashMessages.CookieName}=not-a-protected-payload";

        Assert.Null(AdminFlashMessages.Consume(consumer));
    }

    [Fact]
    public void Expired_message_is_rejected()
    {
        using var services = BuildServices();
        var producer = CreateContext(services);

        AdminFlashMessages.Set(producer, status: "Saved.", timeProvider: new FixedTimeProvider(Now));
        var consumer = CreateConsumerContext(services, producer);
        var flash = AdminFlashMessages.Consume(
            consumer,
            new FixedTimeProvider(Now.Add(AdminFlashMessages.MaxAge).AddSeconds(1)));

        Assert.Null(flash);
    }

    [Fact]
    public void Consume_deletes_the_cookie()
    {
        using var services = BuildServices();
        var producer = CreateContext(services);

        AdminFlashMessages.Set(producer, status: "Saved.", timeProvider: new FixedTimeProvider(Now));
        var consumer = CreateConsumerContext(services, producer);
        AdminFlashMessages.Consume(consumer, new FixedTimeProvider(Now));

        var deletion = consumer.Response.Headers.SetCookie.ToString();
        Assert.Contains(AdminFlashMessages.CookieName + "=", deletion, StringComparison.Ordinal);
        Assert.Contains("expires=", deletion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_cookie_yields_no_message_and_no_deletion()
    {
        using var services = BuildServices();
        var consumer = CreateContext(services);

        Assert.Null(AdminFlashMessages.Consume(consumer));
        Assert.True(string.IsNullOrEmpty(consumer.Response.Headers.SetCookie.ToString()));
    }

    [Fact]
    public async Task WithFlash_writes_the_cookie_when_the_result_executes()
    {
        using var services = BuildServices();
        var context = CreateContext(services);
        context.Response.Body = Stream.Null;

        var result = Results.Redirect("/configuration#thresholds").WithFlash(status: "Thresholds saved.");
        await result.ExecuteAsync(context);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("/configuration#thresholds", context.Response.Headers.Location.ToString());
        Assert.Contains(AdminFlashMessages.CookieName + "=", context.Response.Headers.SetCookie.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithFlash_without_a_message_returns_the_inner_result_unchanged()
    {
        using var services = BuildServices();
        var context = CreateContext(services);
        context.Response.Body = Stream.Null;

        var inner = Results.Redirect("/bans");
        var result = inner.WithFlash();
        Assert.Same(inner, result);

        await result.ExecuteAsync(context);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.SetCookie.ToString()));
    }

    private static ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider())
            .BuildServiceProvider();

    private static DefaultHttpContext CreateContext(ServiceProvider services) =>
        new() { RequestServices = services };

    private static DefaultHttpContext CreateConsumerContext(ServiceProvider services, HttpContext producer)
    {
        var setCookie = producer.Response.Headers.SetCookie
            .FirstOrDefault(value => value?.StartsWith(AdminFlashMessages.CookieName + "=", StringComparison.Ordinal) == true);
        Assert.NotNull(setCookie);
        var consumer = CreateContext(services);
        consumer.Request.Headers.Cookie = setCookie.Split(';')[0];
        return consumer;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
