using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.EventsAndSessions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class SessionsEndpointFakeTests
{
    [Theory]
    [InlineData("/internal/runtime/resume/configuration", false)]
    [InlineData("/internal/runtime/resume/configuration", true)]
    [InlineData("/internal/sessions/session-1/runtime-configuration", false)]
    [InlineData("/internal/sessions/session-1/runtime-configuration", true)]
    public async Task ConfigurationRoutesRejectHttpAndForwardedHttpsBeforeReadingPrivateBody(string path, bool https)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = https ? "https" : "http";
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSessionMaterialEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsync(path, new StringContent("private accepted configuration"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("runtime_configuration_direct_https_required", await response.Content.ReadAsStringAsync());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
    }

    [Fact]
    public void PublicReplayHidesConfigurationKeysDigestsBindingsAndReferences()
    {
        var hash = new string('a', 64);
        var reference = new SessionObjectReference(
            new ObjectKey("private/configuration/opaque-key"),
            SessionMaterialValidation.Purpose(SessionMaterialKind.RuntimeConfiguration), 2)
        {
            Material = new(1, SessionMaterialKind.RuntimeConfiguration, "tenant-1", hash,
                Guid.NewGuid(), 1, 1, new string('b', 64), "sdk-v1", "runtime-v1", "model:selected", "model")
        };
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = SessionsContractVersions.CurrentSchemaVersion,
            eventVersion = SessionsContractVersions.CurrentEventVersion,
            eventId = Guid.NewGuid(),
            identity = new SessionIdentity("project-1", "run-1", "session-1"),
            position = 1,
            occurredAt = DateTimeOffset.UtcNow,
            payload = (SessionEventPayload)new RuntimeConfigurationSessionPayload(reference),
            objectReferences = new[]
            {
                new StoredSessionObjectReference(reference, new("events-owner", DateTimeOffset.UtcNow.AddDays(1)))
            }
        }, CreateJsonOptions());
        var recorded = JsonSerializer.Deserialize<SessionEventEnvelope>(json, CreateJsonOptions())!;
        var projected = PublicSessionEventProjection.Project(new SessionEventPage([recorded], null, false));
        var result = Assert.Single(projected.Events);
        Assert.Null(Assert.IsType<RuntimeConfigurationSessionPayload>(result.Payload).Configuration);
        Assert.Empty(result.ObjectReferences);
        var publicJson = JsonSerializer.Serialize(projected, CreateJsonOptions());
        Assert.DoesNotContain(reference.Key.Value, publicJson);
        Assert.DoesNotContain(hash, publicJson);
        Assert.DoesNotContain(reference.Material.RuntimeInstanceId.ToString("D"), publicJson);
        Assert.Equal(publicJson,
            JsonSerializer.Serialize(PublicSessionEventProjection.Project(projected), CreateJsonOptions()));
    }

    [Theory]
    [InlineData("/internal/sessions/session-1/remote-tool-results")]
    [InlineData("/internal/sessions/session-1/remote-tool-results/read")]
    public async Task ReviewedRemoteToolResultRoutesRejectHttpBeforeReadingBody(string path)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapReviewedRemoteToolResultMaterialEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent("private result"u8.ToArray())
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("runtime_remote_tool_result_https_required",
            document.RootElement.GetProperty("error").GetString());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
    }

    [Fact]
    public async Task RunReplayHidesRecordedRemoteToolResultDetails()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var proof = CreateRemoteToolResultProof();
        var journal = new FakeJournal
        {
            RunReplayPage = new(
                [CreateRemoteToolResultEvent(proof)], "opaque-cursor", HasMore: false)
        };
        builder.Services.AddSingleton<ICoordinationOwnerClient, FakeCoordinationOwnerClient>();
        builder.Services.AddSingleton<ISessionsJournal>(journal);
        builder.Services.AddSingleton<ISessionsProviderBinder, FakeBinder>();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEventsAndSessionsEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var createResponse = await client.PostAsync("/internal/sessions/session-1", content: null);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, createResponse.StatusCode);
        using var response = await client.GetAsync("/internal/projects/project-1/runs/run-1/events");
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK, responseText);
        using var document = JsonDocument.Parse(responseText);
        var item = Assert.Single(document.RootElement.GetProperty("events").EnumerateArray());
        var payload = item.GetProperty("payload");
        Assert.Equal("tool_call", payload.GetProperty("kind").GetString());
        Assert.Equal("external-tool", payload.GetProperty("toolName").GetString());
        Assert.Equal(item.GetProperty("eventId").GetGuid().ToString("N"), payload.GetProperty("callId").GetString());
        Assert.Equal("completed", payload.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("arguments").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("result").ValueKind);
        Assert.Empty(item.GetProperty("objectReferences").EnumerateArray());
        Assert.DoesNotContain("remote_tool_", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("native-call-1", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("""{"query":"x"}""", responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.CallInputHash, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.ArgumentsDigest, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.SnapshotBinding.CatalogDigest, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.Snapshot.SnapshotDigest, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(SessionMaterialValidation.RemoteToolResultProofHash(proof), responseText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(proof.GrantId, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(proof.PolicyReceiptId.ToString("D"), responseText, StringComparison.Ordinal);
        Assert.DoesNotContain("remote result", responseText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/internal/sessions/session-1/remote-tool-results", false)]
    [InlineData("/internal/sessions/session-1/remote-tool-results/read", false)]
    [InlineData("/internal/sessions/session-1/remote-tool-results", true)]
    [InlineData("/internal/sessions/session-1/remote-tool-results/read", true)]
    public async Task ReviewedResultHttpsFlagDoesNotAuthorizeBodyReadOrCertificateLoading(string path, bool configured)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var certificateResolved = false;
        if (configured)
        {
            builder.Services.AddSingleton(new RuntimeServiceTlsOptions { ListenerPort = 9443 });
            builder.Services.AddSingleton<RuntimeServiceTlsIdentity>(_ =>
            {
                certificateResolved = true;
                throw new InvalidOperationException("Native TLS must be checked before resolving the certificate.");
            });
        }
        await using var app = builder.Build();
        UnexpectedBodyReadStream? body = null;
        app.Use(async (context, next) =>
        {
            context.Request.Scheme = "https";
            context.Connection.LocalPort = 9443;
            body = new UnexpectedBodyReadStream();
            context.Request.Body = body;
            await next();
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapReviewedRemoteToolResultMaterialEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ByteArrayContent("private proof and bytes"u8.ToArray())
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(configured ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.ServiceUnavailable,
            response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(configured ? "runtime_remote_tool_result_https_required" : "runtime_remote_tool_result_route_unavailable",
            document.RootElement.GetProperty("error").GetString());
        Assert.NotNull(body);
        Assert.False(body.WasRead);
        Assert.False(certificateResolved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewedResultOwnerClientRequiresExactProofAcknowledgment(bool changed)
    {
        var proof = CreateRemoteToolResultProof();
        var handler = new ReviewedResultHandler(request =>
        {
            Assert.Equal(new Uri("https://orchestrator.example:9443/internal/runtime/remote-tool-results/verify"),
                request.RequestUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("original-actor", request.Headers.Authorization?.Parameter);
            Assert.Equal(proof.TenantId, Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            return new(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new RemoteToolResultVerificationReceipt(
                    1, SessionMaterialValidation.RemoteToolResultEventId(proof),
                    changed ? new string('0', 64) : SessionMaterialValidation.RemoteToolResultProofHash(proof))),
                Headers = { CacheControl = new() { NoStore = true } }
            };
        });
        using var client = new HttpClient(handler);
        var owner = new ReviewedRemoteToolResultOwnerClient(client, new RuntimeServiceTlsOptions
        {
            LocalServiceName = "EventsAndSessions",
            LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
            LocalCertificatePemPath = Path.Combine(Path.GetTempPath(), "unused-service.pem"),
            LocalPrivateKeyPemPath = Path.Combine(Path.GetTempPath(), "unused-service-key.pem"),
            LocalCertificateSha256 = new string('a', 64),
            ListenerPort = 9443,
            PeerServiceName = "Orchestrator",
            PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
            PeerOwnerAddress = "https://orchestrator.example:9443/",
            PeerCertificateSha256Pins = [new string('b', 64)]
        });
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("original-actor", DateTimeOffset.UtcNow.AddMinutes(1)), proof.TenantId);

        if (changed)
        {
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                owner.VerifyAsync(proof, actor, CancellationToken.None));
            Assert.Equal("runtime_remote_tool_result_verification_invalid", failure.Code);
        }
        else
            await owner.VerifyAsync(proof, actor, CancellationToken.None);
    }

    [Fact]
    public async Task GenericSessionMaterialRouteRejectsRemoteToolResultsBeforeOwnerAccess()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var projects = new FakeProjectsAuthorizationContextClient();
        builder.Services.AddSingleton<IProjectsAuthorizationContextClient>(projects);
        builder.Services.AddSingleton(new SessionMaterialApplicationService(
            null!, projects, null!, null!, null!, TimeProvider.System));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSessionMaterialEndpoints();
        await app.StartAsync();

        var proof = CreateRemoteToolResultProof();
        var request = new SessionMaterialWriteRequest(
            1,
            SessionMaterialValidation.RemoteToolResultEventId(proof),
            proof.RuntimeInstanceId,
            proof.RegistrationRevision,
            proof.ExecutionFence,
            SessionMaterialKind.RemoteToolResult,
            "remote result"u8.ToArray())
        {
            RemoteToolResultProof = proof
        };
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/internal/sessions/session-1/material", request);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "runtime_remote_tool_result_write_requires_orchestrator",
            document.RootElement.GetProperty("error").GetString());
        Assert.Equal(0, projects.CallCount);

        using var readResponse = await client.GetAsync(
            $"/internal/sessions/session-1/material/{request.EventId:D}/remoteToolResult");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, readResponse.StatusCode);
        Assert.True(readResponse.Headers.CacheControl?.NoStore);
        using var readDocument = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "runtime_remote_tool_result_read_requires_orchestrator",
            readDocument.RootElement.GetProperty("error").GetString());
        Assert.Equal(1, projects.CallCount);
    }

    [Fact]
    public async Task EndpointUsesFakeJournalAndReturnsOnlyBoundedPersistenceFailure()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        var journal = new FakeJournal();
        var binder = new FakeBinder();
        builder.Services.AddSingleton<ICoordinationOwnerClient, FakeCoordinationOwnerClient>();
        builder.Services.AddSingleton<ISessionsJournal>(journal);
        builder.Services.AddSingleton<ISessionsProviderBinder>(binder);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapEventsAndSessionsEndpoints();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/internal/sessions/session-1", content: null);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("journal_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret=do-not-return", body, StringComparison.Ordinal);
        Assert.True(binder.Binding.Matches(journal.CreatedBinding!));

        using var replayResponse = await client.GetAsync("/internal/sessions/session-1/events");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, replayResponse.StatusCode);
        Assert.True(journal.CreatedBinding!.Matches(binder.VerifiedBinding!));

        using var runReplayResponse =
            await client.GetAsync("/internal/projects/project-1/runs/run-1/events");
        Assert.Equal(System.Net.HttpStatusCode.OK, runReplayResponse.StatusCode);
        Assert.Equal(("project-1", "run-1"), journal.LastRunScope);
        Assert.True(journal.CreatedBinding.Matches(binder.VerifiedBinding!));

        using var forkResponse = await client.PostAsJsonAsync(
            "/internal/sessions/session-1/fork",
            new SessionForkRequest("fork-target", Guid.NewGuid(), "cursor", "fork-once"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, forkResponse.StatusCode);
        Assert.True(forkResponse.Headers.CacheControl?.NoStore == true);
        Assert.Contains("session_fork_unavailable",
            await forkResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private sealed class FakeCoordinationOwnerClient : ICoordinationOwnerClient
    {
        public Task<MessageRouteBinding> ValidateMessageRouteAsync(
            HttpContext context,
            string projectId,
            string runId,
            MessageRouteValidationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CoordinationSessionBinding> GetSessionBindingAsync(
            HttpContext context,
            string projectId,
            string runId,
            string sessionId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PolicyEvaluationReceiptView> ReadPolicyEvaluationReceiptAsync(
            HttpContext context,
            SessionIdentity identity,
            Guid receiptId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ValidatePolicyEvaluationReceiptAdmissionAsync(
            HttpContext context,
            SessionIdentity identity,
            Guid receiptId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProducedRunCaptureProof> ReadProducedRunCaptureProofAsync(
            HttpContext context,
            SessionIdentity identity,
            string captureId,
            CancellationToken cancellationToken = default,
            bool notFoundIsMissing = false) =>
            throw new NotSupportedException();

        public Task<SessionForkAdmissionReceipt> ValidateSessionForkAdmissionAsync(
            HttpContext context,
            SessionIdentity source,
            SessionForkRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionForkAdmissionReceipt(
                Guid.NewGuid(),
                source,
                request.TargetSessionId,
                request.SourceEventId,
                request.SourceCursor,
                request.IdempotencyKey,
                1,
                "https://identity.test",
                context.User.FindFirstValue("sub")!,
                new string('A', 64)));
    }

    private sealed class FakeBinder : ISessionsProviderBinder
    {
        public SessionProviderBinding Binding { get; } = new(
                "project-1", "run-1", "postgres.native-sessions", new Version(1, 0, 0),
                1, "options-v1", "resource-1", 1,
                System.Collections.Immutable.ImmutableHashSet<string>.Empty);
        public SessionProviderBinding? VerifiedBinding { get; private set; }

        public Task<SessionProviderBinding> ResolveAndPinAsync(
            ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Binding);

        public Task VerifyPinnedAsync(
            ClaimsPrincipal principal,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default)
        {
            VerifiedBinding = binding;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJournal : ISessionsJournal
    {
        public SessionProviderBinding? CreatedBinding { get; private set; }
        public (string ProjectId, string RunId)? LastRunScope { get; private set; }
        public SessionEventPage? RunReplayPage { get; init; }

        public Task<SessionRecord> CreateSessionAsync(
            ClaimsPrincipal principal,
            string sessionId,
            SessionProviderBinding binding,
            CancellationToken cancellationToken = default)
        {
            CreatedBinding = binding;
            throw new InvalidOperationException("database secret=do-not-return");
        }

        public Task<SessionProviderBinding> GetProviderBindingAsync(
            ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreatedBinding
                ?? throw new InvalidOperationException("A fake provider binding was not created."));

        public Task<SessionProviderBinding> GetRunProviderBindingAsync(
            ClaimsPrincipal principal,
            string projectId,
            string runId,
            CancellationToken cancellationToken = default)
        {
            LastRunScope = (projectId, runId);
            return Task.FromResult(CreatedBinding
                ?? throw new InvalidOperationException("A fake provider binding was not created."));
        }

        public Task<SessionAppendResult> AppendAsync(
            ClaimsPrincipal principal,
            string sessionId,
            AppendSessionEvent input,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SessionForkResult> ForkFromExplicitEventAsync(
            ClaimsPrincipal principal,
            string sourceSessionId,
            SessionForkRequest request,
            Func<CancellationToken, Task> validateAdmission,
            CancellationToken cancellationToken = default) =>
            throw new SessionForkUnsupportedException("The fake journal does not support session forks.");

        public Task<SessionEventPage> ReplayAsync(
            ClaimsPrincipal principal,
            SessionEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("database secret=do-not-return");

        public Task<SessionEventPage> ReplayRunAsync(
            ClaimsPrincipal principal,
            SessionRunEventPageRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RunReplayPage ?? new SessionEventPage([], null, HasMore: false));

        public IAsyncEnumerable<SessionEventDelivery> SubscribeAsync(
            ClaimsPrincipal principal,
            SessionSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionEventDelivery> SubscribeRunAsync(
            ClaimsPrincipal principal,
            SessionRunSubscriptionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim("sub", "33333333-3333-3333-3333-333333333333"),
                    new Claim("iss", "https://identity.test"),
                    new Claim("scope", "openid"),
                    new Claim("aud", "agentweaver.events"),
                    new Claim("project_id", "project-1"),
                    new Claim("run_id", "run-1")
                ],
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class FakeProjectsAuthorizationContextClient : IProjectsAuthorizationContextClient
    {
        public int CallCount { get; private set; }

        public Task<ProjectsAuthorizationContextResponse> GetCurrentAsync(
            HttpContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new ProjectsAuthorizationContextResponse(
                1,
                "https://identity.test",
                "actor-1",
                "tenant-1",
                1,
                "project-1",
                "run-1",
                []));
        }
    }

    private static SessionEventEnvelope CreateRemoteToolResultEvent(RemoteToolResultProof proof)
    {
        var bytes = "remote result"u8.ToArray();
        var reference = new SessionObjectReference(
            new ObjectKey("session-material/test/remote-result"),
            SessionMaterialValidation.Purpose(SessionMaterialKind.RemoteToolResult),
            bytes.Length)
        {
            Material = new(
                1,
                SessionMaterialKind.RemoteToolResult,
                proof.TenantId,
                RuntimeContractValidation.Hash(bytes),
                proof.RuntimeInstanceId,
                proof.RegistrationRevision,
                proof.ExecutionFence,
                proof.AcceptedSelectionHash,
                "sdk-v1",
                "runtime-v1",
                "accepted-model",
                "model")
            {
                RemoteToolResultProof = proof
            }
        };
        SessionMaterialValidation.Validate(reference);
        var eventId = SessionMaterialValidation.RemoteToolResultEventId(proof);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = SessionsContractVersions.CurrentSchemaVersion,
            eventVersion = SessionsContractVersions.CurrentEventVersion,
            eventId,
            identity = new { proof.ProjectId, proof.RunId, proof.SessionId },
            position = 1,
            occurredAt = DateTimeOffset.UtcNow,
            kind = "toolCall",
            payload = new
            {
                kind = "tool_call",
                callId = SessionMaterialValidation.RemoteToolResultCallId(proof),
                toolName = SessionMaterialValidation.RemoteToolResultToolName(proof),
                state = "completed",
                arguments = (SessionObjectReference?)null,
                result = reference
            },
            objectReferences = new[]
            {
                new StoredSessionObjectReference(
                    reference, new SessionObjectRetention("events-owner", DateTimeOffset.UtcNow.AddDays(1)))
            }
        }, CreateJsonOptions());
        var sessionEvent = JsonSerializer.Deserialize<SessionEventEnvelope>(json, CreateJsonOptions())
            ?? throw new InvalidOperationException("Test event JSON could not be decoded.");
        Assert.Equal(eventId, sessionEvent.EventId);
        Assert.False(sessionEvent.ObjectReferences.IsDefault);
        var storedReference = Assert.Single(sessionEvent.ObjectReferences).Reference;
        AssertReferenceMatches(reference, storedReference);
        var toolCall = Assert.IsType<ToolCallSessionPayload>(sessionEvent.Payload);
        var resultReference = Assert.IsType<SessionObjectReference>(toolCall.Result);
        Assert.NotSame(storedReference, resultReference);
        AssertReferenceMatches(reference, resultReference);
        Assert.True(SessionEventPayloadValidation.SupportsEventVersion(
            sessionEvent.EventVersion, sessionEvent.Payload));
        _ = SessionEventPayloadValidation.ValidateAndGetReferences(sessionEvent.Payload);
        return sessionEvent;
    }

    private static void AssertReferenceMatches(
        SessionObjectReference expected, SessionObjectReference actual)
    {
        Assert.Equal(expected.Key.Value, actual.Key.Value);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.ByteLength, actual.ByteLength);
        Assert.Equal(expected.Material is null, actual.Material is null);
        if (expected.Material is not { } expectedMaterial)
            return;

        var actualMaterial = Assert.IsType<SessionMaterialBinding>(actual.Material);
        Assert.Equal(
            expectedMaterial with { RemoteToolResultProof = null },
            actualMaterial with { RemoteToolResultProof = null });
        if (expectedMaterial.RemoteToolResultProof is { } expectedProof)
            Assert.True(SessionMaterialValidation.SameRemoteToolResultProof(
                expectedProof, actualMaterial.RemoteToolResultProof));
        else
            Assert.Null(actualMaterial.RemoteToolResultProof);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static RemoteToolResultProof CreateRemoteToolResultProof()
    {
        var callEventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var runtimeInstanceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        const string projectId = "project-1";
        const string runId = "run-1";
        const string sessionId = "session-1";
        return new(
            1,
            callEventId,
            new string('c', 64),
            "native-call-1",
            new ReviewedRemoteToolSnapshotReference(
                projectId,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                new string('a', 64),
                "agent",
                "node"),
            new(
                "connection",
                1,
                2,
                3,
                "Enabled",
                new string('b', 64),
                "https://mcp.example.test/api",
                "https://mcp.example.test/resource",
                "None",
                null,
                "StreamableHttp20250618",
                null,
                null,
                null,
                "catalog-1",
                new string('d', 64),
                "remote.lookup",
                "tool-1",
                new string('e', 64),
                "schema-1",
                new string('f', 64),
                "tool.read",
                "runtime.execution",
                new string('9', 64)),
            "remote.lookup",
            """{"query":"x"}""",
            new string('8', 64),
            runtimeInstanceId,
            1,
            "https://identity.test",
            "actor-1",
            "tenant-1",
            projectId,
            runId,
            sessionId,
            "step-1",
            1,
            1,
            1,
            "context-1",
            new string('a', 64),
            1,
            "tool.read",
            "runtime.execution",
            1,
            true,
            callEventId.ToString("N"),
            "1",
            callEventId,
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed,
            SessionMaterialResultContentClass.Structured);
    }

    private sealed class ReviewedResultHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class UnexpectedBodyReadStream : Stream
    {
        public bool WasRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Fail<int>();
        public override int Read(Span<byte> buffer) => Fail<int>();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) => Fail<ValueTask<int>>();
        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Fail<Task<int>>();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private T Fail<T>()
        {
            WasRead = true;
            throw new InvalidOperationException("A disabled private-result route read the request body.");
        }
    }
}
