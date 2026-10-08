using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
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

    [Fact]
    public async Task UsesSelectionCodeOnceThenReusablePinnedBindingForApiAndCheckoutOperations()
    {
        var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
        var time = new FrozenTimeProvider(now);
        var selectionCode = new string('b', 64);
        var selectionHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(selectionCode)));
        var permissionDigest = new string('a', 64);
        var requestBodies = new List<JsonElement>();
        var handler = new AppTokenRecordingHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "/internal/source-control/github-app/installations/token",
                request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            requestBodies.Add(body.RootElement.Clone());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    accessToken = "ephemeral-installation-token",
                    expiresAt = now.AddMinutes(47),
                    connectionId = "connection-1",
                    connectionRevision = 4,
                    installationId = 456,
                    repositoryId = 789,
                    repositoryFullName = "octo/widget",
                    defaultBranch = "main",
                    isPrivate = true,
                    permissionDigest,
                    issueWriteGranted = true,
                    selectionHash
                })
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(
            httpClient, Options(), time);
        var acceptedRun = AcceptedRun();
        GitHubAppInstallationBindingMetadata? selectedBinding = null;
        SecretCredential? selectionCredential = null;

        var negotiated = await client.WithGitHubAppSelectionCredentialAsync(
            CallerContext(includeBrokerAudience: true),
            acceptedRun,
            "connection-1",
            selectionCode,
            "octo/widget",
            issueWriteRequested: true,
            (credential, binding, _) =>
            {
                selectionCredential = credential;
                selectedBinding = binding;
                Assert.Equal("ephemeral-installation-token", credential.GetValue());
                Assert.Equal(selectionHash, binding.SelectionHash);
                Assert.Equal(456, binding.InstallationId);
                return Task.FromResult("repository-negotiated");
            },
            CancellationToken.None);

        Assert.Equal("repository-negotiated", negotiated);
        Assert.NotNull(selectionCredential);
        Assert.Throws<InvalidOperationException>(() => selectionCredential.GetValue());
        var selected = Assert.IsType<GitHubAppInstallationBindingMetadata>(selectedBinding);
        var selectionRequest = requestBodies[0];
        Assert.Equal(selectionCode, selectionRequest.GetProperty("selectionCode").GetString());
        Assert.Null(selectionRequest.GetProperty("selectionHash").GetString());
        Assert.Equal("connection-1", selectionRequest.GetProperty("connectionId").GetString());
        Assert.Equal(JsonValueKind.Null, selectionRequest.GetProperty("connectionRevision").ValueKind);
        Assert.Equal(JsonValueKind.Null, selectionRequest.GetProperty("repositoryId").ValueKind);
        Assert.DoesNotContain("ephemeral-installation-token", selectionRequest.GetRawText(), StringComparison.Ordinal);

        var binding = new SourceControlGitHubAppBinding(
            selected.ConnectionId,
            selected.ConnectionRevision,
            selected.InstallationId,
            selected.PermissionDigest,
            selected.SelectionHash,
            selected.IssueWriteGranted);
        SecretCredential? apiCredential = null;
        var apiResult = await client.WithGitHubAppCredentialAsync(
            CallerContext(includeBrokerAudience: true),
            acceptedRun,
            binding,
            789,
            "octo/widget",
            (credential, metadata, _) =>
            {
                apiCredential = credential;
                Assert.Equal("ephemeral-installation-token", credential.GetValue());
                Assert.Equal("main", metadata.DefaultBranch);
                return Task.FromResult("api-request-complete");
            },
            CancellationToken.None);
        Assert.Equal("api-request-complete", apiResult);
        Assert.NotNull(apiCredential);
        Assert.Throws<InvalidOperationException>(() => apiCredential.GetValue());

        SecretCredential? checkoutCredential = null;
        var checkoutResult = await client.WithGitHubAppCredentialAsync(
            CallerContext(includeBrokerAudience: true),
            acceptedRun,
            binding,
            789,
            "octo/widget",
            (credential, metadata, _) =>
            {
                checkoutCredential = credential;
                Assert.Equal("ephemeral-installation-token", credential.GetValue());
                Assert.Equal(789, metadata.RepositoryId);
                return Task.FromResult("checkout-complete");
            },
            CancellationToken.None);
        Assert.Equal("checkout-complete", checkoutResult);
        Assert.NotNull(checkoutCredential);
        Assert.Throws<InvalidOperationException>(() => checkoutCredential.GetValue());

        Assert.Equal(3, requestBodies.Count);
        foreach (var pinnedRequest in requestBodies.Skip(1))
        {
            Assert.Null(pinnedRequest.GetProperty("selectionCode").GetString());
            Assert.Equal(selectionHash, pinnedRequest.GetProperty("selectionHash").GetString());
            Assert.Equal("connection-1", pinnedRequest.GetProperty("connectionId").GetString());
            Assert.Equal(4L, pinnedRequest.GetProperty("connectionRevision").GetInt64());
            Assert.Equal(456L, pinnedRequest.GetProperty("installationId").GetInt64());
            Assert.Equal(789L, pinnedRequest.GetProperty("repositoryId").GetInt64());
            Assert.Equal(permissionDigest, pinnedRequest.GetProperty("permissionDigest").GetString());
            Assert.True(pinnedRequest.GetProperty("issueWriteRequested").GetBoolean());
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "rotation_uncertain", StatusCodes.Status409Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "refresh_in_progress", StatusCodes.Status503ServiceUnavailable)]
    [InlineData(HttpStatusCode.Conflict, "permissions_changed", StatusCodes.Status409Conflict)]
    public async Task PreservesAllowListedIdentityInstallationTokenErrors(
        HttpStatusCode responseStatus,
        string errorCode,
        int expectedStatus)
    {
        var operationCalled = false;
        var handler = new AppTokenRecordingHandler(_ => Task.FromResult(new HttpResponseMessage(responseStatus)
        {
            Content = JsonContent(new { error = errorCode })
        }));
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(httpClient, Options(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WithGitHubAppSelectionCredentialAsync(
                CallerContext(includeBrokerAudience: true),
                AcceptedRun(),
                "connection-1",
                new string('b', 64),
                "octo/widget",
                issueWriteRequested: false,
                (_, _, _) =>
                {
                    operationCalled = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None));

        Assert.Equal(errorCode, exception.Code);
        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.False(operationCalled);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    [InlineData(HttpStatusCode.Unauthorized, "<html>unauthorized</html>")]
    [InlineData(HttpStatusCode.Forbidden, "")]
    [InlineData(HttpStatusCode.Forbidden, "<html>forbidden</html>")]
    public async Task PreservesNativeIdentityDenialWhenBodyIsNotAnAllowListedError(
        HttpStatusCode responseStatus,
        string body)
    {
        var operationCalled = false;
        var handler = new AppTokenRecordingHandler(_ => Task.FromResult(
            new HttpResponseMessage(responseStatus)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            }));
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(httpClient, Options(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WithGitHubAppSelectionCredentialAsync(
                CallerContext(includeBrokerAudience: true),
                AcceptedRun(),
                "connection-1",
                new string('b', 64),
                "octo/widget",
                issueWriteRequested: false,
                (_, _, _) =>
                {
                    operationCalled = true;
                    return Task.FromResult(true);
                },
                CancellationToken.None));

        Assert.Equal("source_control_secret_redemption_denied", exception.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
        Assert.False(operationCalled);
    }

    [Fact]
    public async Task DoesNotForwardUnknownIdentityInstallationTokenError()
    {
        var handler = new AppTokenRecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = JsonContent(new { error = "internal-provider-detail" })
        }));
        using var httpClient = new HttpClient(handler);
        var client = new SourceControlSecretRedemptionClient(httpClient, Options(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<CoordinationException>(() =>
            client.WithGitHubAppSelectionCredentialAsync(
                CallerContext(includeBrokerAudience: true),
                AcceptedRun(),
                "connection-1",
                new string('b', 64),
                "octo/widget",
                issueWriteRequested: false,
                (_, _, _) => Task.FromResult(true),
                CancellationToken.None));

        Assert.Equal("source_control_installation_token_unavailable", exception.Code);
        Assert.Equal(StatusCodes.Status502BadGateway, exception.StatusCode);
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

    private sealed class AppTokenRecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
