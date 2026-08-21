namespace Viegard.Sources.Imap;

/// <summary>Authentication mechanisms for an IMAP account (D-0019).</summary>
public enum ImapAuthMechanism
{
    /// <summary>App password (or server password) via SASL PLAIN/LOGIN over TLS.  Implemented.</summary>
    AppPassword,

    /// <summary>XOAUTH2.  Reserved seam; not yet implemented (D-0019).</summary>
    OAuth2,
}

/// <summary>Configuration for one monitored IMAP account.  One worker instance per account (D-0011).</summary>
public sealed class ImapAccountOptions
{
    /// <summary>Unique identifier for this account (used as SourceId, in offsets, and in audit records).</summary>
    public string AccountId { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    /// <summary>Implicit-TLS IMAP port; 993 everywhere we care about.</summary>
    public int Port { get; set; } = 993;

    public string Username { get; set; } = string.Empty;

    /// <summary>Name of the secret (via ISecretProvider) holding the app password.  Never the password itself.</summary>
    public string PasswordSecretName { get; set; } = string.Empty;

    public ImapAuthMechanism AuthMechanism { get; set; } = ImapAuthMechanism.AppPassword;

    /// <summary>Folders to monitor (D-0021).  Defaults to INBOX.</summary>
    public IList<string> Folders { get; } = ["INBOX"];

    /// <summary>Prefer IMAP IDLE when the server supports it (D-0020).</summary>
    public bool UseIdle { get; set; } = true;

    /// <summary>Poll interval when IDLE is disabled, unsupported, or has fallen back.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Maximum time to stay in one IDLE call before cycling (safety poll; RFC 2177 suggests re-issuing well under 29 minutes).</summary>
    public TimeSpan IdleCycle { get; set; } = TimeSpan.FromMinutes(9);

    /// <summary>Consecutive IDLE failures before this session falls back to polling.</summary>
    public int IdleFailureThreshold { get; set; } = 3;

    /// <summary>Delay before reconnecting after a connection failure.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When false (default), the first run baselines at the current newest
    /// message and ingests only mail arriving afterwards.  When true, the
    /// first run ingests the folder's existing contents too.
    /// </summary>
    public bool IngestExistingOnFirstRun { get; set; }
}

/// <summary>Root configuration for the IMAP source module.</summary>
public sealed class ImapSourceOptions
{
    public const string SectionName = "Viegard:Sources:Imap";

    public IList<ImapAccountOptions> Accounts { get; } = [];
}
