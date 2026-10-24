using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Orchestrator;

/// <summary>Requests one generation turn in an already accepted project run.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecuteCastingGenerationRequest(Guid RequestId, string Prompt)
{
    /// <summary>Checks the stable request identity and bounded model prompt.</summary>
    public ExecuteCastingGenerationRequest Validate()
    {
        if (RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(Prompt) ||
            Prompt.Length > AddressedMessageValidation.MaximumTextLength ||
            Prompt.Contains('\0') ||
            Prompt.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new ArgumentException("The casting generation request is invalid.");
        return this;
    }
}

/// <summary>Returns the result from the AgentHost turn bound to one request and run.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecuteCastingGenerationResponse(Guid RequestId, string RunId, string Output);

/// <summary>Builds and validates the AgentHost message used by accepted-run casting generation.</summary>
internal static class CastingGenerationExecutionContract
{
    internal static void RequireProjectRunStart(
        CoordinationOwnerStore.ProjectRunRequestRow runRequest,
        string runId,
        string sessionId,
        string prompt)
    {
        // Bind first-time acceptance to the exact durable request, not just a caller-selected run ID.
        if (runRequest.RunId != runId ||
            runRequest.RootSessionId != sessionId ||
            !string.Equals(runRequest.FirstMessage, prompt, StringComparison.Ordinal))
            throw new CoordinationException(
                "casting_generation_run_conflict", StatusCodes.Status409Conflict);
    }

    internal static RuntimeA2ASendRequest CreateHostRequest(
        ExecuteCastingGenerationRequest request,
        RuntimeRegistration registration,
        RuntimeHostReadinessReceipt readiness,
        TimeProvider timeProvider)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(readiness);
        if (registration.Binding.WorkflowStepId is not null)
            throw new CoordinationException(
                "casting_generation_root_registration_invalid", StatusCodes.Status409Conflict);

        CoordinationEndpoints.ValidateHostReadiness(readiness, registration, timeProvider);
        var proof = new RuntimeHostSessionProof(
            1,
            registration,
            readiness.SourceGrant.GrantId,
            readiness.SourceGrant.Revision,
            RuntimeCredentialPurpose.Observe);
        // AgentHost uses this stable message ID to replay exact retries instead of starting another turn.
        var message = new RuntimeA2AMessage(
            "message",
            request.RequestId,
            RuntimeContractValidation.NativeSessionId(registration.Binding),
            "user",
            [new("text", request.Prompt)],
            new(
                proof,
                AddressedMessageDeliveryMode.Immediate,
                ContinueAfterCallerDisconnect: true,
                // The generated result is a proposal; this turn may inspect files but cannot modify them.
                ReadOnlyToolsOnly: true));
        return new(message);
    }

    internal static string ReadOutput(RuntimeA2AResponse response, string expectedContextId)
    {
        if (response is null ||
            response.Kind != "message" ||
            response.MessageId == Guid.Empty ||
            response.ContextId != expectedContextId ||
            response.Role != "agent" ||
            response.Parts is not [{ Kind: "text", Text: { Length: > 0 } output }] ||
            output.Length > AddressedMessageValidation.MaximumTextLength ||
            output.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new CoordinationException(
                "casting_generation_host_response_invalid", StatusCodes.Status503ServiceUnavailable);
        return output;
    }

    internal static void RequireAcceptedRoot(
        string projectId,
        string runId,
        string sessionId,
        SessionTreeSnapshot tree,
        SessionStatusSnapshot status)
    {
        var identity = new SessionIdentity(projectId, runId, sessionId);
        // A generation turn belongs to the accepted coordinator root, not a workflow child.
        var root = tree.Nodes.SingleOrDefault(node => node.Identity == identity);
        if (tree.RootSessionId != sessionId ||
            root is null ||
            root.ParentSessionId is not null ||
            root.Kind != CoordinationSessionKind.Coordinator ||
            root.Lifecycle != CoordinationLifecycleState.Active ||
            status.Identity != identity ||
            status.RootSessionId != sessionId ||
            status.Kind != CoordinationSessionKind.Coordinator ||
            status.Lifecycle != CoordinationLifecycleState.Active ||
            status.ExecutionFence != root.ExecutionFence ||
            status.RunExecution.State is "completed" or "failed" or "indeterminate" or "cancelled")
            throw new CoordinationException(
                "casting_generation_run_unavailable", StatusCodes.Status409Conflict);
    }

    internal static void RequireCurrentRegistration(
        RuntimeRegistration registration,
        string projectId,
        string runId,
        string sessionId,
        long executionFence,
        string acceptedSelectionHash,
        TimeProvider timeProvider)
    {
        RuntimeContractValidation.Validate(registration);
        var binding = registration.Binding;
        // The execution fence and selection hash keep this host registration tied to the accepted run.
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() ||
            binding.ProjectId != projectId ||
            binding.RunId != runId ||
            binding.SessionId != sessionId ||
            binding.ExecutionFence != executionFence ||
            binding.AcceptedSelectionHash != acceptedSelectionHash.ToLowerInvariant() ||
            binding.WorkflowStepId is not null)
            throw new CoordinationException(
                "casting_generation_registration_stale", StatusCodes.Status409Conflict);
    }
}
