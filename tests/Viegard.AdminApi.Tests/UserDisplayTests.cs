using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Viegard.AdminApi;
using Viegard.AdminApi.Components.Shared;
using Viegard.Domain;
using Viegard.Domain.Admin;
using Viegard.Persistence.InMemory;

namespace Viegard.AdminApi.Tests;

public sealed class UserDisplayTests
{
    [Fact]
    public async Task Format_uses_utc_defaults_with_z_suffix()
    {
        var userId = ViegardId.New();
        var display = CreateDisplay(new InMemoryAdminUserStore(), userId);

        await display.InitializeAsync();

        Assert.Equal(
            "2026-09-14 16:30:00Z",
            display.Format(DateTimeOffset.Parse("2026-09-14T16:30:00Z")));
        Assert.Equal(AdminUserPreferences.DefaultPageSize, display.DefaultPageSize);
    }

    [Fact]
    public void Format_uses_converted_offset_suffix_for_non_utc_zone()
    {
        var fixedZone = TimeZoneInfo.CreateCustomTimeZone(
            "fixed-minus-five",
            TimeSpan.FromHours(-5),
            "Fixed minus five",
            "Fixed minus five");

        var formatted = UserDisplay.Format(DateTimeOffset.Parse("2026-09-14T16:30:00Z"), fixedZone);

        Assert.Equal("2026-09-14 11:30:00-05:00", formatted);
    }

    [Fact]
    public async Task Default_page_size_async_uses_saved_preference()
    {
        var store = new InMemoryAdminUserStore();
        var userId = ViegardId.New();
        await store.SavePreferencesAsync(new AdminUserPreferences
        {
            UserId = userId,
            TimeZoneId = "UTC",
            PageSize = 100,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        var display = CreateDisplay(store, userId);

        Assert.Equal(100, await display.DefaultPageSizeAsync());
    }

    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(49, 50, 1)]
    [InlineData(50, 50, 2)]
    [InlineData(199, 50, 4)]
    public void Pagination_math_computes_page_number(long preceding, int take, long expectedPageNumber) =>
        Assert.Equal(expectedPageNumber, PaginationMath.PageNumber(preceding, take));

    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    public void Pagination_math_computes_total_pages(long totalCount, int take, long expectedPages) =>
        Assert.Equal(expectedPages, PaginationMath.TotalPages(totalCount, take));

    private static UserDisplay CreateDisplay(InMemoryAdminUserStore store, Guid userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            ], "test")),
        };
        return new UserDisplay(new HttpContextAccessor { HttpContext = context }, store, new SilentLogger<UserDisplay>());
    }

    private sealed class SilentLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
