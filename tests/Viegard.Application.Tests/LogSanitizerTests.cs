using Viegard.Application.Logging;

namespace Viegard.Application.Tests;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain-name.log", "plain-name.log")]
    [InlineData("line1\r\nFAKE: forged entry", "line1  FAKE: forged entry")]
    [InlineData("tab\tand\u001bescape", "tab and escape")]
    [InlineData("nul\0byte", "nul byte")]
    public void Control_characters_are_replaced_with_spaces(string? input, string expected) =>
        Assert.Equal(expected, LogSanitizer.Sanitize(input));

    [Fact]
    public void Unicode_text_passes_through_unchanged()
    {
        const string value = "Boîte de réception 收件箱";
        Assert.Equal(value, LogSanitizer.Sanitize(value));
    }
}
