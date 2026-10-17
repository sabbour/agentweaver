using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Describes one native reviewed remote-tool call received from a runtime.
/// </summary>
internal sealed record ReviewedRemoteToolInvocation(
    Guid RuntimeInstanceId,
    string AgentId,
    string NodeId,
    string NativeCallId,
    string ToolName,
    string ArgumentsJson);

/// <summary>
/// Contains the remote tool's returned bytes and their classified content type.
/// </summary>
internal sealed record ReviewedRemoteToolTransportResult(
    ReadOnlyMemory<byte> Content,
    SessionMaterialResultContentClass ContentClass);

/// <summary>
/// Contains the durable Events references and result payload for a completed remote-tool invocation.
/// </summary>
internal sealed record ReviewedRemoteToolExecutionResult(
    Guid CallEventId,
    Guid ResultEventId,
    string InputHash,
    ReadOnlyMemory<byte> Content,
    SessionMaterialResultContentClass ContentClass);

/// <summary>
/// Serializes a reviewed tool call and sends it to its pinned remote endpoint.
/// </summary>
internal interface IReviewedRemoteToolTransport
{
    /// <summary>
    /// Serializes a call using the reviewed snapshot's wire contract.
    /// </summary>
    byte[] SerializeCall(ReviewedRemoteToolSnapshot snapshot, ReviewedRemoteToolCall call);

    // The receiving host authenticates this original context and repeats current-owner checks.
    // Neither serialized MCP metadata nor the proof alone establishes actor or network authority.
    // Implementations also enforce the snapshot's HTTPS endpoint, transport, and server identity pin.
    /// <summary>
    /// Sends the call while preserving the authenticated request context and rechecking remote authority.
    /// </summary>
    Task<ReviewedRemoteToolTransportResult> SendAsync(
        HttpContext context,
        RemoteToolResultProof proof,
        ReviewedRemoteToolSnapshot snapshot,
        ReadOnlyMemory<byte> serializedCall,
        CancellationToken cancellationToken);
}

