using System.Net;
using Microsoft.Extensions.Options;
using Viegard.Application.Classifiers;
using Viegard.Application.Configuration;
using Viegard.Application.Inference;
using Viegard.Application.Inference.Prompts;
using Viegard.Inference.Ollama;
using Viegard.Persistence.InMemory;

namespace Viegard.Application.Tests;

public sealed class OllamaInferenceProviderTests
{
    [Fact]
    public async Task Success_posts_chat_request_and_parses_message_content()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"model":"qwen-test:latest","message":{"role":"assistant","content":"{\"classification\":\"x\",\"confidence\":0.7,\"severity\":7,\"reasons\":[\"r\"]}"}}
                """),
        });
        var provider = await CreateProviderAsync(handler);

        var result = await provider.InferAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal("qwen-test:latest", result.ModelId);
        Assert.Contains("\"severity\":7", result.RawOutput, StringComparison.Ordinal);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("/api/chat", handler.LastRequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("Assess the incident and return only the JSON.", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"format\":", handler.LastRequestBody, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Infer_uses_request_template_when_provided_and_static_template_when_null()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"model":"qwen-test:latest","message":{"role":"assistant","content":"{}"}}
                """),
        });
        var provider = await CreateProviderAsync(handler);
        var custom = LocalModelAdvisorPrompt.Template with
        {
            SystemInstructions = "Custom system {output_schema}",
            ApplicationInstructions = "Custom application {base_category}",
        };

        await provider.InferAsync(Request() with { Template = custom });

        Assert.Contains("Custom system schema", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("Custom application path-traversal", handler.LastRequestBody, StringComparison.Ordinal);

        await provider.InferAsync(Request());

        Assert.Contains("local-model security advisor", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_500_returns_unavailable_and_never_throws()
    {
        var provider = await CreateProviderAsync(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Server Error",
        }));

        var result = await provider.InferAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(InferenceFailureKind.Unavailable, result.FailureKind);
    }

    [Fact]
    public async Task Timeout_returns_timeout_and_never_throws()
    {
        var provider = await CreateProviderAsync(new StubHandler(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), timeoutMs: 250);

        var result = await provider.InferAsync(Request(timeout: TimeSpan.FromMilliseconds(250)));

        Assert.False(result.Succeeded);
        Assert.Equal(InferenceFailureKind.Timeout, result.FailureKind);
    }

    private static async Task<OllamaInferenceProvider> CreateProviderAsync(StubHandler handler, int timeoutMs = 8000)
    {
        var store = new InMemoryLocalModelAdvisorSettingsStore();
        await store.UpsertAsync(new LocalModelAdvisorSettings
        {
            Enabled = true,
            Endpoint = "http://ollama.example",
            Model = "qwen-test:latest",
            Temperature = 0,
            TimeoutMs = timeoutMs,
            KeepAlive = "5m",
            InvokeConfidenceMin = 0.5,
            InvokeConfidenceMax = 0.85,
            MaxSeverityDelta = 3,
            MaxConfidenceDelta = 0.2,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "test",
        }, 0, "test", DateTimeOffset.UtcNow);
        var source = new LocalModelAdvisorSource(store);
        await source.RefreshAsync();
        return new OllamaInferenceProvider(
            new HttpClient(handler),
            source,
            Options.Create(new LocalModelAdvisorOptions()),
            new PromptAssembler());
    }

    private static InferenceRequest Request(TimeSpan? timeout = null) => new()
    {
        TemplateId = AdvisoryIncidentClassifier.PromptTemplateId,
        Variables =
        [
            new PromptVariable { Name = "base_category", Value = "path-traversal", Trust = PromptTrust.Application },
            new PromptVariable { Name = "base_severity", Value = "6", Trust = PromptTrust.Application },
            new PromptVariable { Name = "base_confidence", Value = "0.6", Trust = PromptTrust.Application },
            new PromptVariable { Name = "max_severity_delta", Value = "3", Trust = PromptTrust.Application },
            new PromptVariable { Name = "max_confidence_delta", Value = "0.2", Trust = PromptTrust.Application },
            new PromptVariable { Name = "output_schema", Value = "schema", Trust = PromptTrust.System },
            new PromptVariable { Name = "evidence", Value = "hostile request", Trust = PromptTrust.UntrustedObservedData },
        ],
        OutputSchemaId = AdvisoryIncidentClassifier.OutputSchemaId,
        MaxTokens = 256,
        Temperature = 0,
        Timeout = timeout ?? TimeSpan.FromSeconds(8),
    };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public StubHandler(Func<CancellationToken, HttpResponseMessage> responder)
            : this(ct => Task.FromResult(responder(ct)))
        {
        }

        public string? LastRequestBody { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await responder(cancellationToken);
        }
    }
}
