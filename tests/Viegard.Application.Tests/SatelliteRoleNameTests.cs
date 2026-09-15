using Viegard.Application.Configuration;

namespace Viegard.Application.Tests;

public sealed class SatelliteRoleNameTests
{
    [Theory]
    [InlineData("mdaemon01", "viegard_sat_mdaemon01")]
    [InlineData("host_2", "viegard_sat_host_2")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdef", "viegard_sat_abcdefghijklmnopqrstuvwxyzabcdef")]
    public void Valid_names_are_normalized_and_prefixed(string input, string expectedRoleName)
    {
        var ok = SatelliteRoleName.TryNormalize(input, out var name, out var error);

        Assert.True(ok, error);
        Assert.NotNull(name);
        Assert.Equal(expectedRoleName, name.RoleName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("MDaemon")]
    [InlineData("host-name")]
    [InlineData("host.name")]
    [InlineData("x\"; DROP ROLE")]
    [InlineData("abcdefghijklmnopqrstuvwxyzabcdefg")]
    public void Invalid_names_are_rejected(string input)
    {
        var ok = SatelliteRoleName.TryNormalize(input, out var name, out var error);

        Assert.False(ok);
        Assert.Null(name);
        Assert.Equal(SatelliteRoleName.ValidationError, error);
    }

    [Fact]
    public void Existing_prefix_text_is_treated_as_part_of_the_satellite_name()
    {
        var ok = SatelliteRoleName.TryNormalize("viegard_sat_mdaemon01", out var name, out var error);

        Assert.True(ok, error);
        Assert.NotNull(name);
        Assert.Equal("viegard_sat_viegard_sat_mdaemon01", name.RoleName);
    }
}

public sealed class SatelliteRolePasswordTests
{
    [Fact]
    public void Generated_password_uses_expected_length_and_alphabet()
    {
        var password = SatelliteRolePassword.Generate();

        Assert.Equal(SatelliteRolePassword.DefaultLength, password.Length);
        Assert.True(SatelliteRolePassword.UsesAlphabet(password));
    }

    [Fact]
    public void Generated_passwords_are_unique_across_calls()
    {
        var passwords = Enumerable.Range(0, 128)
            .Select(_ => SatelliteRolePassword.Generate())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(128, passwords.Count);
    }

    [Fact]
    public void Generator_rejects_short_lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SatelliteRolePassword.Generate(31));
    }
}
