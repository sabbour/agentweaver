using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ModelProviderDraftHttpContractTests
{
    [Theory]
    [InlineData("https://api.openai.com/v1/CaseSensitive", "openai", true)]
    [InlineData("https://api.anthropic.com/v1/messages", "anthropic", true)]
    [InlineData("https://my-resource.openai.azure.com/", "azure", true)]
    [InlineData("https://my-resource.openai.azure.com////", "azure", true)]
    [InlineData("https://bücher.example/v1", "openai", true)]
    [InlineData("https://api.openai.com/v1?api_key=secret", "openai", false)]
    [InlineData("https://user:pass@api.openai.com/v1", "openai", false)]
    [InlineData("https://api.openai.com/v1#fragment", "openai", false)]
    [InlineData("https://api.openai.com:444/v1", "openai", false)]
    [InlineData("https://127.0.0.1/v1", "openai", false)]
    [InlineData("https://vault.azure.net/", "openai", false)]
    [InlineData("https://api.openai.com/v1/deployments", "azure", false)]
    public void PublicUrlValidationUsesTheSharedHostRule(
        string value,
        string providerType,
        bool expected) =>
        Assert.Equal(expected, ModelProviderDraftEndpoints.IsSafePublicUrl(value, providerType));

    [Theory]
    [InlineData("?pageSize=2", 2, null)]
    [InlineData("?afterProviderId=provider-1", 50, "provider-1")]
    [InlineData("?pageSize=100&afterProviderId=p_2", 100, "p_2")]
    [InlineData("?pageSize=0", 0, null)]
    [InlineData("?pageSize=2&pageSize=3", 50, null)]
    [InlineData("?unknown=value", 50, null)]
    [InlineData("?afterProviderId=", 50, null)]
    [InlineData("?afterProviderId=..", 50, null)]
    public void ListQueryAcceptsOnlyBoundedUniqueValues(string query, int pageSize, string? after)
    {
        var request = new DefaultHttpContext().Request;
        request.QueryString = new QueryString(query);
        var valid = ModelProviderDraftEndpoints.TryReadPage(request, out var actualSize, out var actualAfter);
        Assert.Equal(pageSize is > 0 and <= 100 && query is not "?unknown=value" &&
            query is not "?afterProviderId=" and not "?afterProviderId=.." &&
            query is not "?pageSize=2&pageSize=3", valid);
        if (valid)
        {
            Assert.Equal(pageSize, actualSize);
            Assert.Equal(after, actualAfter);
        }
    }

    [Fact]
    public async Task SaveParserRejectsCredentialsUnknownAndDuplicateFields()
    {
        const string prefix =
            "{\"operationId\":\"ad451e41-9b48-412f-99dc-ad6a6cd4080c\",\"expectedRevision\":0,\"name\":\"Model\",\"type\":\"openai\",\"base_url\":\"https://api.openai.com/v1\",\"model\":\"test\"";
        foreach (var property in new[]
        {
            "api_key", "apiKey", "headers", "credentialVersion", "credential_version",
            "vaultUri", "vault_uri", "secretName", "secret_name", "rateCardId", "scope",
            "projectId", "providerId", "state",
        })
            await Assert.ThrowsAsync<JsonException>(() =>
                ParseAsync(prefix + $",\"{property}\":\"do-not-log\"}}"));
        Assert.Null(await ParseAsync(prefix + ",\"name\":\"duplicate\"}"));
    }

    [Fact]
    public async Task ReferenceCreateRequiresCanonicalInstallationPin()
    {
        const string prefix =
            "{\"operationId\":\"ad451e41-9b48-412f-99dc-ad6a6cd4080c\",\"expectedRevision\":0,\"name\":\"Model\",\"type\":\"openai\",\"base_url\":\"https://api.openai.com/v1\",\"model\":\"test\"";
        const string pin = "00112233-4455-4677-8899-aabbccddeeff";
        async Task<ModelProviderDraftSaveBody?> ParsePlatformAsync(string suffix, bool allowReference = true)
        {
            var request = new DefaultHttpContext().Request;
            request.ContentType = "application/json";
            request.Body = new MemoryStream(Encoding.UTF8.GetBytes(prefix + suffix + "}"));
            return await ModelProviderDraftEndpoints.ReadSaveBodyAsync(request, CancellationToken.None, allowReference);
        }
        Assert.NotNull(await ParsePlatformAsync($",\"reference\":\"Ref\",\"targetInstallationId\":\"{pin}\""));
        foreach (var suffix in new[]
        {
            ",\"reference\":\"Ref\"",
            ",\"reference\":\"Ref\",\"targetInstallationId\":null",
            ",\"reference\":\"Ref\",\"targetInstallationId\":\"00000000-0000-0000-0000-000000000000\"",
            ",\"reference\":\"Ref\",\"targetInstallationId\":\"00112233-4455-4677-8899-AABBCCDDEEFF\"",
            ",\"reference\":\"Ref\",\"targetInstallationId\":\"bad\"",
            $",\"targetInstallationId\":\"{pin}\"",
            $",\"reference\":\"Ref\",\"targetInstallationId\":\"{pin}\",\"targetInstallationId\":\"{pin}\"",
        })
            Assert.Null(await ParsePlatformAsync(suffix));
        Assert.Null(await ParsePlatformAsync($",\"reference\":\"Ref\",\"targetInstallationId\":\"{pin}\"", false));
    }

    [Theory]
    [InlineData("{\"operationId\":\"ad451e41-9b48-412f-99dc-ad6a6cd4080c\",\"name\":\"Model\",\"type\":\"openai\",\"base_url\":\"https://api.openai.com/v1\",\"model\":\"test\"}")]
    [InlineData("{\"operationId\":\"ad451e41-9b48-412f-99dc-ad6a6cd4080c\",\"expectedRevision\":null,\"name\":\"Model\",\"type\":\"openai\",\"base_url\":\"https://api.openai.com/v1\",\"model\":\"test\"}")]
    public async Task SaveParserRejectsOmittedOrNullExpectedRevision(string json) =>
        await Assert.ThrowsAsync<JsonException>(() => ParseAsync(json));

    [Fact]
    public async Task SaveParserLeavesEmptyOperationIdForEndpointValidation()
    {
        var body = await ParseAsync(
            """{"operationId":"00000000-0000-0000-0000-000000000000","expectedRevision":0,"name":"Model","type":"openai","base_url":"https://api.openai.com/v1","model":"test"}""");
        Assert.Equal(Guid.Empty, body?.OperationId);
    }

    [Fact]
    public async Task SaveEndpointRejectsEmptyOperationAndInvalidCreateOrUpdateRevision()
    {
        var valid = await ParseAsync(
            """{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":0,"name":"Model","type":"openai","base_url":"https://api.openai.com/v1","model":"test"}""");
        Assert.NotNull(valid);
        var invalidRequests = new[]
        {
            (Body: valid! with { OperationId = Guid.Empty }, Create: true),
            (Body: valid! with { ExpectedRevision = 1 }, Create: true),
            (Body: valid! with { ExpectedRevision = 0 }, Create: false),
        };
        foreach (var (body, create) in invalidRequests)
        {
            var error = Assert.IsAssignableFrom<IResult>(
                ModelProviderDraftEndpoints.ValidateSaveBody(body, create));
            var context = new DefaultHttpContext();
            using var services = new ServiceCollection().AddLogging().AddRouting().BuildServiceProvider();
            context.RequestServices = services;
            context.Response.Body = new MemoryStream();
            await error.ExecuteAsync(context);
            context.Response.Body.Position = 0;
            var responseBody = await new StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
            Assert.Equal("model_provider_draft_invalid_request",
                JsonDocument.Parse(responseBody).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("raw-request-sentinel", responseBody);
            Assert.DoesNotContain("JsonException", responseBody);
            Assert.DoesNotContain("BytePositionInLine", responseBody);
        }

        Assert.Null(ModelProviderDraftEndpoints.ValidateSaveBody(
            valid! with { ExpectedRevision = 0 }, create: true));
        Assert.Null(ModelProviderDraftEndpoints.ValidateSaveBody(
            valid! with { ExpectedRevision = 1 }, create: false));
    }

    [Fact]
    public async Task BodyLimitErrorDoesNotExposeOrLogRequestDetails()
    {
        var logs = new CapturedLogs();
        using var host = await new HostBuilder()
            .ConfigureLogging(logging => logging.AddProvider(logs))
            .ConfigureWebHost(web => web.UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseModelProviderDraftNoStore();
                    app.UseEndpoints(endpoints =>
                        endpoints.MapPost("/api/platform/byok-providers", () => Results.Ok())
                            .WithName("createPlatformByokProviderDraft"));
                }))
            .StartAsync();
        using var client = host.GetTestClient();
        var sentinel = "draft-oversized-sentinel-" + Guid.NewGuid().ToString("N");
        var requestBody = sentinel + new string(
            'x', ModelProviderDraftEndpoints.MaximumSaveBodyBytes + 1 - sentinel.Length);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/byok-providers")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, (int)response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("request_body_too_large",
            JsonDocument.Parse(responseBody).RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(sentinel, responseBody);
        Assert.DoesNotContain(requestBody, responseBody);
        Assert.DoesNotContain("JsonException", responseBody);
        Assert.DoesNotContain("BytePositionInLine", responseBody);
        Assert.DoesNotContain(sentinel, logs.Text);
        Assert.DoesNotContain("raw-request", logs.Text);
        Assert.DoesNotContain("JsonException", logs.Text);
    }

    [Fact]
    public void PublicProjectionDoesNotSerializeBindingReferencesOrAuditFields()
    {
        var binding = new ModelProviderBindingRevision(
            ModelProviderBindingRevision.CurrentContractVersion, "byok-provider", ModelProviderBindingScope.Project,
            "project-a", "Model", "openai", "https://api.openai.com/v1/Case", "deployment", "responses", null,
            ImmutableArray.Create("high"), 1234, 3, "credential-version-secret", "rate-card-secret", "7",
            "configuration-hash");
        var response = ModelProviderDraftEndpoints.Project(new ModelProviderDraft(ModelProviderDraftState.Draft, binding));
        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"has_api_key\":false", json);
        Assert.Contains("\"hasCredential\":true", json);
        Assert.DoesNotContain("credential-version-secret", json);
        Assert.DoesNotContain("rate-card-secret", json);
        Assert.DoesNotContain("CreatedByActorId", json);
        Assert.Equal("Available", ModelProviderDraftEndpoints.Project(
            new ModelProviderDraft(ModelProviderDraftState.Available, binding)).State);
        Assert.Throws<ModelProviderDraftUnsafeMetadataException>(() =>
            ModelProviderDraftEndpoints.Project(new ModelProviderDraft(
                ModelProviderDraftState.Draft,
                binding with { BaseUrl = "https://api.openai.com/v1?api_key=sentinel" })));
    }

    private static Task<ModelProviderDraftSaveBody?> ParseAsync(string json)
    {
        var request = new DefaultHttpContext().Request;
        request.ContentType = "application/json";
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ModelProviderDraftEndpoints.ReadSaveBodyAsync(request, CancellationToken.None);
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly StringBuilder _text = new();
        public string Text => _text.ToString();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(_text);
        public void Dispose() { }

        private sealed class CapturedLogger(StringBuilder text) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                text.AppendLine(formatter(state, exception));
                if (exception is not null)
                    text.AppendLine(exception.ToString());
            }
        }
    }
}
