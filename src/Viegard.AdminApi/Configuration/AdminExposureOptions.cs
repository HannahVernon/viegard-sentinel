using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Viegard.Application.Net;

namespace Viegard.AdminApi.Configuration;

public static class AdminExposureModes
{
    public const string Loopback = "loopback";
    public const string Direct = "direct";
    public const string Proxy = "proxy";

    public static bool IsKnown(string? mode) =>
        string.Equals(mode, Loopback, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Direct, StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, Proxy, StringComparison.OrdinalIgnoreCase);
}

public sealed class AdminExposureOptions
{
    public const string SectionName = "Viegard:Admin";

    public string Exposure { get; set; } = AdminExposureModes.Loopback;

    public AdminTlsOptions Tls { get; set; } = new();

    public AdminProxyOptions Proxy { get; set; } = new();

    public AdminBootstrapOptions Bootstrap { get; set; } = new();
}

public sealed class AdminTlsOptions
{
    public string? CertificatePath { get; set; }

    public string? KeyPath { get; set; }

    public int Port { get; set; } = 8443;
}

public sealed class AdminProxyOptions
{
    public string[] TrustedNetworks { get; set; } = [];
}

public sealed class AdminBootstrapOptions
{
    public string Username { get; set; } = "admin";

    public string PasswordSecretName { get; set; } = "viegard-admin-bootstrap-password";
}

public sealed class AdminExposureOptionsValidator : IValidateOptions<AdminExposureOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminExposureOptions options)
    {
        var failures = new List<string>();
        if (!AdminExposureModes.IsKnown(options.Exposure))
        {
            failures.Add("Admin Exposure must be loopback, direct, or proxy.");
        }

        if (string.Equals(options.Exposure, AdminExposureModes.Direct, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Tls.CertificatePath) || string.IsNullOrWhiteSpace(options.Tls.KeyPath))
            {
                failures.Add("Admin direct exposure requires Tls:CertificatePath and Tls:KeyPath.");
            }

            if (options.Tls.Port is < 1 or > 65535)
            {
                failures.Add("Admin direct exposure TLS port must be within 1-65535.");
            }
        }

        if (string.Equals(options.Exposure, AdminExposureModes.Proxy, StringComparison.OrdinalIgnoreCase)
            && options.Proxy.TrustedNetworks.Length == 0)
        {
            failures.Add("Admin proxy exposure requires at least one Proxy:TrustedNetworks entry.");
        }

        foreach (var trustedNetwork in options.Proxy.TrustedNetworks)
        {
            if (!CidrSet.TryParseEntry(trustedNetwork, out var failure))
            {
                failures.Add($"Admin proxy trusted network '{trustedNetwork}' is invalid: {failure}");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class AdminProxyNetworkParser
{
    public static IPNetwork Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        var slash = trimmed.IndexOf('/');
        var addressPart = slash < 0 ? trimmed : trimmed[..slash];
        if (!IPAddress.TryParse(addressPart, out var address))
        {
            throw new FormatException($"Invalid trusted proxy network '{value}'.");
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var prefix = slash < 0
            ? (normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128)
            : int.Parse(trimmed[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (address.IsIPv4MappedToIPv6 && prefix is >= 96 and <= 128)
        {
            prefix -= 96;
        }

        return new IPNetwork(normalized, prefix);
    }
}
