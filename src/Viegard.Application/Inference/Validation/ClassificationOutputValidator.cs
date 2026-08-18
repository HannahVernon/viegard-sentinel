using System.Text.Json;

namespace Viegard.Application.Inference.Validation;

/// <summary>Successfully validated classification fields from model output.</summary>
public sealed record ValidatedClassificationOutput
{
    public required string Category { get; init; }

    public required double Confidence { get; init; }

    public required int Severity { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public string? RecommendedAction { get; init; }

    public double? Uncertainty { get; init; }
}

/// <summary>Result of validating raw model output; failure carries reasons, never partial data.</summary>
public sealed record ValidationOutcome
{
    public required bool Succeeded { get; init; }

    public ValidatedClassificationOutput? Output { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public static ValidationOutcome Success(ValidatedClassificationOutput output) =>
        new() { Succeeded = true, Output = output };

    public static ValidationOutcome Failure(params string[] errors) =>
        new() { Succeeded = false, Errors = errors };
}

/// <summary>
/// Strict validator for AI classification output (Section 4/9 requirements:
/// free-form model output must never trigger actions; only schema-validated
/// output reaches the policy engine).  Deliberately hand-rolled on
/// System.Text.Json: no third-party schema dependency, fail-closed on
/// anything unexpected.
///
/// Accepted shape (all other top-level properties are rejected):
/// {
///   "classification": string (required, non-empty),
///   "confidence": number in [0,1] (required),
///   "severity": integer in [0,10] (required),
///   "reasons": array of non-empty strings (required, may be empty array),
///   "recommended_action": string (optional),
///   "uncertainty": number in [0,1] (optional)
/// }
/// </summary>
public sealed class ClassificationOutputValidator
{
    /// <summary>Upper bound on raw model output; oversized responses are rejected outright.</summary>
    public const int MaxOutputLength = 65_536;

    private static readonly string[] KnownProperties =
    [
        "classification", "confidence", "severity", "reasons", "recommended_action", "uncertainty",
    ];

    public ValidationOutcome Validate(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return ValidationOutcome.Failure("Model output is empty.");
        }

        if (rawOutput.Length > MaxOutputLength)
        {
            return ValidationOutcome.Failure($"Model output exceeds {MaxOutputLength} characters.");
        }

        var json = StripCodeFence(rawOutput.Trim());

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            return ValidationOutcome.Failure($"Model output is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ValidationOutcome.Failure("Model output is not a JSON object.");
            }

            var errors = new List<string>();

            foreach (var property in root.EnumerateObject())
            {
                if (!KnownProperties.Contains(property.Name, StringComparer.Ordinal))
                {
                    errors.Add($"Unexpected property '{property.Name}'.");
                }
            }

            var category = RequireString(root, "classification", errors);
            var confidence = RequireNumber(root, "confidence", 0.0, 1.0, errors);
            var severity = RequireInteger(root, "severity", 0, 10, errors);
            var reasons = RequireStringArray(root, "reasons", errors);
            var recommendedAction = OptionalString(root, "recommended_action", errors);
            var uncertainty = OptionalNumber(root, "uncertainty", 0.0, 1.0, errors);

            if (errors.Count > 0)
            {
                return ValidationOutcome.Failure([.. errors]);
            }

            return ValidationOutcome.Success(new ValidatedClassificationOutput
            {
                Category = category!,
                Confidence = confidence!.Value,
                Severity = severity!.Value,
                Reasons = reasons!,
                RecommendedAction = recommendedAction,
                Uncertainty = uncertainty,
            });
        }
    }

    private static string StripCodeFence(string text)
    {
        // Models frequently wrap JSON in Markdown fences; accept that one
        // cosmetic variation and nothing else.
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                return text[(firstNewline + 1)..lastFence].Trim();
            }
        }

        return text;
    }

    private static string? RequireString(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            errors.Add($"Missing required property '{name}'.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            errors.Add($"Property '{name}' must be a non-empty string.");
            return null;
        }

        return element.GetString();
    }

    private static string? OptionalString(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add($"Property '{name}' must be a string when present.");
            return null;
        }

        return element.GetString();
    }

    private static double? RequireNumber(JsonElement root, string name, double min, double max, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            errors.Add($"Missing required property '{name}'.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || double.IsNaN(value))
        {
            errors.Add($"Property '{name}' must be a number.");
            return null;
        }

        if (value < min || value > max)
        {
            errors.Add($"Property '{name}' must be within [{min}, {max}].");
            return null;
        }

        return value;
    }

    private static double? OptionalNumber(JsonElement root, string name, double min, double max, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var value) || double.IsNaN(value))
        {
            errors.Add($"Property '{name}' must be a number when present.");
            return null;
        }

        if (value < min || value > max)
        {
            errors.Add($"Property '{name}' must be within [{min}, {max}].");
            return null;
        }

        return value;
    }

    private static int? RequireInteger(JsonElement root, string name, int min, int max, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            errors.Add($"Missing required property '{name}'.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
        {
            errors.Add($"Property '{name}' must be an integer.");
            return null;
        }

        if (value < min || value > max)
        {
            errors.Add($"Property '{name}' must be within [{min}, {max}].");
            return null;
        }

        return value;
    }

    private static IReadOnlyList<string>? RequireStringArray(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            errors.Add($"Missing required property '{name}'.");
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"Property '{name}' must be an array of strings.");
            return null;
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                errors.Add($"Property '{name}' must contain only non-empty strings.");
                return null;
            }

            values.Add(item.GetString()!);
        }

        return values;
    }
}