/// <summary>
/// Writes a remote-tool result to Events and reads an existing result by its proof.
/// </summary>
internal interface IReviewedRemoteToolResultWriter
{
    // Return only after the result bytes and their Events reference have been durably acknowledged.
    /// <summary>
    /// Persists the result and returns only after its Events reference is durably acknowledged.
    /// </summary>
    Task<SessionMaterialAcknowledgment> WriteAndAcknowledgeAsync(
        HttpContext context,
        RemoteToolResultProof proof,
        ReadOnlyMemory<byte> resultBytes,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads a previously persisted result when one exists.
    /// </summary>
    Task<SessionMaterialReadResult?> ReadOptionalAsync(
        HttpContext context, RemoteToolResultProof proof, CancellationToken cancellationToken);
}

/// <summary>
/// Supplies current runtime registration, accepted selection, and action-admission authority for remote tool calls.
/// </summary>
internal interface IReviewedRemoteToolExecutionAuthority
{
    /// <summary>
    /// Reads a current runtime registration.
    /// </summary>
    Task<RuntimeRegistration> ReadCurrentRegistrationAsync(
        HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads accepted selection and refreshed authorization for a project and run.
    /// </summary>
    Task<AuthorizedRunSelection> ReadAcceptedSelectionWithAuthorityAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Requests runtime action admission for the given call.
    /// </summary>
    Task<RuntimeActionAdmission> AuthorizeAsync(
        HttpContext context, RuntimeActionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Revalidates an earlier runtime action admission against current owner state.
    /// </summary>
    Task<RuntimeActionAdmission> VerifyAsync(
        HttpContext context, RuntimeActionAdmission admission, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts the orchestrator's current owner services to remote-tool execution authority.
/// </summary>
internal sealed class OrchestratorReviewedRemoteToolExecutionAuthority(
    RuntimeRegistrationOwner registrations,
    ProjectsRunSelectionClient projects,
    RuntimeActionOwner actions) : IReviewedRemoteToolExecutionAuthority
{
    /// <summary>
    /// Reads the current runtime registration from the orchestrator owner.
    /// </summary>
    public Task<RuntimeRegistration> ReadCurrentRegistrationAsync(
        HttpContext context, Guid runtimeInstanceId, CancellationToken cancellationToken) =>
        registrations.ReadCurrentAsync(context, runtimeInstanceId, cancellationToken);

    /// <summary>
    /// Reads accepted selection and authorization evidence from Projects.
    /// </summary>
    public Task<AuthorizedRunSelection> ReadAcceptedSelectionWithAuthorityAsync(
        HttpContext context, string projectId, string runId, CancellationToken cancellationToken) =>
        projects.ReadAcceptedSelectionWithAuthorityAsync(context, projectId, runId, cancellationToken);

    /// <summary>
    /// Requests current runtime action admission from the policy owner.
    /// </summary>
    public Task<RuntimeActionAdmission> AuthorizeAsync(
        HttpContext context, RuntimeActionRequest request, CancellationToken cancellationToken) =>
        actions.AuthorizeAsync(context, request, cancellationToken);

    /// <summary>
    /// Revalidates runtime action admission against current owner state.
    /// </summary>
    public Task<RuntimeActionAdmission> VerifyAsync(
        HttpContext context, RuntimeActionAdmission admission, CancellationToken cancellationToken) =>
        actions.VerifyAsync(context, admission, cancellationToken);
}

/// <summary>
/// Executes a selected reviewed remote tool with repeated registration, selection, snapshot, and action checks.
/// Results are persisted and acknowledged before their bytes are returned to the runtime.
/// </summary>
internal sealed class ReviewedRemoteToolAgent(
    IReviewedRemoteToolExecutionAuthority authority,
    IReviewedRemoteToolSnapshotResolver snapshots,
    IReviewedRemoteToolTransport transport,
    IReviewedRemoteToolResultWriter results)
{
    private static readonly JsonSerializerOptions SnapshotReferenceJsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

    /// <summary>
    /// Resolves and invokes one currently selected reviewed tool, or replays its already persisted result.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when invocation binding, snapshot selection, action admission, or result evidence is invalid or stale.</exception>
    /// <remarks>Current authority is checked before transport, after transport, after persistence, and before returning the result.</remarks>
    internal async Task<ReviewedRemoteToolExecutionResult> InvokeAsync(
        HttpContext context,
        ReviewedRemoteToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);

        var registration = await authority.ReadCurrentRegistrationAsync(
            context, invocation.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        RequireInvocationBinding(registration, invocation);
        var binding = registration.Binding;

        var accepted = await authority.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        RequireCurrentSelection(accepted.Selection, binding);
        var snapshot = await ResolveSelectedSnapshotAsync(
            accepted.Selection, invocation, cancellationToken).ConfigureAwait(false);
        RequireDispatchableSnapshot(snapshot);

        var executionBinding = new RemoteToolCallExecutionBinding(
            registration.RuntimeInstanceId,
            registration.Revision,
            binding.ActorIssuer,
            binding.ActorId,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.WorkflowStepId!,
            binding.ProjectRevision,
            binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision,
            binding.ContextRevision,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence);
        var call = ReviewedRemoteToolCall.Create(
            snapshot,
            executionBinding,
            invocation.NativeCallId,
            invocation.AgentId,
            invocation.NodeId,
            invocation.ToolName,
            invocation.ArgumentsJson);
        if (Encoding.UTF8.GetByteCount(call.CanonicalArgumentsJson) >
            SessionMaterialValidation.MaximumRemoteToolArgumentsBytes)
            throw new RuntimeAuthorizationException("runtime_remote_tool_arguments_size_invalid");
        var request = new RuntimeActionRequest(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            binding.ExecutionFence,
            call.EventId,
            call.ActionId,
            call.InputHash)
        {
            IsToolInvocation = true
        };
        RuntimeActionContract.Validate(request);

        var admission = await authority.AuthorizeAsync(
            context, request, cancellationToken).ConfigureAwait(false);
        RequireAdmission(admission, request);
        var verifiedAdmission = await authority.VerifyAsync(
            context, admission, cancellationToken).ConfigureAwait(false);
        RequireAdmission(verifiedAdmission, request);
        var proofTemplate = CreateProof(
            call, snapshot, admission, SessionMaterialResultContentClass.TextOnly);
        SessionMaterialValidation.ValidateRemoteToolResultProof(proofTemplate);

        var saved = await results.ReadOptionalAsync(context, proofTemplate, cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            var recordedProof = RemoteToolResultReceiptValidation.ValidateRead(saved, proofTemplate);
            await RequireCurrentAuthorityAsync(
                context, registration, snapshot, invocation, admission, request, cancellationToken)
                .ConfigureAwait(false);
            return new(call.EventId, saved.Material.EventId, call.InputHash,
                saved.Bytes.ToArray(), recordedProof.ContentClass);
        }

        var serializedCall = transport.SerializeCall(snapshot, call)
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_call_serialization_invalid");
        if (serializedCall.Length is <= 0 or > SessionMaterialValidation.MaximumBytes)
            throw new RuntimeAuthorizationException("runtime_remote_tool_call_size_invalid");
        var outboundBytes = serializedCall.ToArray();
        try
        {
            var verification = RemoteMcpCurrentCallContract.CreateRequest(proofTemplate);
            RemoteMcpCurrentCallContract.ValidateToolCallBytes(
                verification, verification.CallBinding.Method, verification.CallBinding.CanonicalTarget, outboundBytes);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_remote_tool_call_serialization_invalid");
        }

        await RequireCurrentAuthorityAsync(
            context, registration, snapshot, invocation, admission, request, cancellationToken)
            .ConfigureAwait(false);
        var transportResult = await transport.SendAsync(
            context, proofTemplate, snapshot, outboundBytes, cancellationToken).ConfigureAwait(false);
        var resultBytes = transportResult.Content.ToArray();
        if (resultBytes.Length is <= 0 or > SessionMaterialValidation.MaximumBytes ||
            !Enum.IsDefined(transportResult.ContentClass))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_size_invalid");

        await RequireCurrentAuthorityAsync(
            context, registration, snapshot, invocation, admission, request, cancellationToken)
            .ConfigureAwait(false);

        var proof = CreateProof(call, snapshot, admission, transportResult.ContentClass);
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        var acknowledgment = await results.WriteAndAcknowledgeAsync(
            context, proof, resultBytes, cancellationToken).ConfigureAwait(false);
        RemoteToolResultReceiptValidation.ValidateAcknowledgment(acknowledgment, proof, resultBytes);
        await RequireCurrentAuthorityAsync(
            context, registration, snapshot, invocation, admission, request, cancellationToken)
            .ConfigureAwait(false);

        return new(
            call.EventId,
            acknowledgment.EventId,
            call.InputHash,
            resultBytes,
            transportResult.ContentClass);
    }

    /// <summary>
    /// Rechecks the registration, selected snapshot, and action admission against current owner state.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when registration, accepted selection, snapshot, or admission changed.</exception>
    private async Task RequireCurrentAuthorityAsync(
        HttpContext context,
        RuntimeRegistration expectedRegistration,
        ReviewedRemoteToolSnapshot expectedSnapshot,
        ReviewedRemoteToolInvocation invocation,
        RuntimeActionAdmission admission,
        RuntimeActionRequest request,
        CancellationToken cancellationToken)
    {
        var currentRegistration = await authority.ReadCurrentRegistrationAsync(
            context, expectedRegistration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        if (currentRegistration != expectedRegistration ||
            currentRegistration.State != RuntimeRegistrationState.Active)
            throw new RuntimeAuthorizationException("runtime_remote_tool_registration_changed");

        var binding = currentRegistration.Binding;
        var currentSelection = await authority.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        RequireCurrentSelection(currentSelection.Selection, binding);
        var currentSnapshot = await ResolveSelectedSnapshotAsync(
            currentSelection.Selection, invocation, cancellationToken).ConfigureAwait(false);
        if (!SameSnapshot(currentSnapshot, expectedSnapshot))
            throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_changed");
        var currentAdmission = await authority.VerifyAsync(context, admission, cancellationToken).ConfigureAwait(false);
        RequireAdmission(currentAdmission, request);
    }

    /// <summary>
    /// Resolves exactly one snapshot selected for the invocation's project, agent, node, and tool.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the selection is malformed, missing, ambiguous, or unavailable.</exception>
    private async Task<ReviewedRemoteToolSnapshot> ResolveSelectedSnapshotAsync(
        EffectiveRunSelection selection,
        ReviewedRemoteToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!selection.Snapshot.TryGetProperty("projectConfiguration", out var projectConfiguration) ||
            projectConfiguration.ValueKind != JsonValueKind.Object ||
            !projectConfiguration.TryGetProperty("reviewedRemoteToolSnapshots", out var references) ||
            references.ValueKind != JsonValueKind.Array)
            throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_not_selected");

        ReviewedRemoteToolSnapshot? selected = null;
        foreach (var element in references.EnumerateArray())
        {
            ReviewedRemoteToolSnapshotReference reference;
            try
            {
                reference = JsonSerializer.Deserialize<ReviewedRemoteToolSnapshotReference>(
                    element.GetRawText(), SnapshotReferenceJsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_selection_invalid");
            }
            catch (ArgumentException)
            {
                throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_selection_invalid");
            }

            if (reference.ProjectId != selection.ProjectId ||
                reference.AgentId != invocation.AgentId ||
                reference.NodeId != invocation.NodeId)
                continue;

            var snapshot = await snapshots.ResolveAsync(reference, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_unavailable");
            if (!SameReference(snapshot.Reference, reference))
                throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_binding_invalid");
            if (snapshot.ToolId != invocation.ToolName)
                continue;
            if (selected is not null)
                throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_selection_ambiguous");
            selected = snapshot;
        }

        return selected
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_not_selected");
    }

    /// <summary>
    /// Requires the registration and invocation to identify the same active runtime and assigned agent.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the runtime, workflow step, or agent binding is invalid.</exception>
    private static void RequireInvocationBinding(
        RuntimeRegistration registration, ReviewedRemoteToolInvocation invocation)
    {
        var binding = registration.Binding;
        if (registration.State != RuntimeRegistrationState.Active ||
            binding.WorkflowStepId is not { Length: > 0 } ||
            binding.AgentId != invocation.AgentId ||
            registration.RuntimeInstanceId != invocation.RuntimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_binding_invalid");
    }

    /// <summary>
    /// Requires the effective selection revisions and hash to match the runtime binding.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the accepted selection has changed.</exception>
    private static void RequireCurrentSelection(EffectiveRunSelection selection, RuntimeBinding binding)
    {
        if (selection.ProjectId != binding.ProjectId ||
            selection.RunId != binding.RunId ||
            selection.ProjectRevision != binding.ProjectRevision ||
            selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            selection.ContextRevision != binding.ContextRevision ||
            CoordinationOwnerStore.HashSelection(selection).ToLowerInvariant() != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_remote_tool_selection_changed");
    }

    /// <summary>
    /// Requires the snapshot's connection, permission, and endpoints to be safe for dispatch.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the connection is disabled or its action, purpose, or endpoint is invalid.</exception>
    private static void RequireDispatchableSnapshot(ReviewedRemoteToolSnapshot snapshot)
    {
        if (snapshot.ConnectionState != "Enabled" ||
            snapshot.Permission.ActionId is not ("tool.read" or "tool.write") ||
            snapshot.Permission.Purpose != RuntimeActionContract.Purpose ||
            !IsHttpsEndpoint(snapshot.EndpointUri) ||
            snapshot.ResourceUri is { } resourceUri && !IsHttpsEndpoint(resourceUri))
            throw new RuntimeAuthorizationException("runtime_remote_tool_snapshot_not_dispatchable");
    }

    /// <summary>
    /// Checks that an endpoint is an absolute HTTPS URI without user information or a fragment.
    /// </summary>
    private static bool IsHttpsEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0;

    /// <summary>
    /// Requires the action admission to match this call and contain the expected allow receipt.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the admission contract or allow decision differs.</exception>
    private static void RequireAdmission(RuntimeActionAdmission admission, RuntimeActionRequest request)
    {
        if (admission.ContractVersion != 1 ||
            admission.Request != request ||
            admission.GrantId != request.EventId.ToString("N") ||
            admission.GrantRevision != "1" ||
            admission.PolicyReceiptId != request.EventId ||
            admission.Outcome != PolicyEvaluationOutcome.Allow ||
            admission.ReasonCode != PolicyEvaluationReasonCode.Allowed)
            throw new RuntimeAuthorizationException("runtime_remote_tool_action_admission_invalid");
    }

    /// <summary>
    /// Creates a result proof carrying the reviewed snapshot, call, runtime binding, and policy admission.
    /// </summary>
    private static RemoteToolResultProof CreateProof(
        ReviewedRemoteToolCall call,
        ReviewedRemoteToolSnapshot snapshot,
        RuntimeActionAdmission admission,
        SessionMaterialResultContentClass contentClass)
    {
        var binding = call.ExecutionBinding;
        return new(
            1,
            call.EventId,
            call.InputHash,
            call.NativeCallId,
            call.Snapshot,
            SnapshotBinding(snapshot),
            call.ToolId,
            call.CanonicalArgumentsJson,
            call.ArgumentsDigest,
            binding.RuntimeInstanceId,
            binding.RegistrationRevision,
            binding.ActorIssuer,
            binding.ActorId,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.StepId,
            binding.ProjectRevision,
            binding.ProjectConfigurationRevision,
            binding.PlatformRuntimeRevision,
            binding.ContextRevision,
            binding.AcceptedSelectionHash,
            binding.ExecutionFence,
            call.ActionId,
            call.Purpose,
            admission.Request.ContractVersion,
            admission.Request.IsToolInvocation,
            admission.GrantId,
            admission.GrantRevision,
            admission.PolicyReceiptId,
            admission.Outcome,
            admission.ReasonCode,
            contentClass);
    }

    /// <summary>
    /// Projects the reviewed snapshot into the immutable result-proof binding contract.
    /// </summary>
    internal static RemoteToolResultSnapshotBinding SnapshotBinding(ReviewedRemoteToolSnapshot snapshot)
    {
        var pin = snapshot.RegistryServerPin;
        return new(
            snapshot.ConnectionId,
            snapshot.ConnectionRowRevision,
            snapshot.ConnectionConfigurationRevision,
            snapshot.ConnectionDiscoveryRevision,
            snapshot.ConnectionState,
            snapshot.ConfigurationSha256,
            snapshot.EndpointUri,
            snapshot.ResourceUri,
            snapshot.AuthenticationMode,
            snapshot.IdentityBindingReference,
            snapshot.TransportProfile,
            pin?.ServerName,
            pin?.ExactVersion,
            pin?.MetadataSha256,
            snapshot.CatalogRevision,
            snapshot.CatalogDigest,
            snapshot.ToolId,
            snapshot.ToolRevision,
            snapshot.ToolDigest,
            snapshot.ToolSchemaRevision,
            snapshot.ToolSchemaDigest,
            snapshot.Permission.ActionId,
            snapshot.Permission.Purpose,
            snapshot.Permission.Digest);
    }

    /// <summary>
    /// Compares two resolved snapshots by reference and canonical snapshot digest.
    /// </summary>
    private static bool SameSnapshot(
        ReviewedRemoteToolSnapshot first, ReviewedRemoteToolSnapshot second) =>
        SameReference(first.Reference, second.Reference) &&
        first.SnapshotDigest == second.SnapshotDigest;

    /// <summary>
    /// Compares all identity fields in two reviewed snapshot references.
    /// </summary>
    private static bool SameReference(
        ReviewedRemoteToolSnapshotReference first, ReviewedRemoteToolSnapshotReference second) =>
        first.ProjectId == second.ProjectId &&
        first.SnapshotId == second.SnapshotId &&
        first.SnapshotDigest == second.SnapshotDigest &&
        first.AgentId == second.AgentId &&
        first.NodeId == second.NodeId;
}
