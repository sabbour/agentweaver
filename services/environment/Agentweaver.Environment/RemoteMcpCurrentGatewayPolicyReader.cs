using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Versioned configuration for the in-process managed Remote MCP gateway policy handler.</summary>
public sealed record RemoteMcpManagedGatewayProviderOptions(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    string GatewayEndpoint)
{
    public const string L7Capability = "networkpolicy.l7";
    public const string RemoteMcpCapability = "networkpolicy.remote-mcp";

    /// <summary>Validates explicit provider metadata and a canonical HTTPS gateway endpoint.</summary>
    public RemoteMcpManagedGatewayProviderOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || ProviderId.Length > 256 || ProviderId.Any(char.IsControl) ||
            !Version.TryParse(AdapterVersion, out _) || OptionsSchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(OptionsRevision) || OptionsRevision.Length > 256 ||
            OptionsRevision.Any(char.IsControl) ||
            RemoteMcpUri.CanonicalizeHttps(GatewayEndpoint) != GatewayEndpoint)
            throw new ArgumentException("Explicit versioned managed gateway options and a canonical HTTPS endpoint are required.");
        return this;
    }

    /// <summary>Hashes the validated provider settings so policy receipts can pin the exact configuration.</summary>
    public string Sha256()
    {
        Validate();
        return Hash(JsonSerializer.SerializeToUtf8Bytes(this));
    }

    /// <summary>Builds the provider registration and its declared L7 and Remote MCP capabilities.</summary>
    public ProviderRegistration CreateRegistration()
    {
        Validate();
        return new(new(ProviderSeam.NetworkPolicy, ProviderId, Version.Parse(AdapterVersion),
            OptionsSchemaVersion, ProviderHostingPattern.InProcess,
            ImmutableHashSet.Create(StringComparer.Ordinal, L7Capability, RemoteMcpCapability)),
            true, OptionsRevision, OptionsSchemaVersion);
    }

    /// <summary>Returns the lowercase SHA-256 digest used to pin managed gateway provider options.</summary>
    internal static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

/// <summary>Reports the active gateway descriptor and the handler's current verification receipt.</summary>
public sealed record RemoteMcpManagedGatewayActiveState(
    RemoteMcpManagedGatewayGateDescriptor Descriptor,
    RemoteMcpManagedGatewayPolicyReceipt Receipt);

// The managed handler returns its actual current descriptor and a fresh Verify receipt.
/// <summary>Reads the exact gateway policy currently enforced by the managed handler.</summary>
public interface IRemoteMcpManagedGatewayStateReader
{
    /// <summary>Returns current state for the supplied live call after validating its actor context.</summary>
    Task<RemoteMcpManagedGatewayActiveState> ReadCurrentAsync(
        RemoteMcpNativeCallContext currentCall, CancellationToken cancellationToken);
}

/// <summary>Pairs a current gateway policy binding with evidence from its managed handler.</summary>
public sealed record RemoteMcpCurrentGatewayPolicyReadResult(
    RemoteMcpCurrentGatewayPolicyBinding GatewayPolicy,
    RemoteMcpManagedGatewayPolicyReceipt HandlerReceipt)
{
    // This reader has object and handler evidence, not a forced-mediation proof producer.
    /// <summary>Fails closed because a policy record and handler receipt do not prove forced network mediation.</summary>
    public void RequireDispatchEnforcement() =>
        throw new RuntimeAuthorizationException("remote_mcp_forced_network_enforcement_unavailable");
}

