namespace Viegard.Sources.Syslog.Tests;

public sealed class SyslogSourceOptionsValidatorTests
{
    private readonly SyslogSourceOptionsValidator _validator = new();

    private static SyslogSourceOptions Enabled(params string[] sources)
    {
        var options = new SyslogSourceOptions { Enabled = true };
        foreach (var source in sources)
        {
            options.AllowedSources.Add(source);
        }

        return options;
    }

    [Fact]
    public void Disabled_listener_passes_without_allowlist()
    {
        Assert.True(_validator.Validate(null, new SyslogSourceOptions()).Succeeded);
    }

    [Fact]
    public void Enabled_listener_requires_non_empty_allowlist()
    {
        var result = _validator.Validate(null, Enabled());

        Assert.True(result.Failed);
        Assert.Contains("fail-closed", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("192.168.0.0/16")]
    [InlineData("172.16.0.0/12")]
    [InlineData("fc00::/7")]
    [InlineData("2001:db8::7")]
    public void Valid_ip_and_cidr_allowed_sources_pass(string entry)
    {
        Assert.True(_validator.Validate(null, Enabled(entry)).Succeeded);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("192.168.0.0/33")]
    [InlineData("192.168.0.0/-1")]
    [InlineData("192.168.0.0/x")]
    [InlineData("")]
    [InlineData("192.168.0.0/16/24")]
    public void Invalid_allowed_source_entries_fail(string entry)
    {
        Assert.True(_validator.Validate(null, Enabled(entry)).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Invalid_port_fails(int port)
    {
        var options = Enabled("192.0.2.10");
        options.Port = port;

        Assert.True(_validator.Validate(null, options).Failed);
    }

    [Fact]
    public void Invalid_listen_address_fails()
    {
        var options = Enabled("192.0.2.10");
        options.ListenAddress = "not-an-address";

        Assert.True(_validator.Validate(null, options).Failed);
    }
}
