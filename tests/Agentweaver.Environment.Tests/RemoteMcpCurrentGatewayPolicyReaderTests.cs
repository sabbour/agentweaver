using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
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
    public async Task ACopiedCurrentPolicyResultCannotManufactureForcedMediationProof()
    {
        var rig = await CreateAsync();
        var current = await rig.Reader.ReadCurrentAsync(rig.Call, CancellationToken.None);
        var assertion = current with { };

        Assert.Equal("remote_mcp_forced_network_enforcement_unavailable",
            Assert.Throws<RuntimeAuthorizationException>(assertion.RequireDispatchEnforcement).Code);
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

    private static async Task<Rig> CreateAsync(string? failure = null)
    {
        var proof = Proof();
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
            { WorkflowStepId = proof.StepId, EnvironmentLifecycleGeneration = 1 },
            RuntimeRegistrationState.Active, expiry);
        return RemoteMcpNativeCallContext.CreateVerified(principal, proof.ActorIssuer, proof, registration,
            new(1, SessionMaterialValidation.RemoteToolResultEventId(proof), SessionMaterialValidation.RemoteToolResultProofHash(proof)),
            new(new SecretCredential("original-actor-token", expiry), proof.TenantId), TimeProvider.System);
    }

    private static RemoteToolResultProof Proof()
    {
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        const string arguments = """{"query":"x"}""";
        return new(1, eventId, Hash("input"), "native-call-1",
            new("project-1", Guid.Parse("44444444-4444-4444-4444-444444444444"), Hash("snapshot"), "agent-1", "node-1"),
            new("connection-1", 1, 2, 3, "Enabled", Hash("configuration"), "https://mcp.example.test/api", null,
                "None", null, "StreamableHttp20250618", null, null, null, "catalog-1", Hash("catalog"),
                "remote.lookup", "tool-1", Hash("tool"), "schema-1", Hash("schema"), "tool.read", "runtime.execution", Hash("metadata")),
            "remote.lookup", arguments, Hash(arguments), Guid.Parse("22222222-2222-2222-2222-222222222222"), 1,
            "https://identity.test/", "11111111-1111-1111-1111-111111111111", "tenant-1", "project-1", "run-1",
            "session-1", "step-1", 1, 1, 1, "context-1", Hash("selection"), 1, "tool.read", "runtime.execution",
            1, true, eventId.ToString("N"), "1", eventId, PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed,
            SessionMaterialResultContentClass.TextOnly);
    }

    private static string Hash(string text) => RemoteMcpManagedGatewayProviderOptions.Hash(Encoding.UTF8.GetBytes(text));

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

    private sealed record Rig(RemoteMcpCurrentGatewayPolicyReader Reader, RemoteMcpNativeCallContext Call,
        RemoteMcpManagedGatewayGateDescriptor Descriptor, Projects Projects, State State, ReadOnlyNetwork Network);
}
