using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class ReviewedRemoteToolTransportClientTests
{
    private static readonly OrchestratorOptions Options = new(
        "https://identity.test/", "orchestrator", "https://projects.test/", "projects",
        "https://events.test/", "events");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly Guid RuntimeId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string ActorId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task SendsExactCopiedEnvelopeAndOriginalActorOnlyToTheFixedEnvironmentOwner()
    {
        using var rig = CreateRig();
        var expected = rig.Bytes.ToArray();
        rig.Authentication.OnAuthenticate = () => Array.Fill(rig.Bytes, (byte)'!');
        rig.Handler.Respond = async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://environment.test:9448" + RemoteMcpNativeCallContract.DispatchPath,
                request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer actor-bearer", request.Headers.Authorization!.ToString());
            Assert.Equal(["tenant-1"], request.Headers.GetValues("X-Agentweaver-Tenant"));
            return await MatchingResponseAsync(request, expected);
        };

        var result = await rig.SendAsync();

        Assert.Equal("result"u8.ToArray(), result.Content.ToArray());
        Assert.Equal(SessionMaterialResultContentClass.Structured, result.ContentClass);
        Assert.Equal(1, rig.Handler.Calls);
        Assert.All(rig.Bytes, value => Assert.Equal((byte)'!', value));
    }

    [Fact]
    public void SerializerUsesReviewedArgumentsAndAnIndependentRpcIdentity()
    {
        using var rig = CreateRig();
        using var document = JsonDocument.Parse(rig.Bytes);
        Assert.NotEqual(rig.Proof.NativeCallId, document.RootElement.GetProperty("id").GetString());
        RemoteMcpCurrentCallContract.ValidateToolCallBytes(
            RemoteMcpCurrentCallContract.CreateRequest(rig.Proof), "POST",
            rig.Snapshot.EndpointUri, rig.Bytes);
    }

    [Fact]
    public async Task JsonRoundTripMatchesTheEntireProofByValueRatherThanReferenceIdentity()
    {
        using var rig = CreateRig();
        var proof = JsonSerializer.Deserialize<RemoteToolResultProof>(
            JsonSerializer.SerializeToUtf8Bytes(rig.Proof, JsonOptions), JsonOptions)!;
        Assert.NotSame(rig.Proof.Snapshot, proof.Snapshot);

        var bytes = rig.Client.SerializeCall(Snapshot(), rig.Call);
        var result = await rig.Client.SendAsync(
            rig.Context, proof, Snapshot(), bytes, CancellationToken.None);

        Assert.Equal("result"u8.ToArray(), result.Content.ToArray());
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(SessionMaterialValidation.MaximumBytes + 1)]
    public async Task RequestSizeIsCheckedBeforeCopyingOrAuthenticating(int length)
    {
        using var rig = CreateRig();
        rig.Authentication.OnAuthenticate = () =>
            throw new InvalidOperationException("An invalid size must be rejected before authentication.");

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Client.SendAsync(rig.Context, rig.Proof, rig.Snapshot, new byte[length], CancellationToken.None));

        Assert.Equal("runtime_remote_tool_call_size_invalid", failure.Code);
        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task ExactRequestByteLimitReachesOnlyTheFixedOwner()
    {
        using var rig = CreateRig();
        var bytes = new byte[SessionMaterialValidation.MaximumBytes];
        Array.Fill(bytes, (byte)' ');
        rig.Bytes.CopyTo(bytes, 0);
        rig.Handler.Respond = request => MatchingResponseAsync(request, bytes);

        var result = await rig.Client.SendAsync(
            rig.Context, rig.Proof, rig.Snapshot, bytes, CancellationToken.None);

        Assert.Equal("result"u8.ToArray(), result.Content.ToArray());
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public async Task ExactResultByteLimitReturnsTheBoundedPrivateCopy()
    {
        using var rig = CreateRig();
        var expected = new byte[SessionMaterialValidation.MaximumBytes];
        rig.Handler.Respond = async request =>
        {
            var envelope = await ReadRequestAsync(request);
            return Response(request, Result(envelope) with { ContentBytes = expected });
        };

        var result = await rig.SendAsync();

        Assert.Equal(expected, result.Content.ToArray());
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("snapshot")]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("expired")]
    [InlineData("unauthenticated")]
    public async Task InvalidInputsCannotReachTheOwner(string changed)
    {
        using var rig = CreateRig();
        var snapshot = changed == "snapshot" ? Snapshot("https://other.test/api") : rig.Snapshot;
        if (changed == "bytes")
            Array.Fill(rig.Bytes, (byte)'!');
        if (changed == "actor")
            rig.Context.User = Principal(Guid.NewGuid().ToString("D"));
        if (changed == "tenant")
            rig.Context.Request.Headers["X-Agentweaver-Tenant"] = "other";
        if (changed == "expired")
            rig.Time.Now = rig.Authentication.Expiry.AddSeconds(1);
        if (changed == "unauthenticated")
            rig.Authentication.Succeeded = false;

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Client.SendAsync(rig.Context, rig.Proof, snapshot, rig.Bytes, CancellationToken.None));

        Assert.Equal(0, rig.Handler.Calls);
    }

    [Fact]
    public async Task MissingTenantSelectorStillForwardsTheOwnerBuiltTenantBinding()
    {
        using var rig = CreateRig();
        rig.Context.Request.Headers.Remove("X-Agentweaver-Tenant");
        rig.Handler.Respond = request =>
        {
            Assert.Equal(["tenant-1"], request.Headers.GetValues("X-Agentweaver-Tenant"));
            return MatchingResponseAsync(request);
        };

        _ = await rig.SendAsync();

        Assert.Equal(1, rig.Handler.Calls);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("tenant")]
    [InlineData("bearer")]
    [InlineData("expired")]
    public async Task ActorChangesDuringTheOwnerWaitPreventReturningAResult(string changed)
    {
        using var rig = CreateRig();
        rig.Handler.Respond = async request =>
        {
            var response = await MatchingResponseAsync(request);
            if (changed == "actor")
                rig.Context.User = Principal(Guid.NewGuid().ToString("D"));
            if (changed == "tenant")
                rig.Context.Request.Headers["X-Agentweaver-Tenant"] = "other";
            if (changed == "bearer")
                rig.Context.Request.Headers.Authorization = "Bearer changed";
            if (changed == "expired")
                rig.Time.Now = rig.Authentication.Expiry.AddSeconds(1);
            return response;
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.SendAsync());

        Assert.Equal(1, rig.Handler.Calls);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("snapshot-id")]
    [InlineData("snapshot-digest")]
    [InlineData("snapshot-agent")]
    [InlineData("snapshot-node")]
    [InlineData("version")]
    [InlineData("empty")]
    [InlineData("oversized-content")]
    [InlineData("cache")]
    [InlineData("redirect")]
    [InlineData("unknown-field")]
    [InlineData("oversized-envelope")]
    [InlineData("oversized-unknown-length-envelope")]
    public async Task ChangedOrUnboundedResponsesCannotProduceAResult(string changed)
    {
        using var rig = CreateRig();
        rig.Handler.Respond = async request =>
        {
            var envelope = await ReadRequestAsync(request);
            var result = Result(envelope);
            result = changed switch
            {
                "proof" => result with { Proof = result.Proof with { ActorId = Guid.NewGuid().ToString("D") } },
                "version" => result with { ContractVersion = 2 },
                "empty" => result with { ContentBytes = [] },
                "oversized-content" => result with
                {
                    ContentBytes = new byte[SessionMaterialValidation.MaximumBytes + 1]
                },
                _ => result
            };
            if (changed.StartsWith("snapshot-", StringComparison.Ordinal))
            {
                var snapshot = result.Proof.Snapshot;
                result = result with
                {
                    Proof = result.Proof with
                    {
                        Snapshot = new(
                            snapshot.ProjectId,
                            changed == "snapshot-id" ? Guid.NewGuid() : snapshot.SnapshotId,
                            changed == "snapshot-digest" ? new string('e', 64) : snapshot.SnapshotDigest,
                            changed == "snapshot-agent" ? "other-agent" : snapshot.AgentId,
                            changed == "snapshot-node" ? "other-node" : snapshot.NodeId)
                    }
                };
            }
            var response = Response(request, result);
            if (changed == "cache")
                response.Headers.CacheControl = null;
            if (changed == "redirect")
                response.StatusCode = HttpStatusCode.TemporaryRedirect;
            if (changed == "unknown-field")
            {
                var json = JsonSerializer.Serialize(result, JsonOptions);
                response.Content = new StringContent(json[..^1] + ",\"unexpected\":true}", Encoding.UTF8,
                    "application/json");
            }
            if (changed.StartsWith("oversized-", StringComparison.Ordinal) &&
                changed != "oversized-content")
            {
                var bytes = new byte[RemoteMcpNativeCallContract.MaximumEnvelopeBytes + 1];
                response.Content = changed == "oversized-unknown-length-envelope"
                    ? new StreamContent(new UnknownLengthStream(bytes))
                    : new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = new("application/json");
            }
            return response;
        };

        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.SendAsync());

        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public async Task UnavailableEnforcementProducesOneDeniedOwnerRequestWithoutRetry()
    {
        using var rig = CreateRig();
        rig.Handler.Respond = request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = JsonContent.Create(new { code = "remote_mcp_forced_network_enforcement_unavailable" })
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        };

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => rig.SendAsync());

        Assert.Equal("runtime_owner_unavailable", failure.Code);
        Assert.Equal(1, rig.Handler.Calls);
    }

    [Fact]
    public async Task OwnerTimeoutIsReportedWithoutRetry()
    {
        using var rig = CreateRig();
        rig.Handler.Failure = new TaskCanceledException("Controlled owner timeout.");

        var failure = await Assert.ThrowsAsync<CoordinationException>(() => rig.SendAsync());

        Assert.Equal("runtime_remote_tool_transport_owner_timeout", failure.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Equal(1, rig.Handler.Calls);
    }

    private static Rig CreateRig()
    {
        var snapshot = Snapshot();
        var execution = new RemoteToolCallExecutionBinding(
            RuntimeId, 1, Options.Issuer, ActorId, "tenant-1", "project-1", "run-1", "session-1", "step-1",
            1, 1, 1, "context-1", new string('f', 64), 1);
        var call = ReviewedRemoteToolCall.Create(
            snapshot, execution, "native-call-1", snapshot.AgentId, snapshot.NodeId, snapshot.ToolId,
            """{"query":"x"}""");
        var proof = new RemoteToolResultProof(
            1, call.EventId, call.InputHash, call.NativeCallId, snapshot.Reference,
            ReviewedRemoteToolAgent.SnapshotBinding(snapshot), snapshot.ToolId,
            call.CanonicalArgumentsJson, call.ArgumentsDigest, RuntimeId, 1, Options.Issuer, ActorId,
            "tenant-1", "project-1", "run-1", "session-1", "step-1", 1, 1, 1, "context-1",
            new string('f', 64), 1, call.ActionId, call.Purpose, 1, true,
            call.EventId.ToString("N"), "1", call.EventId, PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed, SessionMaterialResultContentClass.TextOnly);
        var time = new Clock();
        var authentication = new Authentication(time.Now.AddMinutes(5));
        var services = new ServiceCollection().AddSingleton<IAuthenticationService>(authentication)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = Principal(ActorId) };
        context.Request.Headers.Authorization = "Bearer actor-bearer";
        context.Request.Headers["X-Agentweaver-Tenant"] = "tenant-1";
        var handler = new Handler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        var tls = new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Orchestrator",
            PeerServiceName = "Environment",
            LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
            PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
            LocalCertificatePemPath = Path.Combine(AppContext.BaseDirectory, "unused.pem"),
            LocalPrivateKeyPemPath = Path.Combine(AppContext.BaseDirectory, "unused-key.pem"),
            LocalCertificateSha256 = new string('a', 64),
            PeerCertificateSha256Pins = [new string('b', 64)],
            PeerOwnerAddress = "https://environment.test:9448/",
            ListenerPort = 9446
        };
        var client = new ReviewedRemoteToolTransportClient(http, tls, Options, time);
        return new(context, snapshot, call, proof, client.SerializeCall(snapshot, call),
            client, handler, authentication, time, http, services);
    }

    private static ReviewedRemoteToolSnapshot Snapshot(string endpoint = "https://mcp.example.test/api") => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"), "project-1", "agent", "node",
        "connection-1", 1, 2, 3, "Enabled", new string('a', 64), endpoint,
        "https://mcp.example.test/resource", "None", null, "StreamableHttp20250618", null,
        "catalog-1", new string('b', 64), "remote.lookup", "tool-1", new string('c', 64),
        "schema-1", new string('d', 64), """{"name":"remote.lookup"}""", """{"type":"object"}""",
        new("tool.read", "runtime.execution", """{"source":"explicit-review"}"""));

    private static ClaimsPrincipal Principal(string actor) => new(new ClaimsIdentity(
        [new("sub", actor), new("iss", Options.Issuer)], "test"));

    private static async Task<RemoteMcpNativeCallRequest> ReadRequestAsync(HttpRequestMessage request) =>
        (await request.Content!.ReadFromJsonAsync<RemoteMcpNativeCallRequest>(JsonOptions))!;

    private static async Task<HttpResponseMessage> MatchingResponseAsync(
        HttpRequestMessage request, byte[]? expected = null)
    {
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.False(request.Headers.Contains("Proxy-Authorization"));
        var envelope = await ReadRequestAsync(request);
        if (expected is not null)
            Assert.Equal(expected, envelope.RequestBytes);
        RemoteMcpNativeCallContract.ValidateRequest(envelope, "POST",
            envelope.CurrentCall.CallBinding.CanonicalTarget, envelope.RequestBytes);
        return Response(request, Result(envelope));
    }

    private static RemoteMcpNativeCallResult Result(RemoteMcpNativeCallRequest request) => new(
        1, request.CurrentCall.Proof with { ContentClass = SessionMaterialResultContentClass.Structured },
        "result"u8.ToArray());

    private static HttpResponseMessage Response(HttpRequestMessage request, RemoteMcpNativeCallResult result)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = JsonContent.Create(result, options: JsonOptions)
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed record Rig(
        HttpContext Context, ReviewedRemoteToolSnapshot Snapshot, ReviewedRemoteToolCall Call,
        RemoteToolResultProof Proof, byte[] Bytes,
        ReviewedRemoteToolTransportClient Client, Handler Handler, Authentication Authentication,
        Clock Time, HttpClient Http, ServiceProvider Services) : IDisposable
    {
        public Task<ReviewedRemoteToolTransportResult> SendAsync() =>
            Client.SendAsync(Context, Proof, Snapshot, Bytes, CancellationToken.None);
        public void Dispose()
        {
            Http.Dispose();
            Services.Dispose();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Exception? Failure { get; set; }
        public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
            request => MatchingResponseAsync(request);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Failure is null ? Respond(request) : Task.FromException<HttpResponseMessage>(Failure);
        }
    }

    private sealed class Authentication(DateTimeOffset expiry) : IAuthenticationService
    {
        public DateTimeOffset Expiry => expiry;
        public bool Succeeded { get; set; } = true;
        public Action? OnAuthenticate { get; set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            OnAuthenticate?.Invoke();
            return Task.FromResult(Succeeded
                ? AuthenticateResult.Success(new AuthenticationTicket(context.User,
                    new AuthenticationProperties { ExpiresUtc = expiry }, "test"))
                : AuthenticateResult.NoResult());
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignInAsync(
            HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }
}
