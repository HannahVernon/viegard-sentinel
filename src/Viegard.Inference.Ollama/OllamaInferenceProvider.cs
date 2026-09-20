using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;
using Viegard.Application.Inference;
using Viegard.Application.Inference.Prompts;
using Viegard.Application.Inference.Validation;

namespace Viegard.Inference.Ollama;

public sealed class OllamaInferenceProvider(
    HttpClient httpClient,
    LocalModelAdvisorSource advisorSource,
    IOptions<LocalModelAdvisorOptions> options,
    PromptAssembler promptAssembler) : IInferenceProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ProviderId => "ollama";

    public async Task<InferenceResult> InferAsync(InferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = advisorSource.CurrentValues(options.Value);
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout ?? TimeSpan.FromMilliseconds(settings.TimeoutMs));

        try
        {
            var prompt = promptAssembler.Assemble(LocalModelAdvisorPrompt.Template, request.Variables);
            var payload = new
            {
                model = settings.Model,
                messages = new[]
                {
                    new { role = "system", content = prompt },
                    new { role = "user", content = "Assess the incident and return only the JSON." },
                },
                stream = false,
                keep_alive = settings.KeepAlive,
                options = new
                {
                    temperature = request.Temperature ?? settings.Temperature,
                    num_predict = request.MaxTokens ?? 256,
                    top_p = 1,
                },
                format = ClassificationOutputSchema,
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await httpClient
                .PostAsync(new Uri(new Uri(settings.Endpoint), "/api/chat"), content, timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return InferenceResult.Failure(
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                        ? InferenceFailureKind.Overloaded
                        : InferenceFailureKind.Unavailable,
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
            }

            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            if (responseBody.Length > ClassificationOutputValidator.MaxOutputLength * 4)
            {
                return InferenceResult.Failure(InferenceFailureKind.ResponseTooLarge, "Ollama response was too large.");
            }

            using var document = JsonDocument.Parse(responseBody, new JsonDocumentOptions { MaxDepth = 16 });
            if (!document.RootElement.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var contentElement)
                || contentElement.ValueKind != JsonValueKind.String)
            {
                return InferenceResult.Failure(InferenceFailureKind.MalformedOutput, "Ollama response did not contain message.content.");
            }

            var rawOutput = contentElement.GetString() ?? string.Empty;
            if (rawOutput.Length > ClassificationOutputValidator.MaxOutputLength)
            {
                return InferenceResult.Failure(InferenceFailureKind.ResponseTooLarge, "Ollama message.content was too large.");
            }

            var modelId = document.RootElement.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : settings.Model;
            stopwatch.Stop();
            return InferenceResult.Success(rawOutput, modelId, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return InferenceResult.Failure(InferenceFailureKind.Cancelled, "Inference was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return InferenceResult.Failure(InferenceFailureKind.Timeout, "Ollama inference timed out.");
        }
        catch (HttpRequestException ex)
        {
            return InferenceResult.Failure(InferenceFailureKind.Unavailable, ex.Message);
        }
        catch (JsonException ex)
        {
            return InferenceResult.Failure(InferenceFailureKind.MalformedOutput, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return InferenceResult.Failure(InferenceFailureKind.Unavailable, ex.Message);
        }
        catch (Exception ex)
        {
            return InferenceResult.Failure(InferenceFailureKind.Unknown, ex.Message);
        }
    }

    private static object ClassificationOutputSchema { get; } = new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "classification", "confidence", "severity", "reasons" },
        properties = new
        {
            classification = new { type = "string" },
            confidence = new { type = "number", minimum = 0, maximum = 1 },
            severity = new { type = "integer", minimum = 0, maximum = 10 },
            reasons = new
            {
                type = "array",
                items = new { type = "string" },
            },
            recommended_action = new { type = new[] { "string", "null" } },
            uncertainty = new { type = new[] { "number", "null" }, minimum = 0, maximum = 1 },
        },
    };
}
