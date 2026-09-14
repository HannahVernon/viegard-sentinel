namespace Viegard.Domain.Configuration;

public enum CustomSignatureTarget
{
    HttpUri,
    HttpQuery,
    HttpUserAgent,
    HttpPath,
}

public enum CustomSignatureMatchType
{
    Contains,
    Prefix,
}

public sealed record CustomSignature
{
    public const int MaxNameLength = 128;
    public const int MaxPatternLength = 512;
    public const int MaxCategoryLength = 128;
    public const int MaxUpdatedByLength = 128;
    public const int MinSeverity = 0;
    public const int MaxSeverity = 10;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool Enabled { get; init; }

    public required CustomSignatureTarget Target { get; init; }

    public required CustomSignatureMatchType MatchType { get; init; }

    public required string Pattern { get; init; }

    public required string Category { get; init; }

    public required int Severity { get; init; }

    public double EvidenceWeight { get; init; } = 1.0;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string UpdatedBy { get; init; }

    public required int Version { get; init; }
}

public sealed record CustomSignatureValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CustomSignatureValidationResult Success { get; } = new(true, []);
}

public static class CustomSignatureValidator
{
    public static CustomSignatureValidationResult Validate(CustomSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var errors = new List<string>();

        if (signature.Id == Guid.Empty)
        {
            errors.Add("Signature id is required.");
        }

        ValidateRequiredText(signature.Name, CustomSignature.MaxNameLength, "Name", errors);
        ValidateRequiredText(signature.Pattern, CustomSignature.MaxPatternLength, "Pattern", errors);
        ValidateRequiredText(signature.Category, CustomSignature.MaxCategoryLength, "Category", errors);
        ValidateRequiredText(signature.UpdatedBy, CustomSignature.MaxUpdatedByLength, "Updated by", errors);

        if (!Enum.IsDefined(signature.Target))
        {
            errors.Add("Target is not supported.");
        }

        if (!Enum.IsDefined(signature.MatchType))
        {
            errors.Add("Match type is not supported.");
        }

        if (signature.Severity is < CustomSignature.MinSeverity or > CustomSignature.MaxSeverity)
        {
            errors.Add("Severity must be within 0-10.");
        }

        if (!double.IsFinite(signature.EvidenceWeight) || signature.EvidenceWeight is < 0.0 or > 1.0)
        {
            errors.Add("Evidence weight must be within 0.0-1.0.");
        }

        if (signature.Version < 0)
        {
            errors.Add("Version cannot be negative.");
        }

        return errors.Count == 0 ? CustomSignatureValidationResult.Success : new(false, errors);
    }

    public static CustomSignature Normalize(CustomSignature signature) => signature with
    {
        Name = NormalizeText(signature.Name),
        Pattern = NormalizeText(signature.Pattern),
        Category = NormalizeText(signature.Category),
        UpdatedBy = NormalizeText(signature.UpdatedBy),
    };

    public static string UniformError(CustomSignatureValidationResult result) =>
        result.IsValid ? string.Empty : string.Join(" ", result.Errors);

    private static void ValidateRequiredText(string value, int maxLength, string fieldName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is required.");
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            errors.Add($"{fieldName} must be {maxLength} characters or fewer.");
        }
    }

    private static string NormalizeText(string value) => value.Trim();
}

public static class CustomSignatureSeeds
{
    public const string AftershipReferralBotName = "aftership-referral-bot";

    public static CustomSignature AftershipReferralBot(DateTimeOffset now) => new()
    {
        Id = ViegardId.New(),
        Name = AftershipReferralBotName,
        Enabled = true,
        Target = CustomSignatureTarget.HttpQuery,
        MatchType = CustomSignatureMatchType.Contains,
        Pattern = "ref=aftership",
        Category = "referral-bot",
        Severity = 3,
        EvidenceWeight = 1.0,
        CreatedAt = now.ToUniversalTime(),
        UpdatedAt = now.ToUniversalTime(),
        UpdatedBy = "seed",
        Version = 1,
    };
}
