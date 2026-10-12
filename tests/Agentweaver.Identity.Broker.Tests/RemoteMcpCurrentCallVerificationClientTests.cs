using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpCurrentCallVerificationClientTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-11T00:00:00Z");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task SendsOriginalAuthenticatedActorAndExactProofWithoutWireIdentityClaims()
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var expected = Acknowledgment(request);
        var sends = 0;
        using var http = new HttpClient(new Handler(async wire =>
        {
            sends++;
            Assert.Equal("https://environment.test:9446/internal/remote-mcp/current-call/verify",
                wire.RequestUri!.AbsoluteUri);
            Assert.Equal("original-actor-token", wire.Headers.Authorization?.Parameter);
            Assert.Equal("tenant-1", Assert.Single(wire.Headers.GetValues("X-Agentweaver-Tenant")));
            Assert.True(wire.Headers.CacheControl?.NoStore);
            var bytes = await wire.Content!.ReadAsByteArrayAsync();
            Assert.DoesNotContain("original-actor-token", Encoding.UTF8.GetString(bytes));
            var received = JsonSerializer.Deserialize<RemoteMcpCurrentCallVerificationRequest>(bytes, JsonOptions);
            Assert.NotNull(received);
            Assert.True(SessionMaterialValidation.SameRemoteToolResultProof(request.Proof, received.Proof));
            Assert.Equal(request.CallBinding, received.CallBinding);
            return Success(wire, expected);
        }));

        var actual = await Client(http).VerifyAsync(Context(), request, CancellationToken.None);

        Assert.Equal(expected, actual);
        Assert.Equal(1, sends);
        Assert.Equal("native-call-1", request.CallBinding.NativeCallId);
    }

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("wrong-subject")]
    [InlineData("duplicate-subject")]
    [InlineData("expired")]
    [InlineData("missing-expiry")]
    [InlineData("wrong-issuer")]
    [InlineData("duplicate-header")]
    [InlineData("wrong-tenant")]
    public async Task InvalidActorCannotContactOwner(string failure)
    {
        var sends = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            sends++;
            throw new InvalidOperationException("Invalid actors must not contact the owner.");
        }));
        var context = Context(failure);
        var proof = Proof();
        if (failure == "wrong-issuer")
            proof = proof with { ActorIssuer = "https://other.test/" };

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(http).VerifyAsync(context, RemoteMcpCurrentCallContract.CreateRequest(proof), CancellationToken.None));

        Assert.Equal("remote_mcp_current_verification_actor_invalid", error.Code);
        Assert.Equal(0, sends);
    }

    [Theory]
    [InlineData("proof")]
    [InlineData("binding")]
    [InlineData("snapshot")]
    [InlineData("tenant")]
    [InlineData("target")]
    [InlineData("policy")]
    public async Task RejectsOwnerAcknowledgmentForAnotherCallOrPolicy(string failure)
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var expected = Acknowledgment(request);
        var bad = failure switch
        {
            "proof" => expected with { ProofSha256 = Hash("another proof") },
            "binding" => expected with { CallBindingSha256 = Hash("another binding") },
            "snapshot" => expected with { SnapshotBinding = expected.SnapshotBinding with { ConnectionRowRevision = 99 } },
            "target" => expected with { GatewayPolicy = expected.GatewayPolicy with { TargetEndpointSha256 = Hash("another target") } },
            "policy" => expected with { GatewayPolicy = expected.GatewayPolicy with { PolicyResource = expected.GatewayPolicy.PolicyResource with { Seam = ProviderSeam.Sandbox } } },
            _ => expected with
            {
                GatewayPolicy = expected.GatewayPolicy with
                {
                    AppliedPolicy = new("policy-1",
                        new(new("another-tenant", "project-1", "run-1", "environment-1"), 1), 1, Hash("egress"))
                }
            }
        };
        using var http = new HttpClient(new Handler(wire => Task.FromResult(Success(wire, bad))));

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(http).VerifyAsync(Context(), request, CancellationToken.None));

        Assert.Equal("remote_mcp_current_verification_owner_invalid", error.Code);
    }

    [Theory]
    [InlineData("redirect", "runtime_owner_redirect_rejected")]
    [InlineData("missing-owner", "runtime_owner_unavailable")]
    [InlineData("cacheable", "runtime_owner_cache_contract_invalid")]
    public async Task RejectsMissingRedirectingOrCacheableOwner(string failure, string code)
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        using var http = new HttpClient(new Handler(wire =>
        {
            var response = Success(wire, Acknowledgment(request));
            if (failure == "redirect")
            {
                response.StatusCode = HttpStatusCode.TemporaryRedirect;
                response.Headers.Location = new Uri("https://other.test/");
            }
            else if (failure == "missing-owner")
                response.StatusCode = HttpStatusCode.NotFound;
            else
                response.Headers.CacheControl = null;
            return Task.FromResult(response);
        }));

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(http).VerifyAsync(Context(), request, CancellationToken.None));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task ActorExpiryDuringOwnerWaitDeniesAcknowledgment()
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var clock = new Clock(Now);
        using var http = new HttpClient(new Handler(wire =>
        {
            clock.Now = Now.AddMinutes(3);
            return Task.FromResult(Success(wire, Acknowledgment(request)));
        }));

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Client(http, clock).VerifyAsync(Context(), request, CancellationToken.None));

        Assert.Equal("remote_mcp_current_verification_actor_expired", error.Code);
    }

    [Fact]
    public void PolicyChangesAcrossWaitCannotReuseAuthority()
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        var before = Acknowledgment(request);
        var after = before with
        {
            GatewayPolicy = before.GatewayPolicy with
            {
                AppliedPolicy = new("policy-2", before.GatewayPolicy.AppliedPolicy.EnvironmentFence, 2, Hash("egress-2"))
            }
        };

        Assert.Throws<ArgumentException>(() => RemoteMcpCurrentCallContract.RequireSameAuthority(request, before, after));
        RemoteMcpCurrentCallContract.RequireSameAuthority(request, before, before with { });
    }

    [Fact]
    public void BindingCannotReplaceReviewedNativeCallIdentityOrArgumentDigest()
    {
        var request = RemoteMcpCurrentCallContract.CreateRequest(Proof());
        Assert.Throws<ArgumentException>(() => RemoteMcpCurrentCallContract.Validate(
            request with { CallBinding = request.CallBinding with { NativeCallId = "rpc-local-id" } }));
        Assert.Throws<ArgumentException>(() => RemoteMcpCurrentCallContract.CreateRequest(
            request.Proof with { ArgumentsDigest = Hash("caller-wire-bytes") }));
    }

    internal static RemoteToolResultProof Proof()
    {
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string arguments = """{"query":"x"}""";
        return new(1, eventId, Hash("input"), "native-call-1",
            new("project-1", Guid.Parse("44444444-4444-4444-4444-444444444444"), Hash("snapshot"), "agent", "node"),
            new("connection-1", 1, 2, 3, "Enabled", Hash("configuration"),
                "https://mcp.example.test/api", "https://mcp.example.test/resource", "None", null,
                "StreamableHttp20250618", null, null, null, "catalog-1", Hash("catalog"),
                "remote.lookup", "tool-1", Hash("tool"), "schema-1", Hash("schema"),
                "tool.read", "runtime.execution", Hash("permission")),
            "remote.lookup", arguments, Hash(arguments),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 1,
            "https://identity.test/", "11111111-1111-1111-1111-111111111111",
            "tenant-1", "project-1", "run-1", "session-1", "step-1",
            1, 1, 1, "context-1", Hash("selection"), 1, "tool.read", "runtime.execution",
            1, true, eventId.ToString("N"), "1", eventId, PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed, SessionMaterialResultContentClass.TextOnly);
    }

    internal static RemoteMcpCurrentCallVerificationAcknowledgment Acknowledgment(
        RemoteMcpCurrentCallVerificationRequest request)
    {
        var proof = request.Proof;
        var hash = SessionMaterialValidation.RemoteToolResultProofHash(proof);
        return new(1, hash, RemoteMcpCurrentCallContract.CallBindingHash(request.CallBinding),
            new(1, SessionMaterialValidation.RemoteToolResultEventId(proof), hash), proof.SnapshotBinding,
            new(new("policy-1", new(new("tenant-1", "project-1", "run-1", "environment-1"), 1), 1, Hash("egress")),
                new(ProviderSeam.NetworkPolicy, "managed-gateway", "resource-1", 1),
                "1.0.0", 1, "options-1", Hash("options"), Hash("gateway"), Hash(request.CallBinding.CanonicalTarget)));
    }

    private static RemoteMcpCurrentCallVerificationClient Client(HttpClient http, TimeProvider? clock = null) =>
        new(http, new RuntimeServiceTlsOptions
        {
            LocalServiceName = "IdentityBroker",
            LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
            LocalCertificatePemPath = Path.Combine(AppContext.BaseDirectory, "unused.pem"),
            LocalPrivateKeyPemPath = Path.Combine(AppContext.BaseDirectory, "unused-key.pem"),
            LocalCertificateSha256 = Hash("identity"),
            ListenerPort = 9445,
            PeerServiceName = "Environment",
            PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
            PeerOwnerAddress = "https://environment.test:9446/",
            PeerCertificateSha256Pins = [Hash("environment")]
        }, new IdentityBrokerOptions
        {
            Issuer = "https://identity.test/",
            Signing = new() { PfxPath = "unused.pfx", PfxPassword = "unused" },
            ExternalProvider = new() { Authority = "https://external.test/", ClientId = "unused" },
            SecretRedemption = new()
            {
                Audience = "unused", VaultUri = "https://vault.test/",
                WorkloadIdentityTenantId = "unused", WorkloadIdentityClientId = "unused",
                WorkloadIdentityTokenFilePath = "unused"
            },
            DataProtectionKeyPath = "unused", Clients = []
        }, clock ?? new Clock(Now));

    internal static HttpContext Context(string? failure = null)
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", failure == "wrong-subject" ? "22222222-2222-2222-2222-222222222222" : Proof().ActorId)],
            failure == "unauthenticated" ? null : "validated");
        if (failure == "duplicate-subject")
            identity.AddClaim(new Claim("sub", Proof().ActorId));
        var principal = new ClaimsPrincipal(identity);
        if (failure != "missing-expiry")
            principal.SetExpirationDate(failure == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(2));
        var authentication = AuthenticateResult.Success(new(principal,
            OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme));
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new Authentication(authentication));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Headers.Authorization = failure == "duplicate-header"
            ? new Microsoft.Extensions.Primitives.StringValues(["Bearer original-actor-token", "Bearer another"])
            : "Bearer original-actor-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = failure == "wrong-tenant" ? "another-tenant" : "tenant-1";
        return context;
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static HttpResponseMessage Success(HttpRequestMessage request, RemoteMcpCurrentCallVerificationAcknowledgment receipt) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = JsonContent.Create(receipt, options: JsonOptions),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        };

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request);
    }

    private sealed class Authentication(AuthenticateResult result) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Assert.Equal(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, scheme);
            return Task.FromResult(result);
        }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
}
