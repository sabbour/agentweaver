using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Sandbox.AgentSandbox;

/// <summary>Builds stable Kubernetes policy names for command and output-collector operations.</summary>
public static class AgentSandboxBuildTestPolicyNames
{
    /// <summary>Gets the command-policy name for a non-empty operation identifier.</summary>
    public static string Command(Guid operationId) => Get(operationId, SandboxBuildTestLimits.CommandPolicyRole);
    /// <summary>Gets the collector-policy name for a non-empty operation identifier.</summary>
    public static string Collector(Guid operationId) => Get(operationId, SandboxBuildTestLimits.CollectorPolicyRole);

    /// <summary>Validates the operation ID and composes a bounded role-specific policy name.</summary>
    private static string Get(Guid operationId, string role)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A BuildTest operation ID is required.", nameof(operationId));
        return $"buildtest-{operationId:N}-{role}-policy";
    }
}

/// <summary>
/// Creates and observes isolated BuildTest command and output-collector Pods.
/// Every Pod starts behind a scheduling gate and is admitted only after runtime, PVC, policy, owner, and manifest checks.
/// </summary>
public sealed partial class AgentSandboxProvider
{
    private const string BuildTestPodNamePrefix = "buildtest-";
    private const string BuildTestPolicyApiGroup = "cilium.io";
    private const string BuildTestPolicyApiVersion = "v2";
    private const string BuildTestPolicyResource = "ciliumnetworkpolicies";
    private const string BuildTestGateName = "agentweaver.dev/buildtest-not-authorized";
    private const string BuildTestManagedLabel = "agentweaver.dev/managed-buildtest";
    private const string BuildTestOwnerLabel = "agentweaver.dev/buildtest-owner";
    private const string BuildTestLifecycleLabel = "agentweaver.dev/buildtest-lifecycle";
    private const string BuildTestSandboxGenerationLabel = "agentweaver.dev/buildtest-sandbox-generation";
    private const string BuildTestFencingGenerationLabel = "agentweaver.dev/buildtest-fencing-generation";
    private const string BuildTestOwnerAnnotation = "agentweaver.dev/buildtest-owner";
    private const string BuildTestImmutableHashAnnotation = "agentweaver.dev/buildtest-immutable-hash";
    private const string BuildTestRequestFingerprintAnnotation = "agentweaver.dev/buildtest-request-fingerprint";
    private const string BuildTestSandboxUidAnnotation = "agentweaver.dev/buildtest-sandbox-uid";
    private const string BuildTestWorkspaceUidAnnotation = "agentweaver.dev/buildtest-workspace-uid";
    private const string BuildTestPolicyUidAnnotation = "agentweaver.dev/buildtest-policy-uid";
    private const string BuildTestPolicyHashAnnotation = "agentweaver.dev/buildtest-policy-sha256";
    private const string BuildTestWorkspaceVolumeName = "buildtest-workspace";
    private const string BuildTestCommandContainerName = "buildtest-command";
    private const string BuildTestCollectorPodUidEnvironmentVariable = "AGENTWEAVER_COLLECTOR_POD_UID";
    private const int BuildTestContainerUser = 1000;

