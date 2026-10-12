using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class RemoteMcpCurrentGatewayPolicyReaderTests
{
    [Fact]
    public async Task ReadsActualHandlerStateAndCurrentSelectionWithoutPromotingCiliumObjectToEnforcement()
    {
        var rig = await CreateAsync();

        var current = await rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None);

        Assert.Equal(rig.Descriptor.GatewayPolicy, current.GatewayPolicy);
        Assert.Equal(rig.State.HandlerInstanceId, current.HandlerReceipt.HandlerInstanceId);
        Assert.Equal(2, rig.State.Reads);
        Assert.Equal(2, rig.Projects.AuthorizationReads);
        Assert.Equal(2, rig.Projects.SelectionReads);
        Assert.Equal(0, rig.Network.Reads);
        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable",
            Assert.Throws<RuntimeAuthorizationException>(current.RequireDispatchEnforcement).Code);
    }

    [Fact]
    public async Task DispatchVerificationCannotReturnAnInProcessPolicyAsForcedNetworkEvidence()
    {
        var rig = await CreateAsync();

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Reader.VerifyForDispatchAsync(rig.Call, CancellationToken.None));

        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable", error.Code);
    }

    [Fact]
    public async Task ManagedPolicyReturnsItsActiveDescriptorAndFreshHandlerVerification()
    {
        var rig = await CreateAsync();
        var policy = new RemoteMcpManagedGatewayPolicy();
        var applied = await policy.ApplyAsync(rig.Descriptor, CancellationToken.None);

        var active = await policy.ReadCurrentAsync(rig.Call, CancellationToken.None);

        Assert.Equal(rig.Descriptor, active.Descriptor);
        RemoteMcpManagedGatewayContract.ValidateReceipt(
            rig.Descriptor, RemoteMcpManagedGatewayOperation.Verify, active.Receipt);
        Assert.Equal(applied.HandlerInstanceId, active.Receipt.HandlerInstanceId);
    }

    [Fact]
    public async Task ManagedPolicyDeniesAnotherSnapshotAndRevokedState()
    {
        var rig = await CreateAsync();
        var other = await CreateAsync(snapshotDigest: "other-snapshot", connectionRowRevision: 2);
        var policy = new RemoteMcpManagedGatewayPolicy();
        _ = await policy.ApplyAsync(rig.Descriptor, CancellationToken.None);

        var otherCall = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            policy.ReadCurrentAsync(other.Call, CancellationToken.None));
        Assert.Equal("remote_mcp_managed_handler_unavailable", otherCall.Code);

        _ = await policy.RevokeAsync(rig.Descriptor, CancellationToken.None);
        var revoked = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            policy.ReadCurrentAsync(rig.Call, CancellationToken.None));
        Assert.Equal("remote_mcp_managed_handler_unavailable", revoked.Code);
    }

    [Fact]
    public async Task ACopiedCurrentPolicyResultCannotManufactureForcedMediationProof()
    {
        var rig = await CreateAsync();
        var current = await rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None);
        var assertion = current with { };

        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable",
            Assert.Throws<RuntimeAuthorizationException>(assertion.RequireDispatchEnforcement).Code);
    }

    [Fact]
    public async Task CurrentCallVerificationChecksOwnersThenDeniesWithoutNetworkEnforcement()
    {
        var rig = await CreateAsync();
        var proof = rig.Call.Proof;
        var verification = new RemoteToolResultVerificationReceipt(
            1, SessionMaterialValidation.RemoteToolResultEventId(proof),
            SessionMaterialValidation.RemoteToolResultProofHash(proof));
        var owner = new OwnerHandler(rig.Call.Registration, verification, proof);
        using var client = new HttpClient(owner) { Timeout = TimeSpan.FromSeconds(2) };
        var service = new RemoteMcpCurrentCallVerificationService(
            new OwnerClientFactory(client),
            OrchestratorTlsOptions(),
            new(proof.ActorIssuer),
            rig.Reader,
            TimeProvider.System);
        var context = ActorContext(rig.Call, proof);

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            service.VerifyAsync(context, RemoteMcpCurrentCallContract.CreateRequest(proof), CancellationToken.None));

        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable", error.Code);
        Assert.Collection(owner.Requests,
            request => Assert.Equal(
                (HttpMethod.Get, $"/internal/runtime/registrations/{proof.RuntimeInstanceId:D}"),
                (request.Method, request.Path)),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/internal/runtime/remote-tool-results/verify", request.Path);
                Assert.True(SessionMaterialValidation.SameRemoteToolResultProof(proof, request.Proof));
            });
        Assert.All(owner.Requests, request =>
        {
            Assert.Equal("Bearer original-actor-token", request.Authorization);
            Assert.Equal("tenant-1", request.Tenant);
        });
        Assert.Equal(2, rig.State.Reads);
        Assert.Equal(0, rig.Network.Reads);
    }

    [Fact]
    public async Task InvalidCurrentCallIsRejectedBeforeOwnerOrPolicyReads()
    {
        var rig = await CreateAsync();
        var proof = rig.Call.Proof;
        var owner = new OwnerHandler(rig.Call.Registration,
            new(1, SessionMaterialValidation.RemoteToolResultEventId(proof),
                SessionMaterialValidation.RemoteToolResultProofHash(proof)), proof);
        using var client = new HttpClient(owner) { Timeout = TimeSpan.FromSeconds(2) };
        var service = new RemoteMcpCurrentCallVerificationService(
            new OwnerClientFactory(client),
            OrchestratorTlsOptions(),
            new(proof.ActorIssuer),
            rig.Reader,
            TimeProvider.System);
        var request = RemoteMcpCurrentCallContract.CreateRequest(proof) with
        {
            CallBinding = RemoteMcpCurrentCallContract.CreateBinding(proof) with { Method = "GET" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.VerifyAsync(ActorContext(rig.Call, proof), request, CancellationToken.None));

        Assert.Empty(owner.Requests);
        Assert.Equal(0, rig.State.Reads);
        Assert.Equal(0, rig.Network.Reads);
    }

    [Fact]
    public async Task WrongPortHttpsVerificationRouteDeniesBeforeResolvingVerifierOrCertificate()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddKeyedSingleton(RemoteMcpTrustedServiceTlsComposition.IdentityTlsKey,
            new RuntimeServiceTlsOptions
            {
                LocalServiceName = "Environment",
                PeerServiceName = "IdentityBroker",
                ListenerPort = 9448
            });
        var verifierResolutions = 0;
        builder.Services.AddSingleton<RemoteMcpCurrentCallVerificationService>(_ =>
        {
            verifierResolutions++;
            throw new InvalidOperationException("A denied request must not create the verifier.");
        });
        var certificateResolutions = 0;
        builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
            RemoteMcpTrustedServiceTlsComposition.IdentityTlsKey, (_, _) =>
            {
                certificateResolutions++;
                throw new InvalidOperationException("A denied request must not load the TLS certificate.");
            });
        using var app = builder.Build();
        app.MapRemoteMcpCurrentCallVerification();

        var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>());
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services
        };
        context.Request.IsHttps = true;
        context.Connection.LocalPort = 9449;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        context.Request.ContentLength = 2;
        context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshake(SslProtocols.Tls13));
        await using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context);

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        response.Position = 0;
        using var result = await JsonDocument.ParseAsync(response);
        Assert.Equal("runtime_remote_tool_result_service_listener_denied",
            result.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, verifierResolutions);
        Assert.Equal(0, certificateResolutions);
    }

    [Fact]
    public async Task UnknownLengthVerificationBodyIsBoundedWithoutWritableServerLimit()
    {
        const int listenerPort = 9448;
        const int maximumBytes = 2 * SessionMaterialValidation.MaximumBytes;
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest(
            "CN=remote-mcp-route-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        certificateRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        certificateRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        var extendedKeyUsage = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"),
            new("1.3.6.1.5.5.7.3.2")
        };
        certificateRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(extendedKeyUsage, false));
        using var certificate = certificateRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        var certificatePath = Path.Combine(Path.GetTempPath(), $"remote-mcp-route-{Guid.NewGuid():N}.pem");
        var privateKeyPath = Path.Combine(Path.GetTempPath(), $"remote-mcp-route-{Guid.NewGuid():N}.key");
        File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(privateKeyPath, key.ExportPkcs8PrivateKeyPem());
        try
        {
            var certificateHash = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
            var tlsOptions = new RuntimeServiceTlsOptions
            {
                LocalServiceName = "Environment",
                LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
                LocalCertificatePemPath = certificatePath,
                LocalPrivateKeyPemPath = privateKeyPath,
                LocalCertificateSha256 = certificateHash,
                ListenerPort = listenerPort,
                PeerServiceName = "IdentityBroker",
                PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
                PeerOwnerAddress = "https://identity.test/",
                PeerCertificateSha256Pins = [certificateHash]
            };
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddAuthorization();
            builder.Services.AddKeyedSingleton(RemoteMcpTrustedServiceTlsComposition.IdentityTlsKey, tlsOptions);
            builder.Services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
                RemoteMcpTrustedServiceTlsComposition.IdentityTlsKey,
                (_, _) => new(tlsOptions, "Environment", "IdentityBroker"));
            var verifierResolutions = 0;
            builder.Services.AddSingleton<RemoteMcpCurrentCallVerificationService>(_ =>
            {
                verifierResolutions++;
                throw new InvalidOperationException("A rejected body must not create the verifier.");
            });
            await using var app = builder.Build();
            app.MapRemoteMcpCurrentCallVerification();
            var endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>());

            foreach (var readOnlyFeature in new bool?[] { null, true })
            foreach (var overLimit in new[] { false, true })
            {
                var body = new byte[maximumBytes + (overLimit ? 1 : 0)];
                Array.Fill(body, (byte)' ');
                body[^1] = (byte)'{';
                var bodyStream = new CountingReadStream(body);
                var context = new DefaultHttpContext
                {
                    RequestServices = app.Services
                };
                context.Request.IsHttps = true;
                context.Connection.LocalPort = listenerPort;
                context.Request.ContentType = "application/json";
                context.Request.Body = bodyStream;
                context.Features.Set<ITlsHandshakeFeature>(new TestTlsHandshake(SslProtocols.Tls13));
                context.Features.Set<ITlsConnectionFeature>(new TestTlsConnection(certificate));
                if (readOnlyFeature is true)
                    context.Features.Set<IHttpMaxRequestBodySizeFeature>(new ReadOnlyRequestBodySizeFeature());
                Assert.Null(context.Request.ContentLength);
                if (readOnlyFeature is null)
                    Assert.Null(context.Features.Get<IHttpMaxRequestBodySizeFeature>());
                await using var response = new MemoryStream();
                context.Response.Body = response;

                await endpoint.RequestDelegate!(context);

                Assert.Equal(overLimit ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest,
                    context.Response.StatusCode);
                Assert.Equal(maximumBytes + (overLimit ? 1 : 0), bodyStream.BytesRead);
                response.Position = 0;
                using var result = await JsonDocument.ParseAsync(response);
                Assert.Equal(
                    overLimit ? "remote_mcp_current_verification_request_too_large" :
                        "remote_mcp_current_verification_invalid",
                    result.RootElement.GetProperty("code").GetString());
                Assert.Equal(0, verifierResolutions);
            }
        }
        finally
        {
            File.Delete(certificatePath);
            File.Delete(privateKeyPath);
        }
    }

    [Theory]
    [InlineData("missing-handler", "remote_mcp_managed_handler_unavailable")]
    [InlineData("snapshot", "remote_mcp_managed_handler_binding_invalid")]
    [InlineData("options", "remote_mcp_managed_handler_binding_invalid")]
    [InlineData("gateway", "remote_mcp_managed_handler_binding_invalid")]
    [InlineData("egress", "remote_mcp_managed_handler_binding_invalid")]
    [InlineData("fence", "remote_mcp_managed_handler_binding_invalid")]
    public async Task SavedReferenceOrDifferentHandlerPinsCannotSupplyCurrentGatewayFields(string failure, string code)
    {
        var rig = await CreateAsync();
        rig.State.Failure = failure;

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Equal(0, rig.Network.Reads);
    }

    [Theory]
    [InlineData("handler", "remote_mcp_managed_handler_changed")]
    [InlineData("actor", "remote_mcp_network_read_authority_denied")]
    [InlineData("selection", "remote_mcp_network_authority_changed")]
    [InlineData("expiry", "remote_mcp_native_actor_expired")]
    public async Task ChangedAuthorityDuringReadsCannotReturnAPolicy(string failure, string code)
    {
        var rig = await CreateAsync();
        rig.State.ReplaceHandlerOnSecondRead = failure == "handler";
        rig.Projects.ChangeAfterFirstRead = failure;
        if (failure == "expiry")
            rig.State.AfterRead = count =>
            {
                if (count == 2)
                    rig.Call.Actor.Bearer.Invalidate();
            };

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None));

        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData("provider", "remote_mcp_managed_gateway_selection_invalid")]
    [InlineData("target", "remote_mcp_target_egress_not_authorized")]
    [InlineData("permission", "remote_mcp_network_read_authority_denied")]
    public async Task CurrentPurposePermissionAndSelectedProviderAreRequiredBeforeHandlerReads(string failure, string code)
    {
        var rig = await CreateAsync(failure);

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Equal(0, rig.State.Reads);
        Assert.Equal(0, rig.Network.Reads);
    }

    [Theory]
    [InlineData("http://gateway.test/")]
    [InlineData("https://gateway.test/#fragment")]
    [InlineData("https://gateway.test")]
    public void GatewayOptionsRequireExactCanonicalHttps(string endpoint) =>
        Assert.Throws<ArgumentException>(() =>
            new RemoteMcpManagedGatewayProviderOptions("managed-gateway", "1.0.0", 1, "options-1", endpoint).Validate());

    private static async Task<Rig> CreateAsync(
        string? failure = null, string snapshotDigest = "snapshot", long connectionRowRevision = 1)
    {
        var proof = Proof(snapshotDigest, connectionRowRevision);
        var call = Call(proof);
        var options = new RemoteMcpManagedGatewayProviderOptions(
            "managed-gateway", "1.0.0", 1, "gateway-options-1", "https://gateway.test:9447/");
        var ciliumOptions = EnvironmentEgressManagerTests.Options();
        var capabilities = ImmutableArray.Create(
            CiliumEgressCapabilities.L3L4, CiliumEgressCapabilities.Fqdn,
            CiliumEgressCapabilities.Cidr, CiliumEgressCapabilities.Dns);
        var rules = ImmutableArray.Create(
            new NetworkEgressRule(NetworkEgressPurpose.RemoteMcp, NetworkEgressDestinationKind.Fqdn,
                failure == "target" ? "other.test" : "mcp.example.test", 443, EgressProtocol.Tcp),
            new(NetworkEgressPurpose.DnsResolver, NetworkEgressDestinationKind.KubernetesService,
                "kube-system/kube-dns", 53, EgressProtocol.Tcp),
            new(NetworkEgressPurpose.DnsResolver, NetworkEgressDestinationKind.KubernetesService,
                "kube-system/kube-dns", 53, EgressProtocol.Udp));
        var selected = new EffectiveNetworkPolicySelection(proof.ProjectId, proof.RunId, 1, 1, 1, "context-1",
            [new(ProviderCardinality.Layered, ProviderSeam.NetworkPolicy,
                [new(ProviderSeam.NetworkPolicy, CiliumEgressPolicyAdapter.ProviderId, "1.0.0", 1,
                    ciliumOptions.OptionsRevision, ProviderHostingPattern.KubernetesController, capabilities,
                    capabilities.Where(capability => capability != CiliumEgressCapabilities.Cidr).ToImmutableArray(),
                    NetworkPolicyLayer.L3L4),
                 new(ProviderSeam.NetworkPolicy, failure == "provider" ? "another-provider" : options.ProviderId,
                    options.AdapterVersion, 1, options.OptionsRevision, ProviderHostingPattern.InProcess,
                    [RemoteMcpManagedGatewayProviderOptions.L7Capability, RemoteMcpManagedGatewayProviderOptions.RemoteMcpCapability],
                    [RemoteMcpManagedGatewayProviderOptions.L7Capability, RemoteMcpManagedGatewayProviderOptions.RemoteMcpCapability],
                    NetworkPolicyLayer.L7)])],
            rules, null, rules, rules);
        var authorization = new ProjectAuthorizationContextResponse(
            ProjectAuthorizationContextContract.CurrentVersion, proof.ActorIssuer, proof.ActorId, proof.TenantId,
            1, proof.ProjectId, proof.RunId,
            failure == "permission" ? [] :
                [new(ProjectAuthorityResourceType.Project, proof.ProjectId,
                    [new(ProjectAuthorizationPermission.ReadRunSelection, 1)])]);
        var projects = new Projects(authorization, selected);
        var selector = EnvironmentEgressSelector.Create("environment-1", proof.TenantId, proof.ProjectId, proof.RunId,
            ciliumOptions.Namespace);
        var intent = EgressIntentCompiler.Compile(selected).Intent!;
        var fence = new EnvironmentGenerationFence(new(proof.TenantId, proof.ProjectId, proof.RunId, "environment-1"), 1);
        var descriptor = new RemoteMcpManagedGatewayGateDescriptor(1,
            new(new("actual-managed-policy-1", fence, 1, EgressIntentCompiler.Hash(intent, selector)),
                new(ProviderSeam.NetworkPolicy, options.ProviderId, "owned-handler-resource-1", 1),
                options.AdapterVersion, 1, options.OptionsRevision, options.Sha256(),
                Hash(options.GatewayEndpoint), Hash(proof.SnapshotBinding.EndpointUri)),
            proof.SnapshotBinding);
        var state = new State(descriptor);
        var network = new ReadOnlyNetwork();
        var lifecycle = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var reservation = await lifecycle.ReserveNetworkEffectAsync(fence,
            $"{selector.Namespace}/{selector.PolicyName}", 1, 0, EnvironmentNetworkEffectKind.Apply,
            "fixture-applied", CancellationToken.None);
        await lifecycle.CompleteNetworkEffectAsync(reservation.OperationId, fence, true, true, CancellationToken.None);
        return new(new(projects, lifecycle, new(network, ciliumOptions), ciliumOptions, options, state),
            call, descriptor, projects, state, network);
    }

    private static RemoteMcpNativeCallContext Call(RemoteToolResultProof proof)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new("sub", proof.ActorId), new("exp", expiry.ToUnixTimeSeconds().ToString())], "validated"));
        var registration = new RuntimeRegistration(proof.RuntimeInstanceId, 1,
            new(proof.ActorIssuer, proof.ActorId, proof.TenantId, proof.ProjectId, proof.RunId, proof.SessionId,
                "agent-1", "turn-1", 1, 1, 1, "context-1", proof.AcceptedSelectionHash, 1,
                "environment-1", "placement-1", 1, "profile-1", new("https://sandbox.test/configure"),
                new("https://sandbox.test/observe"))
            {
                WorkflowStepId = proof.StepId,
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 1,
                EnvironmentCurrentFencingGeneration = 1,
                EnvironmentProviderFencingGeneration = 1,
                PlacementProviderId = "sandbox-provider-1"
            },
            RuntimeRegistrationState.Active, expiry);
        return RemoteMcpNativeCallContext.CreateVerified(principal, proof.ActorIssuer, proof, registration,
            new(1, SessionMaterialValidation.RemoteToolResultEventId(proof), SessionMaterialValidation.RemoteToolResultProofHash(proof)),
            new(new SecretCredential("original-actor-token", expiry), proof.TenantId), TimeProvider.System);
    }

    private static RemoteToolResultProof Proof(string snapshotDigest = "snapshot", long connectionRowRevision = 1)
    {
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string arguments = """{"query":"x"}""";
        return new(1, eventId, Hash("input"), "native-call-1",
            new("project-1", Guid.Parse("44444444-4444-4444-4444-444444444444"), Hash(snapshotDigest), "agent-1", "node-1"),
            new("connection-1", connectionRowRevision, 2, 3, "Enabled", Hash("configuration"), "https://mcp.example.test/api", null,
                "None", null, "StreamableHttp20250618", null, null, null, "catalog-1", Hash("catalog"),
                "remote.lookup", "tool-1", Hash("tool"), "schema-1", Hash("schema"), "tool.read", "runtime.execution", Hash("metadata")),
            "remote.lookup", arguments, Hash(arguments), Guid.Parse("22222222-2222-2222-2222-222222222222"), 1,
            "https://identity.test/", "11111111-1111-1111-1111-111111111111", "tenant-1", "project-1", "run-1",
            "session-1", "step-1", 1, 1, 1, "context-1", Hash("selection"), 1, "tool.read", "runtime.execution",
            1, true, eventId.ToString("N"), "1", eventId, PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed,
            SessionMaterialResultContentClass.TextOnly);
    }

    private static string Hash(string text) => RemoteMcpManagedGatewayProviderOptions.Hash(Encoding.UTF8.GetBytes(text));

    private sealed class TestTlsHandshake(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private sealed class TestTlsConnection(X509Certificate2 clientCertificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = clientCertificate;

        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ClientCertificate);
    }

    private sealed class ReadOnlyRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public long? MaxRequestBodySize { get; set; } = 1;
        public bool IsReadOnly => true;
    }

    private sealed class CountingReadStream(byte[] content) : MemoryStream(content)
    {
        public int BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }
    }

    private static DefaultHttpContext ActorContext(
        RemoteMcpNativeCallContext call, RemoteToolResultProof proof)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new("sub", proof.ActorId), new("exp", call.ExpiresAt.ToUnixTimeSeconds().ToString())], "validated"));
        context.Request.Headers.Authorization = "Bearer original-actor-token";
        context.Request.Headers["X-Agentweaver-Tenant"] = proof.TenantId;
        return context;
    }

    private static RuntimeServiceTlsOptions OrchestratorTlsOptions() => new()
    {
        LocalServiceName = "Environment",
        LocalServicePrincipalId = "11111111-1111-1111-1111-111111111111",
        LocalCertificatePemPath = @"C:\environment.pem",
        LocalPrivateKeyPemPath = @"C:\environment.key",
        LocalCertificateSha256 = new('a', 64),
        ListenerPort = 5001,
        PeerServiceName = "Orchestrator",
        PeerServicePrincipalId = "22222222-2222-2222-2222-222222222222",
        PeerOwnerAddress = "https://orchestrator.test/",
        PeerCertificateSha256Pins = [new('b', 64)]
    };

    private sealed class Projects(ProjectAuthorizationContextResponse authorization, EffectiveNetworkPolicySelection selection)
        : IProjectsConfigClient
    {
        public string? ChangeAfterFirstRead { get; set; }
        public int AuthorizationReads { get; private set; }
        public int SelectionReads { get; private set; }
        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(CurrentCallerRequest caller, CancellationToken token)
        {
            Assert.Equal("original-actor-token", caller.BearerToken);
            Assert.Equal("tenant-1", caller.TenantSelector);
            AuthorizationReads++;
            return Task.FromResult(AuthorizationReads > 1 && ChangeAfterFirstRead == "actor"
                ? authorization with { ActorId = Guid.NewGuid().ToString("D") } : authorization);
        }
        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller, string projectId, string runId, CancellationToken token)
        {
            SelectionReads++;
            return Task.FromResult(SelectionReads > 1 && ChangeAfterFirstRead == "selection"
                ? selection with { EgressBaseline = selection.EgressBaseline.Add(
                    new(NetworkEgressPurpose.RemoteMcp, NetworkEgressDestinationKind.Fqdn, "another.test", 443, EgressProtocol.Tcp)) }
                : selection);
        }
    }

    private sealed class State(RemoteMcpManagedGatewayGateDescriptor descriptor) : IRemoteMcpManagedGatewayStateReader
    {
        public Guid HandlerInstanceId { get; } = Guid.NewGuid();
        public int Reads { get; private set; }
        public string? Failure { get; set; }
        public bool ReplaceHandlerOnSecondRead { get; set; }
        public Action<int>? AfterRead { get; set; }
        public Task<RemoteMcpManagedGatewayActiveState> ReadCurrentAsync(RemoteMcpNativeCallContext call, CancellationToken token)
        {
            Reads++;
            if (Failure == "missing-handler")
                throw new RuntimeAuthorizationException("remote_mcp_managed_handler_unavailable");
            var policy = descriptor.GatewayPolicy;
            var current = Failure switch
            {
                "snapshot" => descriptor with { SnapshotBinding = descriptor.SnapshotBinding with { ConnectionRowRevision = 99 } },
                "options" => descriptor with { GatewayPolicy = policy with { OptionsSha256 = Hash("other-options") } },
                "gateway" => descriptor with { GatewayPolicy = policy with { GatewayEndpointSha256 = Hash("other-gateway") } },
                "egress" => descriptor with { GatewayPolicy = policy with
                {
                    AppliedPolicy = new(policy.AppliedPolicy.Reference, policy.AppliedPolicy.EnvironmentFence, 1, Hash("other-egress"))
                } },
                "fence" => descriptor with { GatewayPolicy = policy with
                {
                    AppliedPolicy = new(policy.AppliedPolicy.Reference,
                        new(policy.AppliedPolicy.EnvironmentFence.Owner, 99), 1, policy.AppliedPolicy.EgressIntentSha256)
                } },
                _ => descriptor
            };
            AfterRead?.Invoke(Reads);
            return Task.FromResult(new RemoteMcpManagedGatewayActiveState(current,
                new(1, RemoteMcpManagedGatewayOperation.Verify, RemoteMcpManagedGatewayContract.DescriptorHash(current),
                    ReplaceHandlerOnSecondRead && Reads > 1 ? Guid.NewGuid() : HandlerInstanceId,
                    RemoteMcpManagedGatewayGateState.Active)));
        }
    }

    private sealed class ReadOnlyNetwork : ICiliumPolicyResourceStore
    {
        public int Reads { get; private set; }
        public Task<CiliumNetworkPolicyDocument?> GetAsync(string ns, string name, CancellationToken token)
        {
            Reads++;
            throw new InvalidOperationException("A current L7 handler read must not claim Cilium L3/L4 enforcement.");
        }
        public Task<CiliumNetworkPolicyDocument> CreateAsync(CiliumNetworkPolicyDocument policy, CancellationToken token) =>
            throw new InvalidOperationException("The current-policy reader must not create network policies.");
        public Task<CiliumNetworkPolicyDocument> ReplaceAsync(CiliumNetworkPolicyDocument policy, string version, CancellationToken token) =>
            throw new InvalidOperationException("The current-policy reader must not replace network policies.");
        public Task DeleteAsync(string ns, string name, string version, CancellationToken token) =>
            throw new InvalidOperationException("The current-policy reader must not delete network policies.");
    }

    private sealed record OwnerRequest(
        HttpMethod Method, string Path, string? Authorization, string? Tenant, RemoteToolResultProof? Proof);

    private sealed class OwnerClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(RemoteMcpTrustedServiceTlsComposition.OrchestratorClientName, name);
            return client;
        }
    }

    private sealed class OwnerHandler(
        RuntimeRegistration registration,
        RemoteToolResultVerificationReceipt verification,
        RemoteToolResultProof expectedProof) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        public List<OwnerRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            RemoteToolResultProof? proof = null;
            object response;
            if (request.Method == HttpMethod.Get &&
                path == $"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}")
            {
                Assert.Null(proof);
                response = registration;
            }
            else if (request.Method == HttpMethod.Post &&
                path == "/internal/runtime/remote-tool-results/verify")
            {
                proof = await request.Content!.ReadFromJsonAsync<RemoteToolResultProof>(
                    JsonOptions, cancellationToken);
                Assert.True(SessionMaterialValidation.SameRemoteToolResultProof(expectedProof, proof));
                response = verification;
            }
            else
            {
                throw new InvalidOperationException("Unexpected owner request.");
            }

            Requests.Add(new(request.Method, path, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                    ? tenant.SingleOrDefault()
                    : null,
                proof));
            var message = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(response, options: JsonOptions)
            };
            message.Headers.CacheControl = new() { NoStore = true };
            return message;
        }
    }

    private sealed record Rig(RemoteMcpCurrentGatewayPolicyReader Reader, RemoteMcpNativeCallContext Call,
        RemoteMcpManagedGatewayGateDescriptor Descriptor, Projects Projects, State State, ReadOnlyNetwork Network);
}