/// <summary>
/// Re-reads owner authorization, selected egress policy, Cilium state, and gateway state before a Remote MCP dispatch.
/// This reader intentionally does not claim that those observations prove forced L7 mediation.
/// </summary>
public sealed class RemoteMcpCurrentGatewayPolicyReader(
    IProjectsConfigClient projects,
    IEnvironmentLifecycleStore lifecycle,
    CiliumEgressPolicyAdapter cilium,
    CiliumEgressProviderOptions ciliumOptions,
    RemoteMcpManagedGatewayProviderOptions gatewayOptions,
    IRemoteMcpManagedGatewayStateReader managedState)
{
    /// <summary>Reads and compares the current network and gateway bindings against the accepted tool proof.</summary>
    public async Task<RemoteMcpCurrentGatewayPolicyReadResult> ReadCurrentAsync(
        RemoteMcpNativeCallContext currentCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentCall);
        currentCall.RequireUsable();
        var options = gatewayOptions.Validate();
        var proof = currentCall.Proof;
        var runtime = currentCall.Registration.Binding;
        if (runtime.EnvironmentLifecycleGeneration <= 0 || string.IsNullOrWhiteSpace(runtime.EnvironmentId))
            throw new RuntimeAuthorizationException("remote_mcp_environment_binding_invalid");
        var fence = new EnvironmentGenerationFence(
            new(proof.TenantId, proof.ProjectId, proof.RunId, runtime.EnvironmentId),
            runtime.EnvironmentLifecycleGeneration);
        var caller = new CurrentCallerRequest(currentCall.Actor.Bearer.GetValue(), proof.TenantId);
        await lifecycle.RequireActiveAsync(fence, cancellationToken).ConfigureAwait(false);
        var authorization = await projects.GetAuthorizationContextAsync(caller, cancellationToken).ConfigureAwait(false);
        RequireAuthorization(authorization, proof);
        var selection = await projects.GetRunSelectionAsync(caller, proof.ProjectId, proof.RunId, cancellationToken)
            .ConfigureAwait(false);
        RequireSelection(selection, proof, options);
        var compiled = EgressIntentCompiler.Compile(selection);
        if (!compiled.IsSuccess)
            throw new RuntimeAuthorizationException("remote_mcp_egress_intent_invalid");
        var intent = compiled.Intent!;
        var selectedL3 = selection.Providers.Single(provider => provider.Seam == ProviderSeam.NetworkPolicy)
            .Candidates.Single(candidate => candidate.Layer == NetworkPolicyLayer.L3L4);
        var l3Capabilities = CiliumEgressPolicyAdapter.GetVerifiedCapabilities(intent);
        if (selectedL3.AdvertisedCapabilities.IsDefault || selectedL3.RequiredCapabilities.IsDefault ||
            !l3Capabilities.IsSubsetOf(selectedL3.AdvertisedCapabilities) ||
            !selectedL3.RequiredCapabilities.All(l3Capabilities.Contains))
            throw new RuntimeAuthorizationException("remote_mcp_l3_capabilities_invalid");
        var target = new Uri(proof.SnapshotBinding.EndpointUri, UriKind.Absolute);
        if (!NetworkEgressRuleSemantics.IsContainedBy(
                new(NetworkEgressPurpose.RemoteMcp, NetworkEgressDestinationKind.Fqdn,
                    target.IdnHost, target.Port, EgressProtocol.Tcp),
                intent.Rules))
            throw new RuntimeAuthorizationException("remote_mcp_target_egress_not_authorized");
        var selector = EnvironmentEgressSelector.Create(
            runtime.EnvironmentId, proof.TenantId, proof.ProjectId, proof.RunId, ciliumOptions.Namespace);
        var intentHash = EgressIntentCompiler.Hash(intent, selector);
        var state = await managedState.ReadCurrentAsync(currentCall, cancellationToken).ConfigureAwait(false);
        RequireState(state, proof, fence, options, intentHash);
        var binding = state.Descriptor.GatewayPolicy;
        await lifecycle.ReadVerifiedNetworkPolicyGenerationAsync(
            fence, $"{selector.Namespace}/{selector.PolicyName}",
            binding.AppliedPolicy.AppliedGeneration, cancellationToken).ConfigureAwait(false);
        await lifecycle.RequireActiveAsync(fence, cancellationToken).ConfigureAwait(false);
        var currentAuthorization = await projects.GetAuthorizationContextAsync(caller, cancellationToken).ConfigureAwait(false);
        RequireAuthorization(currentAuthorization, proof);
        var currentSelection = await projects.GetRunSelectionAsync(caller, proof.ProjectId, proof.RunId, cancellationToken)
            .ConfigureAwait(false);
        RequireSelection(currentSelection, proof, options);
        if (!EnvironmentEgressManager.SameAuthorizationContext(authorization, currentAuthorization) ||
            SelectionHash(selection) != SelectionHash(currentSelection))
            throw new RuntimeAuthorizationException("remote_mcp_network_authority_changed");
        var currentState = await managedState.ReadCurrentAsync(currentCall, cancellationToken).ConfigureAwait(false);
        RequireState(currentState, proof, fence, options, intentHash);
        if (RemoteMcpManagedGatewayContract.DescriptorHash(state.Descriptor) !=
                RemoteMcpManagedGatewayContract.DescriptorHash(currentState.Descriptor) ||
            state.Receipt.HandlerInstanceId != currentState.Receipt.HandlerInstanceId)
            throw new RuntimeAuthorizationException("remote_mcp_managed_handler_changed");
        await lifecycle.ReadVerifiedNetworkPolicyGenerationAsync(
            fence, $"{selector.Namespace}/{selector.PolicyName}",
            binding.AppliedPolicy.AppliedGeneration, cancellationToken).ConfigureAwait(false);
        await lifecycle.RequireActiveAsync(fence, cancellationToken).ConfigureAwait(false);
        currentCall.RequireUsable();
        // Owner-recorded network generation and handler state do not establish forced mediation.
        return new(binding, currentState.Receipt);
    }

    /// <summary>Rejects dispatch until the current result can prove forced network enforcement.</summary>
    public async Task<RemoteMcpCurrentGatewayPolicyBinding> VerifyForDispatchAsync(
        RemoteMcpNativeCallContext currentCall, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(currentCall, cancellationToken).ConfigureAwait(false);
        current.RequireDispatchEnforcement();
        return current.GatewayPolicy;
    }

    /// <summary>Requires exactly one layered L3/L4 and L7 network provider selection matching pinned revisions.</summary>
    private void RequireSelection(
        EffectiveNetworkPolicySelection selection, RemoteToolResultProof proof,
        RemoteMcpManagedGatewayProviderOptions options)
    {
        if (selection.ProjectId != proof.ProjectId || selection.RunId != proof.RunId ||
            selection.ProjectRevision != proof.ProjectRevision ||
            selection.ProjectConfigurationRevision != proof.ProjectConfigurationRevision ||
            selection.PlatformRuntimeRevision != proof.PlatformRuntimeRevision ||
            selection.ContextRevision != proof.ContextRevision || selection.Providers.IsDefault)
            throw new RuntimeAuthorizationException("remote_mcp_network_selection_invalid");
        var providers = selection.Providers.Where(provider => provider?.Seam == ProviderSeam.NetworkPolicy).ToArray();
        if (providers.Length != 1 || providers[0].Cardinality != ProviderCardinality.Layered ||
            providers[0].Candidates.IsDefault || providers[0].Candidates.Length != 2)
            throw new RuntimeAuthorizationException("remote_mcp_layered_network_selection_invalid");
        var l3 = providers[0].Candidates.Where(candidate => candidate?.Layer == NetworkPolicyLayer.L3L4).ToArray();
        var l7 = providers[0].Candidates.Where(candidate => candidate?.Layer == NetworkPolicyLayer.L7).ToArray();
        if (l3.Length != 1 || l7.Length != 1)
            throw new RuntimeAuthorizationException("remote_mcp_layered_network_selection_invalid");
        _ = cilium.ValidateOptions(l3[0]);
        var selected = l7[0];
        var capabilities = ImmutableHashSet.Create(StringComparer.Ordinal,
            RemoteMcpManagedGatewayProviderOptions.L7Capability, RemoteMcpManagedGatewayProviderOptions.RemoteMcpCapability);
        if (selected.Seam != ProviderSeam.NetworkPolicy || selected.ProviderId != options.ProviderId ||
            selected.AdapterVersion != options.AdapterVersion ||
            selected.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            selected.OptionsRevision != options.OptionsRevision ||
            selected.Hosting != ProviderHostingPattern.InProcess ||
            selected.AdvertisedCapabilities.IsDefault || selected.RequiredCapabilities.IsDefault ||
            !capabilities.IsSubsetOf(selected.AdvertisedCapabilities) ||
            !selected.RequiredCapabilities.All(capabilities.Contains))
            throw new RuntimeAuthorizationException("remote_mcp_managed_gateway_selection_invalid");
    }

    private static void RequireState(
        RemoteMcpManagedGatewayActiveState state, RemoteToolResultProof proof,
        EnvironmentGenerationFence fence, RemoteMcpManagedGatewayProviderOptions options, string intentHash)
    {
        if (state?.Descriptor is null || state.Receipt is null)
            throw new RuntimeAuthorizationException("remote_mcp_managed_handler_unavailable");
        RemoteMcpManagedGatewayContract.ValidateReceipt(
            state.Descriptor, RemoteMcpManagedGatewayOperation.Verify, state.Receipt);
        var policy = state.Descriptor.GatewayPolicy;
        if (state.Descriptor.SnapshotBinding != proof.SnapshotBinding ||
            policy.AppliedPolicy.EnvironmentFence != fence ||
            policy.AppliedPolicy.EgressIntentSha256 != intentHash ||
            policy.PolicyResource.ProviderId != options.ProviderId ||
            policy.AdapterVersion != options.AdapterVersion ||
            policy.OptionsSchemaVersion != options.OptionsSchemaVersion ||
            policy.OptionsRevision != options.OptionsRevision || policy.OptionsSha256 != options.Sha256() ||
            policy.GatewayEndpointSha256 != RemoteMcpManagedGatewayProviderOptions.Hash(Encoding.UTF8.GetBytes(options.GatewayEndpoint)))
            throw new RuntimeAuthorizationException("remote_mcp_managed_handler_binding_invalid");
    }

    private static void RequireAuthorization(ProjectAuthorizationContextResponse authorization, RemoteToolResultProof proof)
    {
        if (authorization.ContractVersion != ProjectAuthorizationContextContract.CurrentVersion ||
            authorization.ActorId != proof.ActorId || authorization.Issuer != proof.ActorIssuer ||
            authorization.TenantId != proof.TenantId || authorization.MembershipRevision <= 0 ||
            authorization.BoundProjectId is { } project && project != proof.ProjectId ||
            authorization.BoundRunId is { } run && (run != proof.RunId || authorization.BoundProjectId != proof.ProjectId) ||
            authorization.EffectiveAuthority.IsDefault ||
            !authorization.EffectiveAuthority.Any(resource =>
                resource?.ResourceType == ProjectAuthorityResourceType.Project &&
                resource.ResourceId == proof.ProjectId && !resource.Permissions.IsDefault &&
                resource.Permissions.Any(grant =>
                    grant?.Permission == ProjectAuthorizationPermission.ReadRunSelection && grant.RoleRevision > 0)))
            throw new RuntimeAuthorizationException("remote_mcp_network_read_authority_denied");
    }

    private static string SelectionHash(EffectiveNetworkPolicySelection selection) =>
        RemoteMcpManagedGatewayProviderOptions.Hash(JsonSerializer.SerializeToUtf8Bytes(selection));
}
