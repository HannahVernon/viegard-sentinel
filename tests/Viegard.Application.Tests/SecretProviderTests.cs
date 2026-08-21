using Microsoft.Extensions.Configuration;
using Viegard.Application.Secrets;

namespace Viegard.Application.Tests;

public sealed class SecretTests
{
    [Fact]
    public void ToString_reveals_name_but_never_value()
    {
        var secret = new Secret("yahoo-imap-password", "super-sensitive-value");

        Assert.DoesNotContain("super-sensitive-value", secret.ToString(), StringComparison.Ordinal);
        Assert.Contains("yahoo-imap-password", secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_returns_the_value()
    {
        var secret = new Secret("name", "value");
        Assert.Equal("value", secret.Reveal());
    }

    [Fact]
    public void Interpolation_does_not_leak_the_value()
    {
        var secret = new Secret("name", "leaky");
        var message = $"Using secret: {secret}";
        Assert.DoesNotContain("leaky", message, StringComparison.Ordinal);
    }
}

public sealed class FileSecretProviderTests : IDisposable
{
    private readonly string _directory;

    public FileSecretProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"viegard-secret-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Reads_secret_from_file_and_trims_trailing_newline()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "imap-password"), "s3cret\n");
        var provider = new FileSecretProvider(_directory);

        var secret = await provider.GetAsync("imap-password");

        Assert.NotNull(secret);
        Assert.Equal("s3cret", secret.Reveal());
    }

    [Fact]
    public async Task Missing_secret_returns_null()
    {
        var provider = new FileSecretProvider(_directory);
        Assert.Null(await provider.GetAsync("does-not-exist"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("sub/child")]
    [InlineData("sub\\child")]
    [InlineData(".hidden")]
    [InlineData("..")]
    public async Task Path_traversal_names_are_rejected(string name)
    {
        var provider = new FileSecretProvider(_directory);
        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.GetAsync(name));
    }

    [Fact]
    public async Task Interior_dots_are_allowed()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "smtp.example.password"), "v");
        var provider = new FileSecretProvider(_directory);

        var secret = await provider.GetAsync("smtp.example.password");

        Assert.NotNull(secret);
    }
}

public sealed class ConfigurationSecretProviderTests
{
    [Fact]
    public async Task Reads_secret_from_configuration_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Viegard:Secrets:mikrotik-password"] = "config-value",
            })
            .Build();
        var provider = new ConfigurationSecretProvider(configuration);

        var secret = await provider.GetAsync("mikrotik-password");

        Assert.NotNull(secret);
        Assert.Equal("config-value", secret.Reveal());
    }

    [Fact]
    public async Task Missing_secret_returns_null()
    {
        var provider = new ConfigurationSecretProvider(new ConfigurationBuilder().Build());
        Assert.Null(await provider.GetAsync("absent"));
    }
}
