using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;

namespace Agentweaver.Environment;

public sealed class EnvironmentSandboxBuildTestCommandManager(
    EnvironmentEgressManager egressManager,
    IEnvironmentLifecycleStore lifecycleStore,
    ISandboxLeaseStore leaseStore,
    IEnvironmentSandboxBuildTestCommandStore commandStore,
    ISandboxBuildTestAcceptedCommandVerifier commandVerifier,
    ISandboxBuildTestCommandProvider commandProvider,
    ICiliumPolicyResourceStore policyStore,
    TimeProvider timeProvider,
    CiliumEgressProviderOptions ciliumOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions PolicyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private const string PendingPolicyUid = "pending";
    private const string CommandPolicyCreateAttempt = "command-policy:create";
    private const string CommandPolicyObserved = "command-policy:observed";
    private const string CollectorPolicyCreateAttempt = "collector-policy:create";
    private const string CollectorPolicyObserved = "collector-policy:observed";
    private const string CommandPodCreateAttempt = "command-pod:create";
    private const string CommandPodObserved = "command-pod:observed";
    private const string CollectorPodCreateAttempt = "collector-pod:create";
    private const string CollectorPodObserved = "collector-pod:observed";
    private const string CommandPodGateReleased = "command-pod:gate-released";
    private const string CollectorPodGateReleased = "collector-pod:gate-released";
    private const string CommandSucceededMarker = "command:exit-zero-observed";

    public async Task<SandboxBuildTestOperationResult> ExecuteAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateRequestOwner(request, projectId, runId, environmentId);
        var acceptedCommand = await ResolveAcceptedCommandAsync(request.Checkpoint, cancellationToken)
            .ConfigureAwait(false);
        var context = await RequirePreparedBindingAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            cancellationToken).ConfigureAwait(false);
        var initialOperation = CreateInitialOperation(request, acceptedCommand, timeProvider.GetUtcNow());
        var reservation = await commandStore.ReserveAsync(
            request,
            acceptedCommand,
            initialOperation,
            cancellationToken).ConfigureAwait(false);
        if (reservation.Replayed)
            return new(reservation.State.Operation, true);

        var result = await AdvanceAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            context,
            reservation.State,
            cancellationToken).ConfigureAwait(false);
        return new(result.Operation, false);
    }

    public async Task<SandboxBuildTestOperationResult?> GetAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("A non-empty BuildTest operation ID is required.", nameof(operationId));
        var context = await ReadPinnedContextAsync(
            caller,
            projectId,
            runId,
            environmentId,
            cancellationToken).ConfigureAwait(false);
        await VerifyNetworkAndBindingAsync(caller, context, cancellationToken).ConfigureAwait(false);
        var state = await commandStore.GetAsync(context.Owner, operationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
            return null;
        if (!SameJson(state.Operation.ExpectedBinding, context.ExpectedBinding))
            throw new EnvironmentLifecycleException(
                "sandbox_binding_stale",
                "The BuildTest operation no longer matches the current owner binding.");
        return new(state.Operation, true);
    }

    public async Task<SandboxBuildTestOperationResult> ReconcileAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        Guid operationId,
        SandboxBuildTestApiRequest request,
        CancellationToken cancellationToken)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        ValidateRequestOwner(request, projectId, runId, environmentId);
        var acceptedCommand = await ResolveAcceptedCommandAsync(request.Checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (acceptedCommand.OperationId != operationId)
            throw new EnvironmentLifecycleException(
                "buildtest_operation_conflict",
                "The checkpoint resolves to a different BuildTest operation ID.");
        var context = await RequirePreparedBindingAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            cancellationToken).ConfigureAwait(false);
        var state = await commandStore.GetAsync(context.Owner, operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "buildtest_operation_unknown",
                "The exact BuildTest operation is not reserved.");
        if (!SameJson(state.AcceptedCommand, acceptedCommand) ||
            !SameJson(state.Operation.ExpectedBinding, request.ExpectedBinding) ||
            state.Operation.RequestFingerprint != request.ComputeRequestFingerprint(acceptedCommand))
            throw new EnvironmentLifecycleException(
                "buildtest_request_conflict",
                "The reconciliation request differs from its persisted immutable BuildTest intent.");
        if (IsTerminal(state.Operation.Status))
            return new(state.Operation, true);

        var result = await AdvanceAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            context,
            state,
            cancellationToken).ConfigureAwait(false);
        return new(result.Operation, true);
    }

    public async Task<SandboxBuildTestBindingPreparation> PrepareBindingAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        string sessionId,
        string executionProfileReference,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueIdentifier(sessionId, nameof(sessionId), 256);
        ValidateOpaqueIdentifier(executionProfileReference, nameof(executionProfileReference), 128);
        var context = await ReadPinnedContextAsync(
            caller,
            projectId,
            runId,
            environmentId,
            cancellationToken).ConfigureAwait(false);
        var profile = context.Options.AcceptedBuildTestProfile
            ?? throw new EnvironmentLifecycleException(
                "buildtest_capability_unavailable",
                "The current Sandbox lease does not pin an accepted BuildTest profile.");
        if (!string.Equals(profile.ProfileId, executionProfileReference, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "buildtest_profile_mismatch",
                "The requested BuildTest profile is not the one pinned by the current Sandbox lease.");

        await egressManager.VerifyNetworkForSandboxAsync(
            caller,
            context.Fence,
            context.Selection,
            context.ProvisionRequest.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
        await ConfirmUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return new SandboxBuildTestBindingPreparation(
            sessionId,
            profile.ProfileId,
            context.Lease.ProvisionIntent.OptionsRevision,
            SandboxBuildTestBindingPreparation.ComputeExecutionOptionsSha256(profile),
            context.ExpectedBinding,
            profile).Validate(sessionId, executionProfileReference);
    }

    private async Task<SandboxBuildTestAcceptedCommand> ResolveAcceptedCommandAsync(
        SandboxBuildTestCheckpointReference checkpoint,
        CancellationToken cancellationToken)
    {
        var command = (await commandVerifier.ResolveAsync(checkpoint, cancellationToken).ConfigureAwait(false))
            .Validate();
        if (command.Checkpoint != checkpoint)
            throw new RuntimeAuthorizationException("buildtest_command_checkpoint_mismatch");
        return command;
    }

    private async Task<PinnedBuildTestContext> RequirePreparedBindingAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        CancellationToken cancellationToken)
    {
        var context = await ReadPinnedContextAsync(
            caller,
            projectId,
            runId,
            environmentId,
            cancellationToken).ConfigureAwait(false);
        if (!SameJson(context.ExpectedBinding, request.ExpectedBinding))
            throw new EnvironmentLifecycleException(
                "sandbox_binding_stale",
                "The prepared BuildTest binding no longer matches the exact current Sandbox and Workspace.");
        if (context.Options.AcceptedBuildTestProfile is not { } profile ||
            !SameJson(profile, acceptedCommand.ExecutionOptions))
            throw new EnvironmentLifecycleException(
                "buildtest_profile_mismatch",
                "The accepted command options differ from the profile pinned by the current Sandbox lease.");
        await VerifyNetworkAndBindingAsync(caller, context, cancellationToken).ConfigureAwait(false);
        return context;
    }

    private async Task VerifyNetworkAndBindingAsync(
        CurrentCallerRequest caller,
        PinnedBuildTestContext context,
        CancellationToken cancellationToken)
    {
        await egressManager.VerifyNetworkForSandboxAsync(
            caller,
            context.Fence,
            context.Selection,
            context.ProvisionRequest.NetworkPolicyGeneration,
            cancellationToken).ConfigureAwait(false);
        await ConfirmUnchangedAsync(caller, context, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateRequestOwner(
        SandboxBuildTestApiRequest request,
        string projectId,
        string runId,
        string environmentId)
    {
        if (!string.Equals(request.Checkpoint.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(request.Checkpoint.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedBinding.Fence.Owner.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedBinding.Fence.Owner.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(request.ExpectedBinding.Fence.Owner.EnvironmentId, environmentId, StringComparison.Ordinal))
            throw new ArgumentException("The BuildTest request must match its project, run, and Environment route.");
    }

    private SandboxBuildTestOperationSnapshot CreateInitialOperation(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        DateTimeOffset now)
    {
        var commandPolicy = CreatePolicyDefinition(
            acceptedCommand.OperationId,
            request.ExpectedBinding,
            SandboxBuildTestLimits.CommandPolicyRole).CommandBinding
            ?? throw new EnvironmentLifecycleException(
                "buildtest_policy_invalid",
                "The expected command-network-policy binding could not be created.");
        var collectorPolicy = acceptedCommand.Outputs.IsEmpty
            ? null
            : CreatePolicyDefinition(
                acceptedCommand.OperationId,
                request.ExpectedBinding,
                SandboxBuildTestLimits.CollectorPolicyRole).CollectorBinding;
        return new SandboxBuildTestOperationSnapshot(
            acceptedCommand.OperationId,
            acceptedCommand.ImmutableHash,
            request.ComputeRequestFingerprint(acceptedCommand),
            SandboxBuildTestOperationStatus.Reserved,
            request.ExpectedBinding,
            commandPolicy,
            null,
            null,
            null,
            null,
            null,
            null,
            collectorPolicy,
            null,
            [],
            null,
            now,
            now).Validate(acceptedCommand, acceptedCommand.ExecutionOptions.MaximumOutputBytes);
    }

    private BuildTestPolicyDefinition CreatePolicyDefinition(
        Guid operationId,
        SandboxBuildTestExpectedBinding expectedBinding,
        string role)
    {
        var policyName = role == SandboxBuildTestLimits.CommandPolicyRole
            ? AgentSandboxBuildTestPolicyNames.Command(operationId)
            : AgentSandboxBuildTestPolicyNames.Collector(operationId);
        var selector = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(
                SandboxBuildTestLimits.PolicyOperationLabel,
                operationId.ToString("N")),
            new KeyValuePair<string, string>(SandboxBuildTestLimits.PolicyRoleLabel, role),
            new KeyValuePair<string, string>(
                "agentweaver.dev/buildtest-lifecycle",
                expectedBinding.Fence.LifecycleGeneration.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(
                "agentweaver.dev/buildtest-sandbox-generation",
                expectedBinding.SandboxResource.Generation.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>(
                "agentweaver.dev/buildtest-fencing-generation",
                expectedBinding.ProviderFencingGeneration.ToString(CultureInfo.InvariantCulture))
        ]);
        var spec = new CiliumPolicySpec(
            new CiliumEndpointSelector(selector),
            ImmutableArray<CiliumEgressRuleDocument>.Empty,
            EnableDefaultDeny: new(Ingress: true, Egress: true));
        var annotations = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(SandboxBuildTestLimits.PolicyOperationLabel, operationId.ToString("N")),
            new KeyValuePair<string, string>(SandboxBuildTestLimits.PolicyRoleLabel, role),
            new KeyValuePair<string, string>(
                "agentweaver.dev/buildtest-network-generation",
                expectedBinding.NetworkPolicyGeneration.ToString(CultureInfo.InvariantCulture))
        ]);
        var metadataLabels = ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>("app.kubernetes.io/managed-by", "agentweaver-environment"),
            new KeyValuePair<string, string>("agentweaver.dev/managed-buildtest", "true")
        ]);
        var resource = new CiliumNetworkPolicyDocument(
            "cilium.io/v2",
            "CiliumNetworkPolicy",
            new CiliumPolicyMetadata(policyName, ciliumOptions.Namespace, metadataLabels, annotations),
            spec);
        var specJson = JsonSerializer.SerializeToElement(spec, PolicyJsonOptions);
        var bindingHash = SandboxBuildTestCommandPolicyCanonicalization.ComputeSpecSha256(specJson);
        if (role == SandboxBuildTestLimits.CommandPolicyRole)
        {
            var commandBinding = new SandboxBuildTestCommandNetworkPolicyBinding(
                PendingPolicyUid,
                bindingHash,
                expectedBinding.NetworkPolicyGeneration,
                selector).Validate();
            return new(resource, commandBinding, null);
        }

        var collectorBinding = new SandboxBuildTestOutputCollectorNetworkPolicyBinding(
            PendingPolicyUid,
            bindingHash,
            expectedBinding.NetworkPolicyGeneration,
            selector).Validate();
        return new(resource, null, collectorBinding);
    }

    private static SandboxBuildTestCommandNetworkPolicyBinding BindCommandPolicy(
        BuildTestPolicyDefinition definition,
        CiliumNetworkPolicyDocument actual)
    {
        ValidatePolicyResource(definition, actual);
        return definition.CommandBinding! with { PolicyUid = actual.Metadata.Uid! };
    }

    private static SandboxBuildTestOutputCollectorNetworkPolicyBinding BindCollectorPolicy(
        BuildTestPolicyDefinition definition,
        CiliumNetworkPolicyDocument actual)
    {
        ValidatePolicyResource(definition, actual);
        return definition.CollectorBinding! with { PolicyUid = actual.Metadata.Uid! };
    }

    private static void ValidatePolicyResource(
        BuildTestPolicyDefinition definition,
        CiliumNetworkPolicyDocument actual)
    {
        var desired = definition.Resource;
        if (!string.Equals(actual.Metadata.Name, desired.Metadata.Name, StringComparison.Ordinal) ||
            !string.Equals(actual.Metadata.Namespace, desired.Metadata.Namespace, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(actual.Metadata.Uid) ||
            string.Equals(actual.Metadata.Uid, PendingPolicyUid, StringComparison.Ordinal) ||
            !SameJson(actual.Metadata.Labels, desired.Metadata.Labels) ||
            !SameJson(actual.Metadata.Annotations, desired.Metadata.Annotations) ||
            !SameJson(actual.Spec, desired.Spec))
            throw new EnvironmentLifecycleException(
                "buildtest_policy_mismatch",
                "The exact BuildTest deny-all Cilium policy does not match its persisted intent.");
        var specJson = JsonSerializer.SerializeToElement(actual.Spec, PolicyJsonOptions);
        var expectedHash = definition.CommandBinding?.SpecSha256 ??
            definition.CollectorBinding?.SpecSha256;
        if (!string.Equals(
                SandboxBuildTestCommandPolicyCanonicalization.ComputeSpecSha256(specJson),
                expectedHash,
                StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "buildtest_policy_mismatch",
                "The exact BuildTest Cilium policy specification hash is invalid.");
        ValidateOpaqueIdentifier(actual.Metadata.Uid, nameof(actual.Metadata.Uid), 256);
    }

    private static bool IsTerminal(SandboxBuildTestOperationStatus status) =>
        status is SandboxBuildTestOperationStatus.Completed or
            SandboxBuildTestOperationStatus.Failed or
            SandboxBuildTestOperationStatus.Interrupted or
            SandboxBuildTestOperationStatus.Stale;

    private async Task<EnvironmentSandboxBuildTestCommandState> AdvanceAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        PinnedBuildTestContext context,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken)
    {
        if (IsTerminal(state.Operation.Status))
            return state;

        state = await EnsurePolicyAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            state,
            SandboxBuildTestLimits.CommandPolicyRole,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired)
            return state;
        if (IsTerminal(state.Operation.Status))
            return state;

        var providerRequest = CreateProviderRequest(state);
        state = await EnsureCommandPodAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            providerRequest,
            state,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired ||
            IsTerminal(state.Operation.Status))
            return state;

        providerRequest = CreateProviderRequest(state);
        state = await ReleasePodGateAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            providerRequest,
            state,
            SandboxBuildTestLimits.CommandPolicyRole,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired ||
            IsTerminal(state.Operation.Status))
            return state;

        providerRequest = CreateProviderRequest(state);
        var commandPod = state.Operation.Pod
            ?? throw new EnvironmentLifecycleException(
                "buildtest_record_invalid",
                "The reserved BuildTest operation has no command Pod reference.");
        var commandObservation = (await commandProvider.ObserveAsync(
            providerRequest,
            commandPod,
            acceptedCommand.ExecutionOptions.MaximumOutputBytes,
            cancellationToken).ConfigureAwait(false))
            .Validate(acceptedCommand.ExecutionOptions.MaximumOutputBytes);
        state = await RecordCommandObservationAsync(
            request,
            acceptedCommand,
            providerRequest,
            state,
            commandObservation,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status != SandboxBuildTestOperationStatus.Running ||
            acceptedCommand.Outputs.IsEmpty ||
            commandObservation.State != SandboxBuildTestPodState.Succeeded ||
            commandObservation.Terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 })
            return state;

        state = await EnsurePolicyAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            state,
            SandboxBuildTestLimits.CollectorPolicyRole,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired)
            return state;
        if (IsTerminal(state.Operation.Status))
            return state;

        providerRequest = CreateProviderRequest(state);
        var collectorRequest = CreateCollectorRequest(
            request,
            acceptedCommand,
            context.ProvisionRequest.MountPath);
        state = await EnsureCollectorPodAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            providerRequest,
            collectorRequest,
            state,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired ||
            IsTerminal(state.Operation.Status))
            return state;

        providerRequest = CreateProviderRequest(state);
        state = await ReleasePodGateAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            providerRequest,
            state,
            SandboxBuildTestLimits.CollectorPolicyRole,
            cancellationToken).ConfigureAwait(false);
        if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired ||
            IsTerminal(state.Operation.Status))
            return state;

        providerRequest = CreateProviderRequest(state);
        var collectorPod = state.Operation.CollectorPod
            ?? throw new EnvironmentLifecycleException(
                "buildtest_record_invalid",
                "The BuildTest output collector has no Pod reference.");
        var collectorObservation = (await commandProvider.ObserveAsync(
            providerRequest,
            collectorPod,
            SandboxBuildTestLimits.MaximumCollectorRequestBytes,
            cancellationToken).ConfigureAwait(false))
            .Validate(SandboxBuildTestLimits.MaximumCollectorRequestBytes);
        return await RecordCollectorObservationAsync(
            request,
            acceptedCommand,
            providerRequest,
            collectorRequest,
            state,
            commandObservation,
            collectorObservation,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> EnsurePolicyAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        EnvironmentSandboxBuildTestCommandState state,
        string role,
        CancellationToken cancellationToken)
    {
        var definition = CreatePolicyDefinition(
            acceptedCommand.OperationId,
            request.ExpectedBinding,
            role);
        var existing = await policyStore.GetAsync(
            ciliumOptions.Namespace,
            definition.Resource.Metadata.Name,
            cancellationToken).ConfigureAwait(false);
        var currentUid = GetPolicyUid(state.Operation, role);
        if (existing is not null)
        {
            var observedUid = role == SandboxBuildTestLimits.CommandPolicyRole
                ? BindCommandPolicy(definition, existing).PolicyUid
                : BindCollectorPolicy(definition, existing).PolicyUid;
            if (currentUid != PendingPolicyUid &&
                !string.Equals(currentUid, observedUid, StringComparison.Ordinal))
                return await MarkReconciliationRequiredAsync(
                    state,
                    "buildtest_policy_uid_changed",
                    cancellationToken).ConfigureAwait(false);
            var observedAttempt = role == SandboxBuildTestLimits.CommandPolicyRole
                ? CommandPolicyObserved
                : CollectorPolicyObserved;
            if (currentUid == PendingPolicyUid &&
                !state.AttemptedEffects.Contains(
                    role == SandboxBuildTestLimits.CommandPolicyRole
                        ? CommandPolicyCreateAttempt
                        : CollectorPolicyCreateAttempt,
                    StringComparer.Ordinal))
                state = await AddAttemptAsync(state, observedAttempt, cancellationToken).ConfigureAwait(false);
            state = await BindPolicyAsync(state, role, observedUid, cancellationToken).ConfigureAwait(false);
            if (state.Operation.Status == SandboxBuildTestOperationStatus.ReconciliationRequired)
                state = await SaveOperationAsync(
                    state,
                    state.Operation with
                    {
                        Status = SandboxBuildTestOperationStatus.Reserved,
                        FailureCode = null
                    },
                    cancellationToken).ConfigureAwait(false);
            return state;
        }

        var attempt = role == SandboxBuildTestLimits.CommandPolicyRole
            ? CommandPolicyCreateAttempt
            : CollectorPolicyCreateAttempt;
        if (currentUid != PendingPolicyUid ||
            state.AttemptedEffects.Contains(attempt, StringComparer.Ordinal))
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_policy_effect_unknown",
                cancellationToken).ConfigureAwait(false);

        state = await RequireCurrentIntentAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            state,
            cancellationToken).ConfigureAwait(false);
        if (IsTerminal(state.Operation.Status))
            return state;
        state = await AddAttemptAsync(state, attempt, cancellationToken).ConfigureAwait(false);
        try
        {
            var created = await policyStore.CreateAsync(definition.Resource, cancellationToken)
                .ConfigureAwait(false);
            var createdUid = role == SandboxBuildTestLimits.CommandPolicyRole
                ? BindCommandPolicy(definition, created).PolicyUid
                : BindCollectorPolicy(definition, created).PolicyUid;
            return await BindPolicyAsync(state, role, createdUid, cancellationToken).ConfigureAwait(false);
        }
        catch (CiliumPolicyException)
        {
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_policy_effect_unknown",
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_policy_effect_unknown",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> EnsureCommandPodAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestProviderRequest providerRequest,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken)
    {
        var existing = await commandProvider.GetAsync(providerRequest, cancellationToken).ConfigureAwait(false);
        var stored = state.Operation.Pod;
        if (existing is null)
        {
            if (stored is not null || state.AttemptedEffects.Contains(CommandPodCreateAttempt, StringComparer.Ordinal))
                return await MarkReconciliationRequiredAsync(
                    state,
                    "buildtest_command_pod_effect_unknown",
                    cancellationToken).ConfigureAwait(false);
            state = await RequireCurrentIntentAsync(
                caller,
                projectId,
                runId,
                environmentId,
                request,
                acceptedCommand,
                state,
                cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Operation.Status))
                return state;
            state = await AddAttemptAsync(state, CommandPodCreateAttempt, cancellationToken).ConfigureAwait(false);
            try
            {
                existing = await commandProvider.CreateGatedAsync(providerRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SandboxProviderException exception)
            {
                return await MarkProviderFailureAsync(state, exception, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (stored is not null && !string.Equals(stored.Uid, existing.Uid, StringComparison.Ordinal))
        {
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_command_pod_uid_changed",
                cancellationToken).ConfigureAwait(false);
        }
        else if (stored is null)
        {
            state = await AddAttemptAsync(state, CommandPodObserved, cancellationToken).ConfigureAwait(false);
        }

        return await SaveOperationAsync(
            state,
            state.Operation with
            {
                Status = SandboxBuildTestOperationStatus.Running,
                Pod = existing,
                CurrentBinding = CreateObservedBinding(
                    state.Operation.ExpectedBinding,
                    state.Operation.ExpectedCommandPolicy),
                FailureCode = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> EnsureCollectorPodAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestProviderRequest providerRequest,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken)
    {
        var existing = await commandProvider.GetOutputCollectorAsync(providerRequest, cancellationToken)
            .ConfigureAwait(false);
        var stored = state.Operation.CollectorPod;
        if (existing is null)
        {
            if (stored is not null ||
                state.AttemptedEffects.Contains(CollectorPodCreateAttempt, StringComparer.Ordinal))
                return await MarkReconciliationRequiredAsync(
                    state,
                    "buildtest_collector_pod_effect_unknown",
                    cancellationToken).ConfigureAwait(false);
            state = await RequireCurrentIntentAsync(
                caller,
                projectId,
                runId,
                environmentId,
                request,
                acceptedCommand,
                state,
                cancellationToken).ConfigureAwait(false);
            if (IsTerminal(state.Operation.Status))
                return state;
            state = await AddAttemptAsync(state, CollectorPodCreateAttempt, cancellationToken).ConfigureAwait(false);
            try
            {
                existing = await commandProvider.CreateOutputCollectorGatedAsync(
                    providerRequest,
                    collectorRequest,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SandboxProviderException exception)
            {
                return await MarkProviderFailureAsync(state, exception, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (stored is not null && !string.Equals(stored.Uid, existing.Uid, StringComparison.Ordinal))
        {
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_collector_pod_uid_changed",
                cancellationToken).ConfigureAwait(false);
        }
        else if (stored is null)
        {
            state = await AddAttemptAsync(state, CollectorPodObserved, cancellationToken).ConfigureAwait(false);
        }

        return await SaveOperationAsync(
            state,
            state.Operation with
            {
                Status = SandboxBuildTestOperationStatus.Running,
                CollectorPod = existing,
                FailureCode = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> ReleasePodGateAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestProviderRequest providerRequest,
        EnvironmentSandboxBuildTestCommandState state,
        string role,
        CancellationToken cancellationToken)
    {
        var pod = role == SandboxBuildTestLimits.CommandPolicyRole
            ? state.Operation.Pod
            : state.Operation.CollectorPod;
        if (pod is null)
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_pod_reference_missing",
                cancellationToken).ConfigureAwait(false);

        var releasedMarker = role == SandboxBuildTestLimits.CommandPolicyRole
            ? CommandPodGateReleased
            : CollectorPodGateReleased;
        if (state.AttemptedEffects.Contains(releasedMarker, StringComparer.Ordinal))
            return state;

        state = await RequireCurrentIntentAsync(
            caller,
            projectId,
            runId,
            environmentId,
            request,
            acceptedCommand,
            state,
            cancellationToken).ConfigureAwait(false);
        if (IsTerminal(state.Operation.Status))
            return state;
        var prefix = role == SandboxBuildTestLimits.CommandPolicyRole
            ? "command-pod:release:"
            : "collector-pod:release:";
        state = await AddAttemptAsync(state, NextAttemptMarker(state, prefix), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var released = await commandProvider.ReleaseSchedulingGateAsync(
                providerRequest,
                pod,
                cancellationToken).ConfigureAwait(false);
            state = await AddAttemptAsync(state, releasedMarker, cancellationToken).ConfigureAwait(false);
            var operation = role == SandboxBuildTestLimits.CommandPolicyRole
                ? state.Operation with { Pod = released }
                : state.Operation with { CollectorPod = released };
            return await SaveOperationAsync(
                state,
                operation with { Status = SandboxBuildTestOperationStatus.Running, FailureCode = null },
                cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxProviderException exception)
        {
            return await MarkProviderFailureAsync(state, exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> RecordCommandObservationAsync(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestProviderRequest providerRequest,
        EnvironmentSandboxBuildTestCommandState state,
        SandboxBuildTestPodObservation observation,
        CancellationToken cancellationToken)
    {
        var operation = state.Operation with
        {
            Pod = observation.Pod,
            CurrentBinding = CreateObservedBinding(
                state.Operation.ExpectedBinding,
                state.Operation.ExpectedCommandPolicy),
            Output = observation.Output,
            FailureCode = null
        };
        if (observation.State is SandboxBuildTestPodState.Pending or SandboxBuildTestPodState.Running)
        {
            if (state.AttemptedEffects.Contains(CommandSucceededMarker, StringComparer.Ordinal))
                return await MarkReconciliationRequiredAsync(
                    state,
                    "buildtest_command_terminal_state_changed",
                    cancellationToken).ConfigureAwait(false);
            return await SaveOperationAsync(
                state,
                operation with { Status = SandboxBuildTestOperationStatus.Running, Terminal = null },
                cancellationToken).ConfigureAwait(false);
        }
        if (observation.State == SandboxBuildTestPodState.Unknown || observation.Terminal is null)
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_command_state_unknown",
                cancellationToken,
                operation).ConfigureAwait(false);

        var terminal = observation.Terminal;
        if (terminal.Kind != SandboxBuildTestTerminationKind.Exited)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Interrupted,
                    Terminal = terminal,
                    FailureCode = "buildtest_command_interrupted"
                },
                cancellationToken).ConfigureAwait(false);
        if (terminal.ExitCode != 0)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = terminal,
                    FailureCode = "buildtest_command_failed"
                },
                cancellationToken).ConfigureAwait(false);
        if (observation.Output.Truncated)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = terminal,
                    FailureCode = "buildtest_command_output_truncated"
                },
                cancellationToken).ConfigureAwait(false);

        if (acceptedCommand.Outputs.IsEmpty)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Completed,
                    Terminal = terminal
                },
                cancellationToken).ConfigureAwait(false);

        if (!state.AttemptedEffects.Contains(CommandSucceededMarker, StringComparer.Ordinal))
            state = await AddAttemptAsync(state, CommandSucceededMarker, cancellationToken).ConfigureAwait(false);
        return await SaveOperationAsync(
            state,
            operation with
            {
                Status = SandboxBuildTestOperationStatus.Running,
                Terminal = null
            },
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<EnvironmentSandboxBuildTestCommandState> RecordCollectorObservationAsync(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        SandboxBuildTestProviderRequest providerRequest,
        SandboxBuildTestOutputCollectorRequest collectorRequest,
        EnvironmentSandboxBuildTestCommandState state,
        SandboxBuildTestPodObservation commandObservation,
        SandboxBuildTestPodObservation collectorObservation,
        CancellationToken cancellationToken)
    {
        var operation = state.Operation with
        {
            CollectorPod = collectorObservation.Pod,
            Output = commandObservation.Output,
            CurrentBinding = CreateObservedBinding(
                state.Operation.ExpectedBinding,
                state.Operation.ExpectedCommandPolicy),
            FailureCode = null
        };
        if (collectorObservation.State is SandboxBuildTestPodState.Pending or SandboxBuildTestPodState.Running)
            return await SaveOperationAsync(
                state,
                operation with { Status = SandboxBuildTestOperationStatus.Running, Terminal = null },
                cancellationToken).ConfigureAwait(false);
        if (collectorObservation.State == SandboxBuildTestPodState.Unknown ||
            collectorObservation.Terminal is null ||
            commandObservation.Terminal is not { Kind: SandboxBuildTestTerminationKind.Exited, ExitCode: 0 })
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_collector_state_unknown",
                cancellationToken,
                operation).ConfigureAwait(false);

        var commandTerminal = commandObservation.Terminal;
        var collectorTerminal = collectorObservation.Terminal;
        if (collectorTerminal.Kind != SandboxBuildTestTerminationKind.Exited)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Interrupted,
                    Terminal = commandTerminal,
                    CollectorTerminal = collectorTerminal,
                    FailureCode = "buildtest_output_collection_interrupted"
                },
                cancellationToken).ConfigureAwait(false);
        if (collectorTerminal.ExitCode != 0 || collectorObservation.Output.Truncated)
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = commandTerminal,
                    CollectorTerminal = collectorTerminal,
                    FailureCode = "buildtest_output_collection_failed"
                },
                cancellationToken).ConfigureAwait(false);

        SandboxBuildTestOutputCollectorReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<SandboxBuildTestOutputCollectorReceipt>(
                    Encoding.UTF8.GetString(collectorObservation.Output.CapturedBytes.AsSpan()),
                    JsonOptions)
                ?? throw new JsonException("The BuildTest collector receipt is empty.");
            _ = receipt.Validate(collectorRequest, collectorObservation.Pod);
        }
        catch (JsonException)
        {
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = commandTerminal,
                    CollectorTerminal = collectorTerminal,
                    FailureCode = "buildtest_output_receipt_invalid"
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = commandTerminal,
                    CollectorTerminal = collectorTerminal,
                    FailureCode = "buildtest_output_receipt_invalid"
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (receipt.Outputs.Any(output => output.Required && !output.Exists))
            return await SaveOperationAsync(
                state,
                operation with
                {
                    Status = SandboxBuildTestOperationStatus.Failed,
                    Terminal = commandTerminal,
                    CollectorTerminal = collectorTerminal,
                    CollectorManifestSha256 = receipt.ManifestSha256,
                    OutputEvidence = receipt.Outputs,
                    FailureCode = "buildtest_required_output_missing"
                },
                cancellationToken).ConfigureAwait(false);

        return await SaveOperationAsync(
            state,
            operation with
            {
                Status = SandboxBuildTestOperationStatus.Completed,
                Terminal = commandTerminal,
                CollectorTerminal = collectorTerminal,
                CollectorManifestSha256 = receipt.ManifestSha256,
                OutputEvidence = receipt.Outputs
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static SandboxBuildTestProviderRequest CreateProviderRequest(
        EnvironmentSandboxBuildTestCommandState state) =>
        new SandboxBuildTestProviderRequest(
            state.AcceptedCommand,
            state.Operation.ExpectedBinding,
            state.Operation.ExpectedCommandPolicy)
        {
            CollectorPolicy = state.Operation.CollectorPolicy
        }.Validate();

    private static SandboxBuildTestOutputCollectorRequest CreateCollectorRequest(
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        string workspaceMountPath) =>
        new SandboxBuildTestOutputCollectorRequest(
            acceptedCommand.OperationId,
            acceptedCommand.ImmutableHash,
            request.ComputeRequestFingerprint(acceptedCommand),
            acceptedCommand.Checkpoint,
            workspaceMountPath,
            acceptedCommand.ExecutionOptions.MaximumFileOutputBytes,
            acceptedCommand.Outputs).Validate();

    private static string GetPolicyUid(
        SandboxBuildTestOperationSnapshot operation,
        string role) =>
        role switch
        {
            SandboxBuildTestLimits.CommandPolicyRole => operation.ExpectedCommandPolicy.PolicyUid,
            SandboxBuildTestLimits.CollectorPolicyRole => operation.CollectorPolicy?.PolicyUid
                ?? throw new EnvironmentLifecycleException(
                    "buildtest_policy_invalid",
                    "The BuildTest operation has no output-collector policy binding."),
            _ => throw new ArgumentException("An exact BuildTest policy role is required.", nameof(role))
        };

    private async Task<EnvironmentSandboxBuildTestCommandState> BindPolicyAsync(
        EnvironmentSandboxBuildTestCommandState state,
        string role,
        string policyUid,
        CancellationToken cancellationToken)
    {
        ValidateOpaqueIdentifier(policyUid, nameof(policyUid), 256);
        var operation = role switch
        {
            SandboxBuildTestLimits.CommandPolicyRole => state.Operation with
            {
                ExpectedCommandPolicy = state.Operation.ExpectedCommandPolicy with { PolicyUid = policyUid }
            },
            SandboxBuildTestLimits.CollectorPolicyRole when state.Operation.CollectorPolicy is { } collectorPolicy =>
                state.Operation with
                {
                    CollectorPolicy = collectorPolicy with { PolicyUid = policyUid }
                },
            _ => throw new ArgumentException("An exact BuildTest policy role is required.", nameof(role))
        };
        return await SaveOperationAsync(state, operation, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> RequireCurrentIntentAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        SandboxBuildTestApiRequest request,
        SandboxBuildTestAcceptedCommand acceptedCommand,
        EnvironmentSandboxBuildTestCommandState state,
        CancellationToken cancellationToken)
    {
        var currentCommand = await ResolveAcceptedCommandAsync(request.Checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!SameJson(currentCommand, acceptedCommand) ||
            !SameJson(currentCommand, state.AcceptedCommand))
            return await MarkStaleAsync(state, "buildtest_checkpoint_stale", cancellationToken)
                .ConfigureAwait(false);

        try
        {
            var current = await RequirePreparedBindingAsync(
                caller,
                projectId,
                runId,
                environmentId,
                request,
                acceptedCommand,
                cancellationToken).ConfigureAwait(false);
            if (!SameJson(current.ExpectedBinding, state.Operation.ExpectedBinding))
                return await MarkStaleAsync(state, "sandbox_binding_stale", cancellationToken)
                    .ConfigureAwait(false);
            return state;
        }
        catch (EnvironmentLifecycleException exception) when (IsStaleBindingFailure(exception.Code))
        {
            return await MarkStaleAsync(state, exception.Code, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsStaleBindingFailure(string code) =>
        code is "environment_unknown" or
            "environment_released" or
            "environment_fence_stale" or
            "sandbox_lease_unknown" or
            "sandbox_lease_not_active" or
            "sandbox_provider_binding_mismatch" or
            "sandbox_binding_stale" or
            "workspace_volume_unknown" or
            "workspace_generation_mismatch" or
            "workspace_unsupported" or
            "buildtest_profile_mismatch";

    private async Task<EnvironmentSandboxBuildTestCommandState> AddAttemptAsync(
        EnvironmentSandboxBuildTestCommandState state,
        string marker,
        CancellationToken cancellationToken)
    {
        if (state.AttemptedEffects.IsDefault ||
            string.IsNullOrWhiteSpace(marker) ||
            marker.Length > 256 ||
            marker.Any(char.IsControl))
            throw new EnvironmentLifecycleException(
                "buildtest_attempt_invalid",
                "The BuildTest effect-attempt marker is invalid.");
        if (state.AttemptedEffects.Contains(marker, StringComparer.Ordinal))
            return state;
        return await SaveOperationAsync(
            state with { AttemptedEffects = state.AttemptedEffects.Add(marker) },
            state.Operation,
            cancellationToken).ConfigureAwait(false);
    }

    private static string NextAttemptMarker(
        EnvironmentSandboxBuildTestCommandState state,
        string prefix)
    {
        var next = state.AttemptedEffects.Count(effect =>
            effect.StartsWith(prefix, StringComparison.Ordinal)) + 1;
        return prefix + next.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> MarkReconciliationRequiredAsync(
        EnvironmentSandboxBuildTestCommandState state,
        string failureCode,
        CancellationToken cancellationToken,
        SandboxBuildTestOperationSnapshot? operation = null)
    {
        if (IsTerminal(state.Operation.Status))
            return state;
        var updated = (operation ?? state.Operation) with
        {
            Status = SandboxBuildTestOperationStatus.ReconciliationRequired,
            FailureCode = SafeFailureCode(failureCode)
        };
        return await SaveOperationAsync(state, updated, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> MarkStaleAsync(
        EnvironmentSandboxBuildTestCommandState state,
        string failureCode,
        CancellationToken cancellationToken)
    {
        if (IsTerminal(state.Operation.Status))
            return state;
        return await SaveOperationAsync(
            state,
            state.Operation with
            {
                Status = SandboxBuildTestOperationStatus.Stale,
                FailureCode = SafeFailureCode(failureCode)
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> MarkProviderFailureAsync(
        EnvironmentSandboxBuildTestCommandState state,
        SandboxProviderException exception,
        CancellationToken cancellationToken)
    {
        if (exception.EffectMayHaveApplied)
            return await MarkReconciliationRequiredAsync(
                state,
                "buildtest_provider_effect_unknown",
                cancellationToken).ConfigureAwait(false);
        return await SaveOperationAsync(
            state,
            state.Operation with
            {
                Status = SandboxBuildTestOperationStatus.Failed,
                FailureCode = SafeFailureCode(exception.Code)
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<EnvironmentSandboxBuildTestCommandState> SaveOperationAsync(
        EnvironmentSandboxBuildTestCommandState state,
        SandboxBuildTestOperationSnapshot operation,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var updated = operation with
        {
            UpdatedAt = operation.UpdatedAt < now ? now : operation.UpdatedAt
        };
        return await commandStore.SaveAsync(
            updated.ExpectedBinding.Fence.Owner,
            state with { Operation = updated },
            cancellationToken).ConfigureAwait(false);
    }

    private static string SafeFailureCode(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length <= 128 &&
        !code.Any(char.IsControl)
            ? code
            : "buildtest_provider_failure";

    private static SandboxBuildTestObservedBinding CreateObservedBinding(
        SandboxBuildTestExpectedBinding expected,
        SandboxBuildTestCommandNetworkPolicyBinding commandPolicy) =>
        new SandboxBuildTestObservedBinding(
            expected.Fence,
            expected.SandboxLeaseOperationId,
            expected.SandboxResource,
            expected.ProviderFencingGeneration,
            expected.WorkspaceVolume,
            expected.DataGeneration,
            expected.NetworkPolicyGeneration,
            commandPolicy).Validate();

    private async Task<PinnedBuildTestContext> ReadPinnedContextAsync(
        CurrentCallerRequest caller,
        string projectId,
        string runId,
        string environmentId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var (owner, authorization) = await egressManager.GetAuthorizedRunEnvironmentControlAsync(
            caller,
            projectId,
            runId,
            environmentId,
            cancellationToken).ConfigureAwait(false);
        var selection = await egressManager.GetAuthorizedRunSelectionAsync(
            caller,
            owner,
            cancellationToken).ConfigureAwait(false);
        var lifecycle = await lifecycleStore.GetAsync(owner, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "environment_unknown",
                "The exact Environment owner tuple is not registered.");
        await lifecycleStore.RequireActiveAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(lifecycle.Fence, cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "sandbox_lease_unknown",
                "The exact current Sandbox lease is not registered.");
        _ = lease.Validate();
        if (lease.State != SandboxLeaseState.Active ||
            !lease.IsCurrent ||
            lease.ProvisionedResource is not { } provisioned)
            throw new EnvironmentLifecycleException(
                "sandbox_lease_not_active",
                "BuildTest requires the exact current active Sandbox lease.");
        provisioned = provisioned.Validate();
        var intent = lease.ProvisionIntent.Validate();
        var options = Deserialize<AgentSandboxOptions>(intent.OptionsSnapshot, "sandbox_provider_binding_invalid")
            .Validate();
        if (intent.ProviderId != provisioned.Resource.ProviderId ||
            intent.AdapterVersion != provisioned.ProviderBinding.AdapterVersion ||
            intent.OptionsSchemaVersion != provisioned.ProviderBinding.OptionsSchemaVersion ||
            intent.OptionsRevision != provisioned.ProviderBinding.OptionsRevision ||
            !SameJson(intent.OptionsSnapshot, provisioned.ProviderBinding.OptionsSnapshot) ||
            !SameJson(intent.SelectionSnapshot, JsonSerializer.SerializeToElement(selection.RunSelection, JsonOptions)) ||
            !provisioned.NegotiatedCapabilities.Contains(SandboxCapabilities.BuildTestCommandPod) ||
            !string.Equals(options.Namespace, ciliumOptions.Namespace, StringComparison.Ordinal))
            throw new EnvironmentLifecycleException(
                "sandbox_provider_binding_mismatch",
                "The current Sandbox lease does not match its immutable selected provider profile.");

        var provisionRequest = EnvironmentSandboxManager.ReadProvisionRequest(lease);
        var workspace = await lifecycleStore.GetWorkspaceVolumeAsync(
            lifecycle.Fence,
            provisionRequest.VolumeId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new EnvironmentLifecycleException(
                "workspace_volume_unknown",
                "The exact owner-scoped Workspace volume does not exist.");
        ValidateWorkspace(workspace, lifecycle.Fence, provisionRequest, options);
        await egressManager.EnsureAuthorizationUnchangedAsync(
            caller,
            owner,
            authorization,
            requireRunSelection: true,
            cancellationToken).ConfigureAwait(false);
        var expected = new SandboxBuildTestExpectedBinding(
            lifecycle.Fence,
            lease.OperationId,
            provisioned.Resource,
            lease.ProviderFencingGeneration,
            new WorkspaceVolumeReference(
                owner.ProjectId,
                provisionRequest.VolumeId,
                provisionRequest.VolumeResourceGeneration),
            provisionRequest.DataGeneration,
            provisionRequest.NetworkPolicyGeneration)
        {
            SandboxProviderBinding = provisioned.ProviderBinding
        }.Validate();
        return new(
            caller,
            owner,
            authorization,
            selection,
            lifecycle.Fence,
            lease,
            workspace,
            provisionRequest,
            options,
            expected);
    }

    private async Task ConfirmUnchangedAsync(
        CurrentCallerRequest caller,
        PinnedBuildTestContext context,
        CancellationToken cancellationToken)
    {
        await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        await egressManager.EnsureAuthorizationUnchangedAsync(
            caller,
            context.Owner,
            context.Authorization,
            requireRunSelection: true,
            cancellationToken).ConfigureAwait(false);
        var lease = await leaseStore.GetCurrentAsync(context.Fence, cancellationToken).ConfigureAwait(false);
        if (!SameJson(context.Lease, lease))
            throw new EnvironmentLifecycleException(
                "sandbox_binding_stale",
                "The current Sandbox lease changed while BuildTest binding was prepared.");
        var workspace = await lifecycleStore.GetWorkspaceVolumeAsync(
            context.Fence,
            context.ProvisionRequest.VolumeId,
            cancellationToken).ConfigureAwait(false);
        if (!SameJson(context.Workspace, workspace))
            throw new EnvironmentLifecycleException(
                "workspace_generation_mismatch",
                "The current Workspace volume changed while BuildTest binding was prepared.");
        await lifecycleStore.RequireActiveAsync(context.Fence, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateWorkspace(
        EnvironmentWorkspaceVolumeSnapshot workspace,
        EnvironmentGenerationFence fence,
        SandboxProvisionApiRequest provisionRequest,
        AgentSandboxOptions sandboxOptions)
    {
        if (workspace.EnvironmentFence != fence ||
            workspace.VolumeId != provisionRequest.VolumeId ||
            workspace.ResourceGeneration != provisionRequest.VolumeResourceGeneration ||
            workspace.DataGeneration != provisionRequest.DataGeneration ||
            workspace.Phase != EnvironmentWorkspaceVolumeState.Attached ||
            workspace.Resource is null ||
            workspace.ProviderBinding is null ||
            workspace.Resource.ProviderId != sandboxOptions.WorkspaceStorageProviderId)
            throw new EnvironmentLifecycleException(
                "workspace_generation_mismatch",
                "The current Workspace volume is not the exact attached provider resource used by this Sandbox.");
        var specification = Deserialize<WorkspaceVolumeSpec>(
            workspace.Specification,
            "workspace_specification_invalid");
        if (specification.ProjectId != fence.Owner.ProjectId ||
            specification.VolumeId != provisionRequest.VolumeId ||
            !specification.AllowsEnvironment(fence.Owner.ProjectId, fence.Owner.EnvironmentId) ||
            specification.Consistency != WorkspaceVolumeConsistency.Strict)
            throw new EnvironmentLifecycleException(
                "workspace_unsupported",
                "BuildTest requires a strict Workspace volume authorized for this Environment.");
        _ = workspace.ProviderBinding.ValidateFor(workspace.Resource);
    }

    private static T Deserialize<T>(JsonElement value, string errorCode) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions)
                ?? throw new JsonException("The persisted value is empty.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new EnvironmentLifecycleException(errorCode, "A pinned Environment binding is invalid.");
        }
    }

    private static bool SameJson<T>(T left, T? right) =>
        JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(left, JsonOptions),
            JsonSerializer.SerializeToNode(right, JsonOptions));

    private static void ValidateOpaqueIdentifier(string? value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
            throw new ArgumentException("A bounded opaque identifier is required.", name);
    }

    private sealed record PinnedBuildTestContext(
        CurrentCallerRequest Caller,
        EnvironmentOwnerIdentity Owner,
        ProjectAuthorizationContextResponse Authorization,
        EnvironmentEgressManager.AuthorizedSelection Selection,
        EnvironmentGenerationFence Fence,
        SandboxLeaseSnapshot Lease,
        EnvironmentWorkspaceVolumeSnapshot Workspace,
        SandboxProvisionApiRequest ProvisionRequest,
        AgentSandboxOptions Options,
        SandboxBuildTestExpectedBinding ExpectedBinding);

    private sealed record BuildTestPolicyDefinition(
        CiliumNetworkPolicyDocument Resource,
        SandboxBuildTestCommandNetworkPolicyBinding? CommandBinding,
        SandboxBuildTestOutputCollectorNetworkPolicyBinding? CollectorBinding);
}
