using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

internal sealed class ReviewedRemoteToolResultVerifier(
    RuntimeRegistrationOwner registrations,
    ProjectsRunSelectionClient projects,
    IReviewedRemoteToolSnapshotResolver snapshots,
    RuntimeActionOwner actions,
    OrchestratorOptions options)
{
    private static readonly JsonSerializerOptions SelectionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal async Task<RemoteToolResultVerificationReceipt> VerifyAsync(
        HttpContext context, RemoteToolResultProof proof, CancellationToken cancellationToken)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        var registration = await registrations.ReadCurrentAsync(
            context, proof.RuntimeInstanceId, cancellationToken).ConfigureAwait(false);
        var binding = registration.Binding;
        RequireProofBinding(context, proof, registration, options);

        var accepted = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        RequireCurrentSelection(accepted.Selection, binding);
        RequireSelectedSnapshot(accepted.Selection.Snapshot, proof.Snapshot);

        var snapshot = await snapshots.ResolveAsync(proof.Snapshot, cancellationToken).ConfigureAwait(false)
            ?? throw new CoordinationException(
                "reviewed_remote_tool_snapshot_unavailable", StatusCodes.Status409Conflict);
        RequireSnapshotBinding(snapshot, proof);

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
            proof.NativeCallId,
            snapshot.AgentId,
            snapshot.NodeId,
            proof.ToolName,
            proof.CanonicalArgumentsJson);
        RequireExactCall(call, proof);

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
        var admission = new RuntimeActionAdmission(
            1,
            request,
            call.EventId.ToString("N"),
            "1",
            call.EventId,
            PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed);
        await actions.VerifyAsync(context, admission, cancellationToken).ConfigureAwait(false);

        if (await registrations.ReadCurrentAsync(
                context, registration.RuntimeInstanceId, cancellationToken).ConfigureAwait(false) != registration)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_registration_changed");
        var current = await projects.ReadAcceptedSelectionWithAuthorityAsync(
            context, binding.ProjectId, binding.RunId, cancellationToken).ConfigureAwait(false);
        RequireCurrentSelection(current.Selection, binding);
        RequireSelectedSnapshot(current.Selection.Snapshot, proof.Snapshot);
        var currentSnapshot = await snapshots.ResolveAsync(proof.Snapshot, cancellationToken).ConfigureAwait(false);
        if (currentSnapshot is null || currentSnapshot.SnapshotDigest != snapshot.SnapshotDigest)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_snapshot_changed");

        return new(
            1,
            SessionMaterialValidation.RemoteToolResultEventId(proof),
            SessionMaterialValidation.RemoteToolResultProofHash(proof));
    }

    private static void RequireProofBinding(
        HttpContext context,
        RemoteToolResultProof proof,
        RuntimeRegistration registration,
        OrchestratorOptions options)
    {
        var binding = registration.Binding;
        var actor = CoordinationIdentity.RequireActor(context.User, options.Issuer);
        if (binding.WorkflowStepId is not { Length: > 0 } ||
            actor.Issuer != binding.ActorIssuer || actor.Subject != binding.ActorId ||
            proof.RuntimeInstanceId != registration.RuntimeInstanceId ||
            proof.RegistrationRevision != registration.Revision ||
            proof.ActorIssuer != binding.ActorIssuer ||
            proof.ActorId != binding.ActorId ||
            proof.TenantId != binding.TenantId ||
            proof.ProjectId != binding.ProjectId ||
            proof.RunId != binding.RunId ||
            proof.SessionId != binding.SessionId ||
            proof.StepId != binding.WorkflowStepId ||
            proof.ProjectRevision != binding.ProjectRevision ||
            proof.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            proof.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            proof.ContextRevision != binding.ContextRevision ||
            proof.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
            proof.ExecutionFence != binding.ExecutionFence ||
            proof.Snapshot.ProjectId != binding.ProjectId ||
            proof.Snapshot.AgentId != binding.AgentId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_binding_invalid");
    }

    private static void RequireCurrentSelection(EffectiveRunSelection selection, RuntimeBinding binding)
    {
        if (selection.ProjectId != binding.ProjectId ||
            selection.RunId != binding.RunId ||
            selection.ProjectRevision != binding.ProjectRevision ||
            selection.ProjectConfigurationRevision != binding.ProjectConfigurationRevision ||
            selection.PlatformRuntimeRevision != binding.PlatformRuntimeRevision ||
            selection.ContextRevision != binding.ContextRevision ||
            CoordinationOwnerStore.HashSelection(selection).ToLowerInvariant() != binding.AcceptedSelectionHash)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_selection_changed");
    }

    private static void RequireSelectedSnapshot(
        JsonElement selection, ReviewedRemoteToolSnapshotReference expected)
    {
        if (selection.ValueKind != JsonValueKind.Object ||
            !selection.TryGetProperty("projectConfiguration", out var configuration) ||
            configuration.ValueKind != JsonValueKind.Object ||
            !configuration.TryGetProperty("reviewedRemoteToolSnapshots", out var references) ||
            references.ValueKind == JsonValueKind.Null)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_snapshot_not_selected");
        if (references.ValueKind != JsonValueKind.Array)
            throw new CoordinationException(
                "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);

        foreach (var element in references.EnumerateArray())
        {
            ReviewedRemoteToolSnapshotReference? reference;
            try
            {
                reference = JsonSerializer.Deserialize<ReviewedRemoteToolSnapshotReference>(
                    element.GetRawText(), SelectionJsonOptions);
            }
            catch (JsonException)
            {
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
            }
            catch (ArgumentException)
            {
                throw new CoordinationException(
                    "projects_run_selection_contract_invalid", StatusCodes.Status502BadGateway);
            }

            if (reference is not null && SameReference(reference, expected))
                return;
        }
        throw new RuntimeAuthorizationException("runtime_remote_tool_result_snapshot_not_selected");
    }

    private static void RequireSnapshotBinding(ReviewedRemoteToolSnapshot snapshot, RemoteToolResultProof proof)
    {
        if (!SameReference(snapshot.Reference, proof.Snapshot) ||
            SnapshotBinding(snapshot) != proof.SnapshotBinding)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_snapshot_binding_invalid");
    }

    private static RemoteToolResultSnapshotBinding SnapshotBinding(ReviewedRemoteToolSnapshot snapshot)
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

    private static void RequireExactCall(ReviewedRemoteToolCall call, RemoteToolResultProof proof)
    {
        if (!SameReference(call.Snapshot, proof.Snapshot) ||
            call.EventId != proof.CallEventId ||
            call.InputHash != proof.CallInputHash ||
            call.NativeCallId != proof.NativeCallId ||
            call.CanonicalArgumentsJson != proof.CanonicalArgumentsJson ||
            call.ArgumentsDigest != proof.ArgumentsDigest ||
            call.ToolId != proof.ToolName ||
            call.ActionId != proof.ActionId ||
            call.Purpose != proof.Purpose ||
            call.EventId != proof.PolicyReceiptId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_call_invalid");
    }

    private static bool SameReference(
        ReviewedRemoteToolSnapshotReference first, ReviewedRemoteToolSnapshotReference second) =>
        first.ProjectId == second.ProjectId &&
        first.SnapshotId == second.SnapshotId &&
        first.SnapshotDigest == second.SnapshotDigest &&
        first.AgentId == second.AgentId &&
        first.NodeId == second.NodeId;
}

public static class ReviewedRemoteToolResultEndpoints
{
    public static IEndpointRouteBuilder MapReviewedRemoteToolResultEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/internal/runtime/remote-tool-results/verify",
            (HttpContext context) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var error = context.Request.IsHttps
                    ? "runtime_remote_tool_result_route_unavailable"
                    : "runtime_remote_tool_result_https_required";
                return Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(
        HttpContext context, Func<Task<RemoteToolResultVerificationReceipt>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return Results.Ok(await action().ConfigureAwait(false));
        }
        catch (CoordinationException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: failure.StatusCode);
        }
        catch (RuntimeAuthorizationException failure)
        {
            return Results.Json(new { error = failure.Code }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "runtime_remote_tool_result_invalid" });
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_owner_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.Json(new { error = "runtime_remote_tool_result_owner_timeout" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
