using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class SourceControlSecretRedemptionClientTests
{
    private const string OrchestratorAudience = "https://orchestrator.test";
    private const string BrokerAudience = "https://broker-redemption.test";
    private const string Issuer = "https://issuer.test/";
    private const string BrokerOwner = "https://broker.test.local";

    [Fact]
    public async Task UsesDistinctAudienceRunBoundBearerAndInvalidatesCredentialAfterOperation()
    {
        const string secretId = "github-api";
        const string secretVersion = "version-7";
        const string runId = "run-1";
        var secretValue = "ephemeral-secret-value";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                secretId,
                secretVersion,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                value = secretValue,
            }),
        });
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(
            httpClient, Options(), TimeProvider.System);
        var context = CallerContext(includeBrokerAudience: true);
        SecretCredential? usedCredential = null;
        var reference = new SourceControlCredentialReference(
            new SecretRef(secretId, secretVersion), SourceControlSecretPurposes.Api);

        var result = await client.WithCredentialAsync(
            context,
            AcceptedRun(),
            reference,
            (credential, _) =>
            {
                usedCredential = credential;
                Assert.Equal(secretValue, credential.GetValue());
                Assert.True(credential.ExpiresAt > DateTimeOffset.UtcNow);
                return Task.FromResult("operation-complete");
            },
            CancellationToken.None);

        Assert.Equal("operation-complete", result);
        Assert.NotNull(usedCredential);
        Assert.Throws<InvalidOperationException>(() => usedCredential.GetValue());
        Assert.Equal(new Uri(BrokerOwner + "/secrets/redeem"), handler.RequestUri);
        Assert.Equal("Bearer human-issued-multi-audience-token", handler.Authorization);
        Assert.Equal(secretId, handler.RequestBody!.Value.GetProperty("secretId").GetString());
        Assert.Equal(secretVersion, handler.RequestBody.Value.GetProperty("secretVersion").GetString());
        Assert.Equal(SourceControlSecretPurposes.Api,
            handler.RequestBody.Value.GetProperty("purpose").GetString());
        Assert.Equal(runId, handler.RequestBody.Value.GetProperty("runId").GetString());
        Assert.DoesNotContain(secretValue, handler.RequestBody.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMissingBrokerAudienceBeforeSendingBearer()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Request must not be sent."));
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(
            httpClient, Options(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WithCredentialAsync(
                CallerContext(includeBrokerAudience: false),
                AcceptedRun(),
                new SourceControlCredentialReference(
                    new SecretRef("github-api", "version-7"),
                    SourceControlSecretPurposes.Api),
                (_, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RejectsMismatchedBrokerSecretIdentityWithoutReturningCredential()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                secretId = "github-api",
                secretVersion = "wrong-version",
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                value = "must-not-escape",
            }),
        });
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(
            httpClient, Options(), TimeProvider.System);
        var operationCalled = false;

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WithCredentialAsync(
                CallerContext(includeBrokerAudience: true),
                AcceptedRun(),
                new SourceControlCredentialReference(
                    new SecretRef("github-api", "version-7"),
                    SourceControlSecretPurposes.Checkout),
                (_, _) =>
                {
                    operationCalled = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None));

        Assert.Equal("source_control_secret_redemption_contract_invalid", exception.Code);
        Assert.False(operationCalled);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://broker.test.local")]
    [InlineData("https://broker.test.local/path")]
    [InlineData("https://user@broker.test.local")]
    [InlineData("https://broker.test.local/?next=elsewhere")]
    public void RejectsNonOriginOrInsecureBrokerOwner(string owner)
    {
        Assert.Throws<ArgumentException>(() => new SourceControlSecretRedemptionOptions(
            Issuer,
            OrchestratorAudience,
            BrokerAudience,
            owner));
    }

    private static SourceControlSecretRedemptionOptions Options() =>
        new(Issuer, OrchestratorAudience, BrokerAudience, BrokerOwner);

    private static SourceControlAcceptedRunBinding AcceptedRun() =>
        new(
            Issuer,
            "d6e2f560-0144-4b7d-b177-3c1ba289c89e",
            "tenant-1",
            "project-1",
            "run-1",
            "session-1",
            new string('A', 64),
            1,
            2,
            3,
            "context-1",
            4);

    private static DefaultHttpContext CallerContext(bool includeBrokerAudience)
    {
        var actorId = AcceptedRun().Subject;
        var claims = new List<Claim>
        {
            new("sub", actorId),
            new("project_id", "project-1"),
            new("run_id", "run-1"),
            new("aud", OrchestratorAudience),
            new("scope", "api.read projects.orchestrator"),
        };
        if (includeBrokerAudience)
            claims.Add(new Claim("aud", BrokerAudience));

        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer human-issued-multi-audience-token";
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
        return context;
    }

    private static StringContent JsonContent<T>(T value) =>
        new(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Encoding.UTF8,
            "application/json");

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public JsonElement? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
            {
                using var document = JsonDocument.Parse(
                    await request.Content.ReadAsStringAsync(cancellationToken));
                RequestBody = document.RootElement.Clone();
            }
            return respond(request);
        }
    }
}