    private static readonly JsonSerializerOptions BuildTestJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 16
        };

    /// <summary>Creates or recovers a command Pod after dry-run admission and complete policy validation.</summary>
    /// <param name="request">Accepted command, immutable provider binding, and command network-policy pin.</param>
    /// <param name="cancellationToken">Cancels Kubernetes reads and writes.</param>
    /// <returns>The exact command Pod reference with UID and resource version.</returns>
    /// <exception cref="SandboxProviderException">The pinned runtime, PVC, network policy, or Pod manifest is invalid.</exception>
    public async Task<SandboxBuildTestPodReference> CreateGatedAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var context = await VerifyBuildTestContextAsync(
            request,
            SandboxBuildTestLimits.CommandPolicyRole,
            cancellationToken).ConfigureAwait(false);
        var podName = BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CommandPolicyRole);
        var existing = await _client.GetAsync(
            string.Empty, "pods", context.Descriptor.Namespace, podName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return ValidateBuildTestPod(existing.Value, request, context, SandboxBuildTestLimits.CommandPolicyRole);

        var desired = BuildTestPodManifest(request, context, SandboxBuildTestLimits.CommandPolicyRole, null);
        var admitted = await _client.DryRunCreatePodAsync(
            context.Descriptor.Namespace,
            desired,
            BuildTestGateName,
            cancellationToken).ConfigureAwait(false);
        _ = ValidateBuildTestPod(
            admitted,
            request,
            context,
            SandboxBuildTestLimits.CommandPolicyRole,
            requireGate: true);
        return await CreateBuildTestPodAsync(
            desired,
            request,
            context,
            SandboxBuildTestLimits.CommandPolicyRole,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or recovers a gated, separately policy-bound output collector after command success is proven.</summary>
    /// <param name="request">Accepted command and immutable provider binding.</param>
    /// <param name="collectorRequest">Bounded output request matching the accepted command obligations.</param>
    /// <param name="cancellationToken">Cancels Kubernetes reads and writes.</param>
    /// <returns>The exact output-collector Pod reference.</returns>
    /// <exception cref="SandboxProviderException">Command success, collector policy, or Pod validation fails.</exception>
    public async Task<SandboxBuildTestPodReference> CreateOutputCollectorGatedAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(collectorRequest);
        collectorRequest = collectorRequest.Validate();
        var context = ReadBuildTestContext(request);
        ValidateCollectorRequest(request, collectorRequest, context);
        context = await VerifyBuildTestContextAsync(
            request,
            SandboxBuildTestLimits.CollectorPolicyRole,
            cancellationToken,
            context).ConfigureAwait(false);
        await RequireSuccessfulCommandPodAsync(request, context, cancellationToken).ConfigureAwait(false);

        var podName = BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CollectorPolicyRole);
        var existing = await _client.GetAsync(
            string.Empty, "pods", context.Descriptor.Namespace, podName, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return ValidateBuildTestPod(
                existing.Value, request, context, SandboxBuildTestLimits.CollectorPolicyRole, collectorRequest);

        var desired = BuildTestPodManifest(
            request,
            context,
            SandboxBuildTestLimits.CollectorPolicyRole,
            collectorRequest);
        var admitted = await _client.DryRunCreatePodAsync(
            context.Descriptor.Namespace,
            desired,
            BuildTestGateName,
            cancellationToken).ConfigureAwait(false);
        _ = ValidateBuildTestPod(
            admitted,
            request,
            context,
            SandboxBuildTestLimits.CollectorPolicyRole,
            collectorRequest,
            requireGate: true);
        return await CreateBuildTestPodAsync(
            desired,
            request,
            context,
            SandboxBuildTestLimits.CollectorPolicyRole,
            cancellationToken,
            collectorRequest).ConfigureAwait(false);
    }

    /// <summary>Reads the command Pod for its accepted operation, returning null when it is absent.</summary>
    public Task<SandboxBuildTestPodReference?> GetAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken) =>
        GetBuildTestPodAsync(request, SandboxBuildTestLimits.CommandPolicyRole, cancellationToken);

    /// <summary>Reads the output-collector Pod for its accepted operation, returning null when it is absent.</summary>
    public Task<SandboxBuildTestPodReference?> GetOutputCollectorAsync(
        SandboxBuildTestProviderRequest request,
        CancellationToken cancellationToken) =>
        GetBuildTestPodAsync(request, SandboxBuildTestLimits.CollectorPolicyRole, cancellationToken);

    /// <summary>Reads and validates an operation's Pod for the requested command or collector role.</summary>
    private async Task<SandboxBuildTestPodReference?> GetBuildTestPodAsync(
        SandboxBuildTestProviderRequest request,
        string role,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var context = ReadBuildTestContext(request);
        await VerifyBuildTestClusterIdentityAsync(context, cancellationToken).ConfigureAwait(false);
        var podName = BuildTestPodName(request.AcceptedCommand.OperationId, role);
        var pod = await _client.GetAsync(
            string.Empty, "pods", context.Descriptor.Namespace, podName, cancellationToken).ConfigureAwait(false);
        return pod is null
            ? null
            : ValidateBuildTestPod(pod.Value, request, context, role);
    }

    /// <summary>Removes the single authorization gate after revalidating the Pod UID and any command prerequisite.</summary>
    /// <exception cref="SandboxProviderException">The Pod is missing, its gate or identity changed, or its prerequisite failed.</exception>
    public async Task<SandboxBuildTestPodReference> ReleaseSchedulingGateAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        var context = await VerifyBuildTestContextAsync(
            request,
            GetRoleFromPodName(request, pod.Name),
            cancellationToken).ConfigureAwait(false);
        var role = GetRoleFromPodName(request, pod.Name);
        if (role == SandboxBuildTestLimits.CollectorPolicyRole)
            await RequireSuccessfulCommandPodAsync(request, context, cancellationToken).ConfigureAwait(false);

        var currentResource = await _client.GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxProviderException(
                "buildtest_pod_missing",
                "The exact gated BuildTest Pod no longer exists.",
                effectMayHaveApplied: false);
        var current = ValidateBuildTestPod(currentResource, request, context, role);
        if (!string.Equals(current.Uid, pod.Uid, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The BuildTest Pod UID changed before gate release.");
        var gates = ReadSchedulingGates(currentResource);
        if (gates.Length == 0)
            return current;
        if (gates.Length != 1 || gates[0] != BuildTestGateName)
            throw ProviderResourceMismatch("The BuildTest Pod has an unexpected scheduling gate.");

        var updated = await _client.RemovePodSchedulingGateAsync(
            current,
            BuildTestGateName,
            cancellationToken).ConfigureAwait(false);
        return ValidateBuildTestPod(updated, request, context, role, requireGate: false);
    }

    /// <summary>Observes Pod state and bounded logs, then verifies the same Pod UID still exists after log collection.</summary>
    /// <returns>A validated pending, running, successful, failed, or unknown observation.</returns>
    /// <exception cref="SandboxProviderException">The request bounds or exact Pod manifest no longer match.</exception>
    public async Task<SandboxBuildTestPodObservation> ObserveAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        int maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        var role = GetRoleFromPodName(request, pod.Name);
        var context = await VerifyBuildTestContextAsync(request, role, cancellationToken).ConfigureAwait(false);
        if (maximumOutputBytes != GetBuildTestMaximumOutputBytes(request, role))
            throw new ArgumentOutOfRangeException(nameof(maximumOutputBytes));
        var resource = await _client.GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            return UnknownPodObservation(pod, GetBuildTestContainerName(role));
        var current = ValidateBuildTestPod(resource.Value, request, context, role);
        if (!string.Equals(current.Uid, pod.Uid, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The BuildTest Pod UID changed during observation.");

        var phase = ReadOptionalString(resource.Value, "status", "phase");
        if (phase is "Pending" or "Running")
        {
            var pending = new SandboxBuildTestPodObservation(
                current,
                phase == "Pending" ? SandboxBuildTestPodState.Pending : SandboxBuildTestPodState.Running,
                null,
                EmptyOutput(current, GetBuildTestContainerName(role)));
            return pending.Validate(maximumOutputBytes);
        }
        if (phase is not ("Succeeded" or "Failed"))
            return UnknownPodObservation(current, GetBuildTestContainerName(role)).Validate(maximumOutputBytes);

        var output = await _client.ReadPodLogsBoundedAsync(
            current,
            GetBuildTestContainerName(role),
            maximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        var afterLogs = await _client.GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false);
        if (afterLogs is null)
            return UnknownPodObservation(current, GetBuildTestContainerName(role)).Validate(maximumOutputBytes);
        current = ValidateBuildTestPod(afterLogs.Value, request, context, role);
        if (!string.Equals(current.Uid, pod.Uid, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The BuildTest Pod UID changed while logs were read.");

        var terminal = ReadBuildTestTerminalEvidence(
            afterLogs.Value,
            current,
            GetBuildTestContainerName(role),
            output.Truncated);
        var observedPhase = ReadOptionalString(afterLogs.Value, "status", "phase");
        var state = terminal is null
            ? SandboxBuildTestPodState.Unknown
            : observedPhase == "Succeeded" &&
                terminal.Kind == SandboxBuildTestTerminationKind.Exited &&
                terminal.ExitCode == 0
                    ? SandboxBuildTestPodState.Succeeded
                    : SandboxBuildTestPodState.Failed;
        var observation = new SandboxBuildTestPodObservation(current, state, terminal, output);
        return observation.Validate(maximumOutputBytes);
    }

    /// <summary>Stops the exact operation Pod using foreground deletion and its UID precondition.</summary>
    public async Task StopAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken)
    {
        await DeleteBuildTestPodAsync(request, pod, foreground: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the exact operation Pod using background propagation and its UID precondition.</summary>
    public async Task DeleteAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        CancellationToken cancellationToken)
    {
        await DeleteBuildTestPodAsync(request, pod, foreground: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the desired Pod and validates the returned object; conflicts are resolved by reading it once.</summary>
    /// <exception cref="SandboxProviderException">Creation fails or the returned Pod does not match the gated manifest.</exception>
    private async Task<SandboxBuildTestPodReference> CreateBuildTestPodAsync(
        JsonElement desired,
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        string role,
        CancellationToken cancellationToken,
        SandboxBuildTestOutputCollectorRequest? collectorRequest = null)
    {
        JsonElement created;
        try
        {
            created = await _client.CreateAsync(
                string.Empty,
                "pods",
                context.Descriptor.Namespace,
                desired,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException exception) when (exception.Code == "kubernetes_conflict")
        {
            var current = await _client.GetAsync(
                string.Empty,
                "pods",
                context.Descriptor.Namespace,
                ReadOptionalString(desired, "metadata", "name")!,
                cancellationToken).ConfigureAwait(false);
            if (current is null)
                throw;
            return ValidateBuildTestPod(
                current.Value,
                request,
                context,
                role,
                collectorRequest,
                requireGate: true);
        }
        catch (SandboxProviderException exception) when (!exception.EffectMayHaveApplied)
        {
            throw;
        }
        catch (SandboxProviderException exception)
        {
            throw new SandboxProviderException(
                exception.Code,
                exception.Message,
                effectMayHaveApplied: true,
                exception);
        }

        try
        {
            return ValidateBuildTestPod(created, request, context, role, collectorRequest, requireGate: true);
        }
        catch (SandboxProviderException exception)
        {
            throw new SandboxProviderException(
                exception.Code,
                exception.Message,
                effectMayHaveApplied: true,
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new SandboxProviderException(
                "buildtest_pod_invalid",
                "The created BuildTest Pod failed validation.",
                effectMayHaveApplied: true,
                exception);
        }
    }

    /// <summary>Checks the pinned RuntimeClass, workspace claim, and role-specific offline Cilium policy.</summary>
    private async Task<BuildTestContext> VerifyBuildTestContextAsync(
        SandboxBuildTestProviderRequest request,
        string role,
        CancellationToken cancellationToken,
        BuildTestContext? validatedContext = null)
    {
        var context = validatedContext ?? ReadBuildTestContext(request);
        await VerifyBuildTestClusterIdentityAsync(context, cancellationToken).ConfigureAwait(false);
        await VerifyRuntimeClassAsync(context.Options, cancellationToken).ConfigureAwait(false);
        await VerifyWorkspaceClaimForBuildTestAsync(context, cancellationToken).ConfigureAwait(false);
        var policy = role == SandboxBuildTestLimits.CommandPolicyRole
            ? ToPolicyDescriptor(request.CommandPolicy)
            : ToPolicyDescriptor(request.CollectorPolicy
                ?? throw new SandboxProviderException(
                    "buildtest_collector_policy_missing",
                    "A file-output collector requires its own offline Cilium policy.",
                    effectMayHaveApplied: false));
        await VerifyBuildTestPolicyAsync(
            context,
            request.AcceptedCommand.OperationId,
            role,
            request.ExpectedBinding.NetworkPolicyGeneration,
            policy,
            cancellationToken)
            .ConfigureAwait(false);
        return context;
    }

    /// <summary>Converts the accepted command policy contract to the common internal shape.</summary>
    private static BuildTestPolicyDescriptor ToPolicyDescriptor(
        SandboxBuildTestCommandNetworkPolicyBinding policy) =>
        new(policy.PolicyUid, policy.SpecSha256, policy.NetworkPolicyGeneration, policy.SelectorLabels);

    /// <summary>Converts the accepted collector policy contract to the common internal shape.</summary>
    private static BuildTestPolicyDescriptor ToPolicyDescriptor(
        SandboxBuildTestOutputCollectorNetworkPolicyBinding policy) =>
        new(policy.PolicyUid, policy.SpecSha256, policy.NetworkPolicyGeneration, policy.SelectorLabels);

    /// <summary>Reconstructs the pinned Sandbox and PVC context and checks accepted BuildTest profile and generation.</summary>
    /// <exception cref="SandboxProviderException">The accepted options, owner, Sandbox, Storage, or workspace identity differs.</exception>
    private BuildTestContext ReadBuildTestContext(SandboxBuildTestProviderRequest request)
    {
        var expected = request.ExpectedBinding;
        var releaseDescriptor = expected.SandboxProviderBinding!.ReleaseDescriptor;
        var pinnedClusterIdentity = releaseDescriptor.TryGetProperty("clusterIdentity", out var clusterPin) &&
            clusterPin.ValueKind == JsonValueKind.String
                ? clusterPin.GetString()!
                : string.Empty;
        var (options, descriptor) = ReadBinding(
            expected.Fence,
            expected.SandboxResource,
            expected.ProviderFencingGeneration,
            expected.SandboxProviderBinding!,
            pinnedClusterIdentity);
        var accepted = request.AcceptedCommand;
        if (options.AcceptedBuildTestProfile is not { } profile ||
            !SameExecutionOptions(profile, accepted.ExecutionOptions) ||
            !string.Equals(
                descriptor.OperationId,
                expected.SandboxLeaseOperationId.ToString("N"),
                StringComparison.Ordinal) ||
            descriptor.WorkspaceReadOnly ||
            descriptor.WorkspaceResourceGeneration != expected.WorkspaceVolume.ResourceGeneration ||
            !string.Equals(descriptor.StorageProviderId, options.WorkspaceStorageProviderId, StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.WorkspaceIdentityFingerprint,
                GetWorkspaceFingerprint(
                    expected.WorkspaceVolume.ProjectId,
                    expected.WorkspaceVolume.VolumeId,
                    expected.WorkspaceVolume.ResourceGeneration.ToString(CultureInfo.InvariantCulture),
                    expected.Fence.Owner.EnvironmentId),
                StringComparison.Ordinal))
            throw ProviderBindingMismatch();

        var storage = new ProviderResourceRef(
            ProviderSeam.Storage,
            descriptor.StorageProviderId,
            descriptor.WorkspaceClaimUid,
            descriptor.WorkspaceResourceGeneration);
        var workspace = new WorkspaceVolumeAttachmentNegotiation(
            expected.Fence.Owner.ProjectId,
            expected.Fence.Owner.EnvironmentId,
            expected.Fence.Owner.RunId,
            expected.WorkspaceVolume,
            expected.SandboxResource,
            storage,
            expected.Fence,
            expected.DataGeneration,
            descriptor.WorkspaceMountPath,
            false,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim).Validate();
        return new(options, descriptor, workspace);
    }

    /// <summary>Checks the stored cluster pin against the trusted Kubernetes API CA bundle.</summary>
    private async Task VerifyBuildTestClusterIdentityAsync(
        BuildTestContext context,
        CancellationToken cancellationToken)
    {
        var clusterIdentity = await _client.GetClusterIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(context.Descriptor.ClusterIdentity, clusterIdentity, StringComparison.Ordinal))
            throw ProviderBindingMismatch();
    }

    /// <summary>Rechecks the exact bound PVC and requires multi-writer mode for the command-plus-collector flow.</summary>
    private async Task VerifyWorkspaceClaimForBuildTestAsync(
        BuildTestContext context,
        CancellationToken cancellationToken)
    {
        var descriptor = context.Descriptor;
        var claim = await _client.GetAsync(
            string.Empty,
            "persistentvolumeclaims",
            descriptor.WorkspaceNamespace,
            descriptor.WorkspaceClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
            throw new SandboxProviderException(
                "workspace_claim_missing",
                "The exact BuildTest workspace PVC no longer exists.",
                effectMayHaveApplied: false);
        _ = ValidateWorkspaceClaim(
            claim.Value,
            context.Workspace.EnvironmentFence,
            context.Workspace,
            new AgentSandboxPersistentVolumeClaimAttachment(
                1,
                descriptor.StorageProviderId,
                descriptor.WorkspaceNamespace,
                descriptor.WorkspaceClaimName,
                descriptor.WorkspaceClaimUid),
            requireBound: true);
        var accessModes = TryGetArray(RequiredObject(claim.Value, "spec"), "accessModes");
        if (accessModes is not { } modes ||
            !modes.EnumerateArray().Any(mode =>
                mode.ValueKind == JsonValueKind.String &&
                string.Equals(mode.GetString(), "ReadWriteMany", StringComparison.Ordinal)))
            throw ProviderResourceMismatch("The BuildTest workspace PVC is not a verified ReadWriteMany claim.");
    }

    /// <summary>Requires the exact policy UID, canonical spec hash, selector, generation, and deny-all behavior.</summary>
    /// <exception cref="SandboxProviderException">The policy is absent, changed, or permits traffic.</exception>
    private async Task VerifyBuildTestPolicyAsync(
        BuildTestContext context,
        Guid operationId,
        string role,
        long expectedGeneration,
        BuildTestPolicyDescriptor policyBinding,
        CancellationToken cancellationToken)
    {
        var policyName = role == SandboxBuildTestLimits.CommandPolicyRole
            ? AgentSandboxBuildTestPolicyNames.Command(operationId)
            : AgentSandboxBuildTestPolicyNames.Collector(operationId);
        var policy = await _client.GetAsync(
            BuildTestPolicyApiGroup,
            BuildTestPolicyResource,
            context.Descriptor.Namespace,
            policyName,
            cancellationToken,
            BuildTestPolicyApiVersion).ConfigureAwait(false);
        if (policy is null)
            throw ProviderResourceMismatch("The exact BuildTest Cilium deny-all policy is not present.");

        var metadata = RequiredObject(policy.Value, "metadata");
        var spec = RequiredObject(policy.Value, "spec");
        var endpointSelector = RequiredObject(spec, "endpointSelector");
        var enableDefaultDeny = RequiredObject(spec, "enableDefaultDeny");
        var actualLabels = ReadStringDictionary(endpointSelector, "matchLabels");
        var expectedRole = role == SandboxBuildTestLimits.CommandPolicyRole
            ? SandboxBuildTestLimits.CommandPolicyRole
            : SandboxBuildTestLimits.CollectorPolicyRole;
        var actualSpecHash = SandboxBuildTestCommandPolicyCanonicalization.ComputeSpecSha256(spec);
        var ingressEmpty = !spec.TryGetProperty("ingress", out var ingress) ||
            ingress.ValueKind == JsonValueKind.Array && ingress.GetArrayLength() == 0;
        if (RequiredString(metadata, "name") != policyName ||
            RequiredString(metadata, "namespace") != context.Descriptor.Namespace ||
            RequiredString(metadata, "uid") != policyBinding.PolicyUid ||
            actualLabels.Count != policyBinding.SelectorLabels.Count ||
            !policyBinding.SelectorLabels.All(pair =>
                actualLabels.TryGetValue(pair.Key, out var value) && value == pair.Value) ||
            actualSpecHash != policyBinding.SpecSha256 ||
            !TryGetBoolean(enableDefaultDeny, "ingress", out var ingressDefaultDeny) ||
            !TryGetBoolean(enableDefaultDeny, "egress", out var egressDefaultDeny) ||
            !ingressDefaultDeny ||
            !egressDefaultDeny ||
            !ingressEmpty ||
            RequiredArray(spec, "egress").GetArrayLength() != 0 ||
            spec.EnumerateObject().Count() != 3 ||
            endpointSelector.EnumerateObject().Count() != 1 ||
            enableDefaultDeny.EnumerateObject().Count() != 2 ||
            policyBinding.NetworkPolicyGeneration != expectedGeneration)
            throw ProviderResourceMismatch(
                $"The exact BuildTest {expectedRole} Cilium policy does not enforce its pinned offline selector.");
    }

    /// <summary>Compares every accepted execution and collector option using ordinal sequence equality.</summary>
    private static bool SameExecutionOptions(
        SandboxBuildTestAcceptedExecutionOptions left,
        SandboxBuildTestAcceptedExecutionOptions right) =>
        left.ProfileId == right.ProfileId &&
        left.ImageReference == right.ImageReference &&
        left.ImagePlatform == right.ImagePlatform &&
        left.AllowedExecutables.SequenceEqual(right.AllowedExecutables, StringComparer.Ordinal) &&
        left.CpuLimit == right.CpuLimit &&
        left.MemoryLimit == right.MemoryLimit &&
        left.EphemeralStorageLimit == right.EphemeralStorageLimit &&
        left.TimeoutSeconds == right.TimeoutSeconds &&
        left.MaximumOutputBytes == right.MaximumOutputBytes &&
        left.MaximumFileOutputBytes == right.MaximumFileOutputBytes &&
        left.EgressProfile == right.EgressProfile &&
        left.CollectorImageReference == right.CollectorImageReference &&
        left.CollectorImagePlatform == right.CollectorImagePlatform &&
        left.CollectorExecutableReference == right.CollectorExecutableReference &&
        left.CollectorAssemblyArguments.SequenceEqual(right.CollectorAssemblyArguments, StringComparer.Ordinal) &&
        left.CollectorMode == right.CollectorMode &&
        left.CollectorContainerName == right.CollectorContainerName;

    /// <summary>Builds the isolated Pod manifest from accepted command data and pinned policy selectors.</summary>
    /// <exception cref="SandboxProviderException">The command requests an unsupported working directory or its binding is incomplete.</exception>
    private JsonElement BuildTestPodManifest(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        string role,
        SandboxBuildTestOutputCollectorRequest? collectorRequest)
    {
        var command = request.AcceptedCommand;
        var isCollector = role == SandboxBuildTestLimits.CollectorPolicyRole;
        var policy = isCollector
            ? ToPolicyDescriptor(request.CollectorPolicy!)
            : ToPolicyDescriptor(request.CommandPolicy);
        var workspaceCapture = isCollector && command.WorkspaceSource is not null;
        var containerName = workspaceCapture
            ? SandboxBuildTestLimits.WorkspaceCaptureContainerName : GetBuildTestContainerName(role);
        var image = isCollector
            ? command.ExecutionOptions.CollectorImageReference
            : command.ExecutionOptions.ImageReference;
        var executable = isCollector
            ? command.ExecutionOptions.CollectorExecutableReference
            : command.ExecutableReference;
        var arguments = workspaceCapture
            ? WorkspaceHelperArguments(request, context, preparing: false, "hold")
            : isCollector
            ? command.ExecutionOptions.CollectorAssemblyArguments
                .Add(command.ExecutionOptions.CollectorMode)
                .Add(EncodeCollectorRequest(collectorRequest!))
            : command.Arguments;
        var workingDirectory = !isCollector && command.WorkspaceSource is { } source
            ? context.Descriptor.WorkspaceMountPath.TrimEnd('/') + "/" + source.PreparedRepositoryRelativePath
            : context.Descriptor.WorkspaceMountPath;
        if (!isCollector && command.WorkingDirectory != ".")
            throw new SandboxProviderException(
                "buildtest_working_directory_unsupported",
                "BuildTest commands must run from the exact workspace mount root.",
                effectMayHaveApplied: false);

        var labels = BuildTestPodLabels(request, context, policy.SelectorLabels);
        var annotations = BuildTestPodAnnotations(
            request, context, policy.PolicyUid, policy.SpecSha256);
        var podName = BuildTestPodName(command.OperationId, role);
        var cpu = command.ExecutionOptions.CpuLimit;
        var memory = command.ExecutionOptions.MemoryLimit;
        var ephemeral = command.ExecutionOptions.EphemeralStorageLimit;
        var readOnly = isCollector;
        var container = new
        {
            name = containerName,
            image,
            imagePullPolicy = "Always",
            command = new[] { executable },
            args = arguments,
            workingDir = workingDirectory,
            resources = new
            {
                requests = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cpu"] = cpu,
                    ["memory"] = memory,
                    ["ephemeral-storage"] = ephemeral
                },
                limits = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["cpu"] = cpu,
                    ["memory"] = memory,
                    ["ephemeral-storage"] = ephemeral
                }
            },
            volumeMounts = new[]
            {
                new
                {
                    name = BuildTestWorkspaceVolumeName,
                    mountPath = context.Descriptor.WorkspaceMountPath,
                    readOnly
                }
            },
            securityContext = new
            {
                allowPrivilegeEscalation = false,
                runAsNonRoot = true,
                runAsUser = BuildTestContainerUser,
                runAsGroup = BuildTestContainerUser,
                readOnlyRootFilesystem = true,
                seccompProfile = new { type = "RuntimeDefault" },
                capabilities = new { drop = new[] { "ALL" } }
            },
            env = isCollector
                ? new[]
                {
                    new
                    {
                        name = BuildTestCollectorPodUidEnvironmentVariable,
                        valueFrom = new
                        {
                            fieldRef = new { apiVersion = "v1", fieldPath = "metadata.uid" }
                        }
                    }
                }
                : []
        };

        var manifest = JsonSerializer.SerializeToElement(new
        {
            apiVersion = "v1",
            kind = "Pod",
            metadata = new
            {
                name = podName,
                @namespace = context.Descriptor.Namespace,
                labels,
                annotations
            },
            spec = new
            {
                runtimeClassName = context.Options.RuntimeClassName,
                restartPolicy = "Never",
                activeDeadlineSeconds = command.ExecutionOptions.TimeoutSeconds,
                terminationGracePeriodSeconds = 1,
                automountServiceAccountToken = false,
                enableServiceLinks = false,
                hostNetwork = false,
                hostPID = false,
                hostIPC = false,
                shareProcessNamespace = false,
                nodeSelector = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kubernetes.io/os"] = "linux",
                    ["kubernetes.io/arch"] = "amd64"
                },
                schedulingGates = new[] { new { name = BuildTestGateName } },
                securityContext = new
                {
                    runAsNonRoot = true,
                    runAsUser = BuildTestContainerUser,
                    runAsGroup = BuildTestContainerUser,
                    fsGroup = BuildTestContainerUser,
                    seccompProfile = new { type = "RuntimeDefault" }
                },
                volumes = new[]
                {
                    new
                    {
                        name = BuildTestWorkspaceVolumeName,
                        persistentVolumeClaim = new
                        {
                            claimName = context.Descriptor.WorkspaceClaimName,
                            readOnly
                        }
                    }
                },
                containers = new[] { container }
            }
        }, JsonOptions);
        return command.WorkspaceSource is null
            ? manifest : AddWorkspacePodContainers(manifest, request, context, isCollector);
    }

    /// <summary>Combines policy selector labels with stable operation, owner, lifecycle, and generation labels.</summary>
    private ImmutableDictionary<string, string> BuildTestPodLabels(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        ImmutableDictionary<string, string> selectorLabels)
    {
        var labels = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var selector in selectorLabels)
            labels.Add(selector.Key, selector.Value);
        AddBuildTestLabel(labels, BuildTestManagedLabel, "true");
        AddBuildTestLabel(labels, BuildTestOwnerLabel, context.Descriptor.OwnerFingerprint);
        AddBuildTestLabel(
            labels,
            BuildTestLifecycleLabel,
            context.Workspace.EnvironmentFence.LifecycleGeneration.ToString(CultureInfo.InvariantCulture));
        AddBuildTestLabel(
            labels,
            BuildTestSandboxGenerationLabel,
            request.ExpectedBinding.SandboxResource.Generation.ToString(CultureInfo.InvariantCulture));
        AddBuildTestLabel(
            labels,
            BuildTestFencingGenerationLabel,
            request.ExpectedBinding.ProviderFencingGeneration.ToString(CultureInfo.InvariantCulture));
        return labels.ToImmutable();
    }

    /// <summary>Adds a required label unless an existing policy selector conflicts with its pinned value.</summary>
    private static void AddBuildTestLabel(
        ImmutableDictionary<string, string>.Builder labels,
        string key,
        string value)
    {
        if (labels.TryGetValue(key, out var existing))
        {
            if (!string.Equals(existing, value, StringComparison.Ordinal))
                throw ProviderResourceMismatch("A BuildTest policy selector conflicts with an owner label.");
            return;
        }
        labels.Add(key, value);
    }

    /// <summary>Builds annotations that bind the Pod to accepted command, Sandbox, PVC, and network-policy digests.</summary>
    private ImmutableDictionary<string, string> BuildTestPodAnnotations(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        string policyUid,
        string policySpecSha256) =>
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(BuildTestOwnerAnnotation, context.Descriptor.OwnerFingerprint),
            new KeyValuePair<string, string>(
                BuildTestImmutableHashAnnotation,
                request.AcceptedCommand.ImmutableHash),
            new KeyValuePair<string, string>(
                BuildTestRequestFingerprintAnnotation,
                new SandboxBuildTestApiRequest(
                    request.AcceptedCommand.Checkpoint,
                    request.ExpectedBinding).ComputeRequestFingerprint(request.AcceptedCommand)),
            new KeyValuePair<string, string>(
                BuildTestSandboxUidAnnotation,
                request.ExpectedBinding.SandboxResource.ResourceId),
            new KeyValuePair<string, string>(
                BuildTestWorkspaceUidAnnotation,
                context.Descriptor.WorkspaceClaimUid),
            new KeyValuePair<string, string>(BuildTestPolicyUidAnnotation, policyUid),
            new KeyValuePair<string, string>(BuildTestPolicyHashAnnotation, policySpecSha256)
        ]);

    /// <summary>Validates metadata, operation identity, policy labels, annotations, and the full Pod spec.</summary>
    /// <returns>The exact namespace, name, UID, and resource version.</returns>
    /// <exception cref="SandboxProviderException">The Pod does not match the accepted operation.</exception>
    private SandboxBuildTestPodReference ValidateBuildTestPod(
        JsonElement pod,
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        string role,
        SandboxBuildTestOutputCollectorRequest? collectorRequest = null,
        bool? requireGate = null)
    {
        var expectedPodName = BuildTestPodName(request.AcceptedCommand.OperationId, role);
        var metadata = RequiredObject(pod, "metadata");
        var expectedPolicy = role == SandboxBuildTestLimits.CommandPolicyRole
            ? ToPolicyDescriptor(request.CommandPolicy)
            : ToPolicyDescriptor(request.CollectorPolicy
                ?? throw ProviderResourceMismatch("The BuildTest collector policy binding is missing."));
        var labels = ReadStringDictionary(metadata, "labels");
        var annotations = ReadStringDictionary(metadata, "annotations");
        foreach (var expected in BuildTestPodLabels(request, context, expectedPolicy.SelectorLabels))
            if (!labels.TryGetValue(expected.Key, out var value) || value != expected.Value)
                throw ProviderResourceMismatch("The BuildTest Pod owner or policy selector labels do not match.");
        foreach (var expected in BuildTestPodAnnotations(
            request, context, expectedPolicy.PolicyUid, expectedPolicy.SpecSha256))
            if (!annotations.TryGetValue(expected.Key, out var value) || value != expected.Value)
                throw ProviderResourceMismatch("The BuildTest Pod immutable operation annotations do not match.");

        var uid = RequiredString(metadata, "uid");
        var namespaceName = RequiredString(metadata, "namespace");
        var name = RequiredString(metadata, "name");
        var resourceVersion = RequiredString(metadata, "resourceVersion");
        if (name != expectedPodName || namespaceName != context.Descriptor.Namespace ||
            ReadOptionalString(pod, "kind") != "Pod" ||
            labels.GetValueOrDefault(SandboxBuildTestLimits.PolicyOperationLabel) !=
                request.AcceptedCommand.OperationId.ToString("N"))
            throw ProviderResourceMismatch("The BuildTest Pod identity does not match the accepted operation.");

        ValidateBuildTestPodSpec(pod, request, context, role, collectorRequest, requireGate);
        return new SandboxBuildTestPodReference(namespaceName, name, uid, resourceVersion).Validate();
    }

    /// <summary>Verifies runtime, isolation, helper, resource, mount, environment, and scheduling-gate invariants.</summary>
    /// <exception cref="SandboxProviderException">Any Pod spec field differs from the immutable accepted manifest.</exception>
    private void ValidateBuildTestPodSpec(
        JsonElement pod,
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        string role,
        SandboxBuildTestOutputCollectorRequest? collectorRequest,
        bool? requireGate)
    {
        var command = request.AcceptedCommand;
        var options = command.ExecutionOptions;
        var spec = RequiredObject(pod, "spec");
        var isCollector = role == SandboxBuildTestLimits.CollectorPolicyRole;
        if (isCollector)
            collectorRequest ??= CreateCollectorRequest(request, context);
        var expectedImage = isCollector ? options.CollectorImageReference : options.ImageReference;
        var expectedExecutable = isCollector ? options.CollectorExecutableReference : command.ExecutableReference;
        var workspaceCapture = isCollector && command.WorkspaceSource is not null;
        var expectedArguments = workspaceCapture
            ? WorkspaceHelperArguments(request, context, preparing: false, "hold")
            : isCollector
            ? options.CollectorAssemblyArguments
                .Add(options.CollectorMode)
                .Add(EncodeCollectorRequest(collectorRequest
                    ?? throw ProviderResourceMismatch("The BuildTest collector request is missing.")))
            : command.Arguments;
        var expectedContainerName = workspaceCapture
            ? SandboxBuildTestLimits.WorkspaceCaptureContainerName : GetBuildTestContainerName(role);
        if (ReadOptionalString(spec, "runtimeClassName") != context.Options.RuntimeClassName ||
            ReadOptionalString(spec, "restartPolicy") != "Never" ||
            (workspaceCapture
                ? spec.TryGetProperty("activeDeadlineSeconds", out _)
                : !TryGetInt64(spec, "activeDeadlineSeconds", out var timeout) ||
                    timeout != options.TimeoutSeconds) ||
            !TryGetInt64(spec, "terminationGracePeriodSeconds", out var gracePeriod) ||
            gracePeriod != 1 ||
            !TryGetBoolean(spec, "automountServiceAccountToken", out var automount) ||
            automount ||
            !TryGetBoolean(spec, "enableServiceLinks", out var serviceLinks) ||
            serviceLinks ||
            !TryGetBoolean(spec, "hostNetwork", out var hostNetwork) ||
            hostNetwork ||
            !TryGetBoolean(spec, "hostPID", out var hostPid) ||
            hostPid ||
            !TryGetBoolean(spec, "hostIPC", out var hostIpc) ||
            hostIpc ||
            !TryGetBoolean(spec, "shareProcessNamespace", out var shareProcessNamespace) ||
            shareProcessNamespace ||
            !NodeSelectorMatches(spec) ||
            !PodSecurityContextMatches(spec))
            throw ProviderResourceMismatch("The BuildTest Pod isolation, resource timeout, or runtime was mutated.");

        var containers = RequiredArray(spec, "containers");
        if (containers.GetArrayLength() != 1 ||
            (command.WorkspaceSource is not null && !isCollector
                ? TryGetArray(spec, "initContainers") is not { } preparationContainers ||
                    preparationContainers.GetArrayLength() != 1
                : TryGetArray(spec, "initContainers") is { } initContainers &&
                    initContainers.GetArrayLength() != 0) ||
            TryGetArray(spec, "ephemeralContainers") is { } ephemeralContainers &&
                ephemeralContainers.GetArrayLength() != 0)
            throw ProviderResourceMismatch("The BuildTest Pod must contain only its pinned command container.");
        var container = containers[0];
        if (ReadOptionalString(container, "name") != expectedContainerName ||
            ReadOptionalString(container, "image") != expectedImage ||
            ReadOptionalString(container, "imagePullPolicy") != "Always" ||
            ReadOptionalString(container, "workingDir") !=
                (!isCollector && command.WorkspaceSource is { } source
                    ? context.Descriptor.WorkspaceMountPath.TrimEnd('/') + "/" + source.PreparedRepositoryRelativePath
                    : context.Descriptor.WorkspaceMountPath) ||
            !JsonStringArrayMatches(container, "command", [expectedExecutable]) ||
            !JsonStringArrayMatches(container, "args", expectedArguments) ||
            !ContainerResourcesMatch(container, options) ||
            !ContainerSecurityContextMatches(container) ||
            !WorkspaceVolumeMatches(spec, container, context, isCollector,
                hasPrivateState: workspaceCapture, hasPrivateVolume: command.WorkspaceSource is not null))
            throw ProviderResourceMismatch("The BuildTest command container differs from its immutable accepted manifest.");
        if (isCollector)
        {
            if (!CollectorUidEnvironmentMatches(container))
                throw ProviderResourceMismatch("The BuildTest output collector Pod UID binding was mutated.");
        }
        else if (TryGetArray(container, "env") is { } environment && environment.GetArrayLength() != 0 ||
                 TryGetArray(container, "envFrom") is { } envFrom && envFrom.GetArrayLength() != 0)
            throw ProviderResourceMismatch("A BuildTest command Pod cannot receive environment variables.");

        if (command.WorkspaceSource is not null)
            ValidateWorkspacePodContainers(spec, request, context, isCollector);

        var gates = ReadSchedulingGates(pod);
        if (requireGate == true && (gates.Length != 1 || gates[0] != BuildTestGateName) ||
            requireGate == false && gates.Length != 0 ||
            gates.Any(gate => gate != BuildTestGateName))
            throw ProviderResourceMismatch("The BuildTest scheduling gate was changed unexpectedly.");
    }

    /// <summary>Requires the Pod to target Linux on amd64 and no additional node-selector values.</summary>
    private static bool NodeSelectorMatches(JsonElement spec)
    {
        var selector = ReadStringDictionary(spec, "nodeSelector");
        return selector.Count == 2 &&
            selector.GetValueOrDefault("kubernetes.io/os") == "linux" &&
            selector.GetValueOrDefault("kubernetes.io/arch") == "amd64";
    }

    /// <summary>Checks the non-root UID/GID, supplemental group, and runtime-default seccomp settings.</summary>
    private static bool PodSecurityContextMatches(JsonElement spec)
    {
        var security = TryGetObject(spec, "securityContext");
        return security is { } value &&
            TryGetBoolean(value, "runAsNonRoot", out var nonRoot) && nonRoot &&
            TryGetInt64(value, "runAsUser", out var user) && user == BuildTestContainerUser &&
            TryGetInt64(value, "runAsGroup", out var group) && group == BuildTestContainerUser &&
            TryGetInt64(value, "fsGroup", out var fsGroup) && fsGroup == BuildTestContainerUser &&
            ReadOptionalString(value, "seccompProfile", "type") == "RuntimeDefault";
    }

    /// <summary>Requires non-root execution, no privilege escalation, a read-only root, and dropped capabilities.</summary>
    private static bool ContainerSecurityContextMatches(JsonElement container)
    {
        var security = TryGetObject(container, "securityContext");
        if (security is not { } value ||
            !TryGetBoolean(value, "allowPrivilegeEscalation", out var escalation) || escalation ||
            !TryGetBoolean(value, "runAsNonRoot", out var nonRoot) || !nonRoot ||
            !TryGetInt64(value, "runAsUser", out var user) || user != BuildTestContainerUser ||
            !TryGetInt64(value, "runAsGroup", out var group) || group != BuildTestContainerUser ||
            !TryGetBoolean(value, "readOnlyRootFilesystem", out var readOnlyRoot) || !readOnlyRoot ||
            ReadOptionalString(value, "seccompProfile", "type") != "RuntimeDefault" ||
            TryGetBoolean(value, "privileged", out var privileged) && privileged)
            return false;
        var capabilities = TryGetObject(value, "capabilities");
        var dropped = capabilities is { } caps ? TryGetArray(caps, "drop") : null;
        var added = capabilities is { } caps2 ? TryGetArray(caps2, "add") : null;
        return dropped is { } dropArray &&
            dropArray.GetArrayLength() == 1 &&
            dropArray[0].ValueKind == JsonValueKind.String &&
            dropArray[0].GetString() == "ALL" &&
            (added is null || added.Value.GetArrayLength() == 0);
    }

    /// <summary>Compares container resource requests and limits against the accepted CPU, memory, and storage bounds.</summary>
    private static bool ContainerResourcesMatch(
        JsonElement container,
        SandboxBuildTestAcceptedExecutionOptions options)
    {
        var resources = TryGetObject(container, "resources");
        if (resources is not { } resourceObject)
            return false;
        var requests = ReadStringDictionary(resourceObject, "requests");
        var limits = ReadStringDictionary(resourceObject, "limits");
        return ResourceQuantityMatches(requests, options) && ResourceQuantityMatches(limits, options);
    }

    /// <summary>Requires exactly the accepted three resource quantities and no additional resource keys.</summary>
    private static bool ResourceQuantityMatches(
        ImmutableDictionary<string, string> quantities,
        SandboxBuildTestAcceptedExecutionOptions options) =>
        quantities.Count == 3 &&
        quantities.GetValueOrDefault("cpu") == options.CpuLimit &&
        quantities.GetValueOrDefault("memory") == options.MemoryLimit &&
        quantities.GetValueOrDefault("ephemeral-storage") == options.EphemeralStorageLimit;

    /// <summary>Checks the exact workspace PVC mount and optional private helper state volume.</summary>
    private static bool WorkspaceVolumeMatches(
        JsonElement spec,
        JsonElement container,
        BuildTestContext context,
        bool readOnly,
        bool hasPrivateState = false,
        bool hasPrivateVolume = false)
    {
        var volumes = TryGetArray(spec, "volumes");
        var mounts = TryGetArray(container, "volumeMounts");
        if (volumes is not { } volumeArray ||
            volumeArray.GetArrayLength() != (hasPrivateState || hasPrivateVolume ? 2 : 1) ||
            mounts is not { } mountArray || mountArray.GetArrayLength() != (hasPrivateState ? 2 : 1))
            return false;
        var volume = volumeArray[0];
        var claim = TryGetObject(volume, "persistentVolumeClaim");
        var mount = mountArray[0];
        return ReadOptionalString(volume, "name") == BuildTestWorkspaceVolumeName &&
            ReadOptionalString(mount, "name") == BuildTestWorkspaceVolumeName &&
            claim is { } claimObject &&
            ReadOptionalString(claimObject, "claimName") == context.Descriptor.WorkspaceClaimName &&
            TryGetBoolean(claimObject, "readOnly", out var claimReadOnly) &&
            claimReadOnly == readOnly &&
            ReadOptionalString(mount, "mountPath") == context.Descriptor.WorkspaceMountPath &&
            TryGetBoolean(mount, "readOnly", out var mountReadOnly) &&
            mountReadOnly == readOnly;
    }

    /// <summary>Requires the collector environment variable to reference the current Pod UID field.</summary>
    private static bool CollectorUidEnvironmentMatches(JsonElement container)
    {
        var env = TryGetArray(container, "env");
        if (env is not { } values || values.GetArrayLength() != 1)
            return false;
        var item = values[0];
        return ReadOptionalString(item, "name") == BuildTestCollectorPodUidEnvironmentVariable &&
            ReadOptionalString(item, "valueFrom", "fieldRef", "apiVersion") == "v1" &&
            ReadOptionalString(item, "valueFrom", "fieldRef", "fieldPath") == "metadata.uid";
    }

    /// <summary>Compares a JSON string array with accepted values using ordinal equality and exact length.</summary>
    private static bool JsonStringArrayMatches(
        JsonElement parent,
        string name,
        IEnumerable<string> expected)
    {
        var actual = TryGetArray(parent, name);
        if (actual is not { } values)
            return false;
        var materialized = expected.ToArray();
        return values.GetArrayLength() == materialized.Length &&
            values.EnumerateArray().Select(value =>
                value.ValueKind == JsonValueKind.String ? value.GetString() : null)
                .SequenceEqual(materialized, StringComparer.Ordinal);
    }

    /// <summary>Reads scheduling-gate names and rejects malformed gate objects.</summary>
    private static string[] ReadSchedulingGates(JsonElement pod)
    {
        var gates = TryGetArray(RequiredObject(pod, "spec"), "schedulingGates");
        if (gates is null)
            return [];
        var result = new List<string>();
        foreach (var gate in gates.Value.EnumerateArray())
        {
            var name = ReadOptionalString(gate, "name");
            if (name is null)
                throw ProviderResourceMismatch("The BuildTest Pod has a malformed scheduling gate.");
            result.Add(name);
        }
        return result.ToArray();
    }

    /// <summary>Extracts terminal evidence only for the named container with a complete termination record.</summary>
    private static SandboxBuildTestTerminalEvidence? ReadBuildTestTerminalEvidence(
        JsonElement pod,
        SandboxBuildTestPodReference reference,
        string containerName,
        bool outputTruncated)
    {
        var statuses = TryGetArray(RequiredObject(pod, "status"), "containerStatuses");
        if (statuses is not { } values)
            return null;
        foreach (var status in values.EnumerateArray())
        {
            if (ReadOptionalString(status, "name") != containerName ||
                TryGetObject(status, "state") is not { } state ||
                TryGetObject(state, "terminated") is not { } terminated ||
                !TryGetInt64(terminated, "exitCode", out var exitCode) ||
                !TryGetDateTime(terminated, "startedAt", out var startedAt) ||
                !TryGetDateTime(terminated, "finishedAt", out var finishedAt) ||
                exitCode is < int.MinValue or > int.MaxValue)
                continue;
            var reason = BoundedBuildTestReason(
                ReadOptionalString(terminated, "reason") ??
                ReadOptionalString(pod, "status", "reason"));
            var timedOut = string.Equals(reason, "DeadlineExceeded", StringComparison.Ordinal);
            var kind = outputTruncated
                ? SandboxBuildTestTerminationKind.OutputLimitExceeded
                : timedOut
                    ? SandboxBuildTestTerminationKind.TimedOut
                    : SandboxBuildTestTerminationKind.Exited;
            return new SandboxBuildTestTerminalEvidence(
                reference.Uid,
                containerName,
                kind,
                checked((int)exitCode),
                startedAt,
                finishedAt,
                reason).Validate();
        }
        return null;
    }

    /// <summary>Removes control characters and bounds an untrusted Kubernetes termination reason.</summary>
    private static string? BoundedBuildTestReason(string? value)
    {
        if (value is null)
            return null;
        var safe = new string(value.Where(character => !char.IsControl(character)).Take(512).ToArray());
        return safe.Length == 0 ? null : safe;
    }

    /// <summary>Requires a successful, exit-code-zero command and bounded output before collecting declared files.</summary>
    private async Task RequireSuccessfulCommandPodAsync(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context,
        CancellationToken cancellationToken)
    {
        var name = BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CommandPolicyRole);
        var resource = await _client.GetAsync(
            string.Empty, "pods", context.Descriptor.Namespace, name, cancellationToken).ConfigureAwait(false);
        if (resource is null)
            throw ProviderResourceMismatch("The BuildTest command Pod is missing before file-output collection.");
        var reference = ValidateBuildTestPod(
            resource.Value, request, context, SandboxBuildTestLimits.CommandPolicyRole);
        if (ReadOptionalString(resource.Value, "status", "phase") != "Succeeded")
            throw ProviderResourceMismatch("A trusted output collector requires a terminal successful command Pod.");
        var terminal = ReadBuildTestTerminalEvidence(
            resource.Value,
            reference,
            BuildTestCommandContainerName,
            outputTruncated: false);
        if (terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 })
            throw ProviderResourceMismatch("A trusted output collector requires exit-code-zero command evidence.");
        var output = await _client.ReadPodLogsBoundedAsync(
            reference,
            BuildTestCommandContainerName,
            request.AcceptedCommand.ExecutionOptions.MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false);
        if (output.Truncated)
            throw ProviderResourceMismatch("The command output exceeded its accepted capture limit.");
    }

    /// <summary>Requires the collector request to match every accepted operation, checkpoint, path, and size limit.</summary>
    /// <exception cref="ArgumentException">The collector input is not the accepted command's exact output request.</exception>
    private static void ValidateCollectorRequest(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        BuildTestContext context)
    {
        var expected = request.AcceptedCommand;
        var expectedFingerprint = new SandboxBuildTestApiRequest(
            expected.Checkpoint,
            request.ExpectedBinding).ComputeRequestFingerprint(expected);
        if (expected.Outputs.IsEmpty && expected.WorkspaceSource is null ||
            (collectorRequest.AcceptedCommandContractVersion ?? 1) != expected.ContractVersion ||
            collectorRequest.OperationId != expected.OperationId ||
            collectorRequest.ImmutableHash != expected.ImmutableHash ||
            collectorRequest.RequestFingerprint != expectedFingerprint ||
            !SameCheckpoint(collectorRequest.Checkpoint, expected.Checkpoint) ||
            collectorRequest.WorkspaceMountPath != context.Descriptor.WorkspaceMountPath ||
            collectorRequest.MaximumTotalBytes != expected.ExecutionOptions.MaximumFileOutputBytes ||
            collectorRequest.Outputs.Length != expected.Outputs.Length ||
            collectorRequest.Outputs.Where((output, index) =>
                output != expected.Outputs[index]).Any())
            throw new ArgumentException("The trusted output collector request does not match the accepted command.");
    }

    /// <summary>Builds the collector request from accepted command metadata and the exact workspace mount.</summary>
    private static SandboxBuildTestOutputCollectorRequest CreateCollectorRequest(
        SandboxBuildTestProviderRequest request,
        BuildTestContext context) =>
        new SandboxBuildTestOutputCollectorRequest(
            request.AcceptedCommand.OperationId,
            request.AcceptedCommand.ImmutableHash,
            new SandboxBuildTestApiRequest(
                request.AcceptedCommand.Checkpoint,
                request.ExpectedBinding).ComputeRequestFingerprint(request.AcceptedCommand),
            request.AcceptedCommand.Checkpoint,
            context.Descriptor.WorkspaceMountPath,
            request.AcceptedCommand.ExecutionOptions.MaximumFileOutputBytes,
            request.AcceptedCommand.Outputs)
        {
            AcceptedCommandContractVersion = request.AcceptedCommand.ContractVersion == 2 ? 2 : null
        }.Validate();

    /// <summary>Compares all checkpoint identity and revision fields used to pin the accepted command.</summary>
    private static bool SameCheckpoint(
        SandboxBuildTestCheckpointReference left,
        SandboxBuildTestCheckpointReference right) =>
        left.ProjectId == right.ProjectId &&
        left.RunId == right.RunId &&
        left.SessionId == right.SessionId &&
        left.CheckpointId == right.CheckpointId &&
        left.WorkPlanId == right.WorkPlanId &&
        left.StepId == right.StepId &&
        left.CheckpointRevision == right.CheckpointRevision &&
        left.DecisionStateVersion == right.DecisionStateVersion &&
        left.ExecutionFence == right.ExecutionFence &&
        string.Equals(left.AcceptedSelectionHash, right.AcceptedSelectionHash, StringComparison.Ordinal);

    /// <summary>Serializes a bounded collector request as unpadded base64url for the helper command line.</summary>
    private static string EncodeCollectorRequest(SandboxBuildTestOutputCollectorRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, BuildTestJsonOptions);
        if (bytes.Length > SandboxBuildTestLimits.MaximumCollectorRequestBytes)
            throw new ArgumentException("The BuildTest output-collector request exceeds its size limit.");
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Creates an unknown observation with an empty, correctly digested output capture.</summary>
    private static SandboxBuildTestPodObservation UnknownPodObservation(
        SandboxBuildTestPodReference pod,
        string containerName) =>
        new(
            pod,
            SandboxBuildTestPodState.Unknown,
            null,
            EmptyOutput(pod, containerName));

    /// <summary>Builds the zero-byte capture result used when no output was collected.</summary>
    private static SandboxBuildTestOutputCapture EmptyOutput(
        SandboxBuildTestPodReference pod,
        string containerName)
    {
        var bytes = ImmutableArray<byte>.Empty;
        return new(
            pod.Uid,
            containerName,
            bytes,
            0,
            Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            false,
            0);
    }

    /// <summary>Returns the accepted maximum output size for the command or collector role.</summary>
    private static int GetBuildTestMaximumOutputBytes(
        SandboxBuildTestProviderRequest request,
        string role) =>
        role == SandboxBuildTestLimits.CommandPolicyRole
            ? request.AcceptedCommand.ExecutionOptions.MaximumOutputBytes
            : SandboxBuildTestLimits.MaximumCollectorRequestBytes;

    /// <summary>Returns the fixed container name assigned to a BuildTest role.</summary>
    private static string GetBuildTestContainerName(string role) =>
        role == SandboxBuildTestLimits.CommandPolicyRole
            ? BuildTestCommandContainerName
            : SandboxBuildTestLimits.OutputCollectorContainerName;

    /// <summary>Derives the deterministic Pod name from its operation ID and role.</summary>
    private static string BuildTestPodName(Guid operationId, string role) =>
        $"{BuildTestPodNamePrefix}{operationId:N}-{role}";

    /// <summary>Returns a role-specific Pod name, or null when no role was selected.</summary>
    private static string? GetBuildTestPodName(
        SandboxBuildTestProviderRequest request,
        string? role) =>
        role is null
            ? null
            : BuildTestPodName(request.AcceptedCommand.OperationId, role);

    /// <summary>Determines which accepted role owns a Pod resource, rejecting names from another operation.</summary>
    private static string GetBuildTestPodRole(
        JsonElement pod,
        SandboxBuildTestProviderRequest request)
    {
        var name = ReadOptionalString(pod, "metadata", "name");
        return name == BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CommandPolicyRole)
            ? SandboxBuildTestLimits.CommandPolicyRole
            : name == BuildTestPodName(
                request.AcceptedCommand.OperationId,
                SandboxBuildTestLimits.CollectorPolicyRole)
                ? SandboxBuildTestLimits.CollectorPolicyRole
                : throw ProviderResourceMismatch("The BuildTest Pod name does not belong to this operation.");
    }

    /// <summary>Determines which accepted role owns a Pod reference, rejecting names from another operation.</summary>
    private static string GetRoleFromPodName(
        SandboxBuildTestProviderRequest request,
        string name) =>
        name == BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CommandPolicyRole)
            ? SandboxBuildTestLimits.CommandPolicyRole
            : name == BuildTestPodName(request.AcceptedCommand.OperationId, SandboxBuildTestLimits.CollectorPolicyRole)
                ? SandboxBuildTestLimits.CollectorPolicyRole
                : throw ProviderResourceMismatch("The BuildTest Pod reference does not belong to this operation.");

    /// <summary>Validates the exact Pod and namespace, then issues UID-preconditioned cleanup.</summary>
    /// <remarks>Missing Pods are treated as already deleted.</remarks>
    private async Task DeleteBuildTestPodAsync(
        SandboxBuildTestProviderRequest request,
        SandboxBuildTestPodReference pod,
        bool foreground,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ArgumentNullException.ThrowIfNull(pod);
        _ = pod.Validate();
        var role = GetRoleFromPodName(request, pod.Name);
        var context = ReadBuildTestContext(request);
        await VerifyBuildTestClusterIdentityAsync(context, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(pod.KubernetesNamespace, context.Descriptor.Namespace, StringComparison.Ordinal))
            throw ProviderResourceMismatch("The BuildTest Pod reference is outside the pinned namespace.");
        var resource = await _client.GetAsync(
            string.Empty, "pods", pod.KubernetesNamespace, pod.Name, cancellationToken).ConfigureAwait(false);
        if (resource is not null)
        {
            var current = ValidateBuildTestPod(resource.Value, request, context, role);
            if (!string.Equals(current.Uid, pod.Uid, StringComparison.Ordinal))
                throw ProviderResourceMismatch("The BuildTest Pod UID changed before cleanup.");
        }
        await _client.DeleteAsync(
            string.Empty,
            "pods",
            pod.KubernetesNamespace,
            pod.Name,
            pod.Uid,
            foreground,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Normalized expected policy UID, spec digest, generation, and selector for one helper role.</summary>
    private sealed record BuildTestPolicyDescriptor(
        string PolicyUid,
        string SpecSha256,
        long NetworkPolicyGeneration,
        ImmutableDictionary<string, string> SelectorLabels);

    /// <summary>Validated options, Sandbox recovery descriptor, and negotiated workspace for one BuildTest operation.</summary>
    private sealed record BuildTestContext(
        AgentSandboxOptions Options,
        AgentSandboxRecoveryDescriptor Descriptor,
        WorkspaceVolumeAttachmentNegotiation Workspace);
}
