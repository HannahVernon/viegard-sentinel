namespace Viegard.Sources.Imap.Tests;

public sealed class ImapSourceOptionsValidatorTests
{
    private readonly ImapSourceOptionsValidator _validator = new();

    private static ImapAccountOptions ValidAccount(string id = "yahoo-primary") => new()
    {
        AccountId = id,
        Host = "imap.example.com",
        Username = "user@example.com",
        PasswordSecretName = "yahoo-primary-password",
    };

    private static ImapSourceOptions Options(params ImapAccountOptions[] accounts)
    {
        var options = new ImapSourceOptions();
        foreach (var account in accounts)
        {
            options.Accounts.Add(account);
        }

        return options;
    }

    [Fact]
    public void Empty_account_list_is_valid()
    {
        Assert.True(_validator.Validate(null, Options()).Succeeded);
    }

    [Fact]
    public void Valid_account_passes()
    {
        Assert.True(_validator.Validate(null, Options(ValidAccount())).Succeeded);
    }

    [Fact]
    public void Duplicate_account_ids_fail()
    {
        var result = _validator.Validate(null, Options(ValidAccount(), ValidAccount()));

        Assert.True(result.Failed);
        Assert.Contains("Duplicate", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_required_fields_fail()
    {
        var account = new ImapAccountOptions();
        var result = _validator.Validate(null, Options(account));

        Assert.True(result.Failed);
    }

    [Fact]
    public void OAuth2_is_rejected_until_implemented()
    {
        var account = ValidAccount();
        account.AuthMechanism = ImapAuthMechanism.OAuth2;

        var result = _validator.Validate(null, Options(account));

        Assert.True(result.Failed);
        Assert.Contains("OAuth2", result.FailureMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Invalid_ports_fail(int port)
    {
        var account = ValidAccount();
        account.Port = port;

        Assert.True(_validator.Validate(null, Options(account)).Failed);
    }

    [Fact]
    public void Empty_folder_list_fails()
    {
        var account = ValidAccount();
        account.Folders.Clear();

        Assert.True(_validator.Validate(null, Options(account)).Failed);
    }

    [Fact]
    public void Default_folder_is_inbox()
    {
        Assert.Equal(["INBOX"], new ImapAccountOptions().Folders);
    }
}
