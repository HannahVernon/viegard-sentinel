using Viegard.AdminApi.Components;

namespace Viegard.AdminApi.Tests;

public sealed class AdminTextAgeTests
{
    [Theory]
    [InlineData(-5, "0 s")]
    [InlineData(0, "0 s")]
    [InlineData(0.767854, "0.8 s")]
    [InlineData(9.94, "9.9 s")]
    [InlineData(10, "10 s")]
    [InlineData(42, "42 s")]
    [InlineData(59.9, "59 s")]
    [InlineData(252, "4 m 12 s")]
    [InlineData(3599, "59 m 59 s")]
    [InlineData(3600, "1 h 0 m")]
    public void Age_formats_seconds_and_minutes(double seconds, string expected) =>
        Assert.Equal(expected, AdminText.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Age_formats_hours_and_days()
    {
        Assert.Equal("3 h 24 m", AdminText.Age(new TimeSpan(3, 24, 10)));
        Assert.Equal("2 d 5 h", AdminText.Age(new TimeSpan(2, 5, 30, 0)));
    }
}