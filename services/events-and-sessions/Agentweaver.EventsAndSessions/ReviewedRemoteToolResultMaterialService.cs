using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.EventsAndSessions;

/// <summary>Calls the fixed Orchestrator owner to verify a remote-tool call against current runtime authority.</summary>
internal sealed class ReviewedRemoteToolResultOwnerClient(HttpClient client, RuntimeServiceTlsOptions tlsOptions)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(
        new Uri(tlsOptions.Validate("EventsAndSessions", "Orchestrator").PeerOwnerAddress!));

    /// <summary>Reads the exact active registration for the proof's runtime instance.</summary>
    internal async Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var registration = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeRegistration>(
            client, _address, $"/internal/runtime/registrations/{runtimeInstanceId:D}",
            actor, null, SessionMaterialValidation.MaximumBytes, false, token).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_result_registration_invalid");
        RuntimeContractValidation.Validate(registration);
        if (registration.RuntimeInstanceId != runtimeInstanceId || registration.State != RuntimeRegistrationState.Active)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_registration_invalid");
        return registration;
    }

    /// <summary>Reads and verifies the source receipt bound to the registration revision.</summary>
    internal async Task<RuntimeSdkSourceReceipt> ReadSourceAsync(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var source = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeSdkSourceReceipt>(
            client, _address, $"/internal/runtime/sources/{registration.RuntimeInstanceId:D}",
            actor, null, SessionMaterialValidation.MaximumBytes, false, token).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_invalid");
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source.Source);
        if (source.RuntimeInstanceId != registration.RuntimeInstanceId ||
            source.RegistrationRevision != registration.Revision ||
            source.CanonicalPayloadHash != RuntimeUsageSourceReceiptContract.HashSource(registration, source.Source))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_invalid");
        return source;
    }

    /// <summary>Requires the owner to confirm that a saved call proof remains current.</summary>
    internal async Task VerifyAsync(RemoteToolResultProof proof, RuntimeActorAuthorization actor, CancellationToken token)
    {
        var receipt = await RuntimeOwnerHttpTransport.SendBoundedAsync<RemoteToolResultVerificationReceipt>(
            client, _address, "/internal/runtime/remote-tool-results/verify",
            actor, proof, SessionMaterialValidation.MaximumBytes, false, token).ConfigureAwait(false);
        if (receipt is null || receipt.ContractVersion != 1 ||
            receipt.ResultEventId != SessionMaterialValidation.RemoteToolResultEventId(proof) ||
            receipt.ProofSha256 != SessionMaterialValidation.RemoteToolResultProofHash(proof))
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_verification_invalid");
    }
}

/// <summary>
/// Admits private result bytes only after repeated Projects, provider, registration, source, and action-proof checks.
/// </summary>
internal sealed class ReviewedRemoteToolResultMaterialService(
    ReviewedRemoteToolResultOwnerClient runtime, IProjectsAuthorizationContextClient projects,
    SessionMaterialStore material, PostgresSessionsJournal journal, ISessionsProviderBinder bindings,
    TimeProvider timeProvider)
{
    /// <summary>Stores result bytes before the journal reference and verifies the exact call proof on acknowledgment.</summary>
    internal Task<SessionMaterialAcknowledgment> WriteAsync(
        HttpContext context, string sessionId, SessionMaterialWriteRequest request, CancellationToken token)
    {
        SessionMaterialValidation.Validate(request);
        if (request.Kind != SessionMaterialKind.RemoteToolResult)
            throw new ArgumentException("The private result route accepts only remote tool result material.");
        var proof = request.RemoteToolResultProof!;
        return ExecuteAsync(context, sessionId, proof, async (registration, source, validateCurrent) =>
        {
            var acknowledgment = await material.WriteAsync(context.User, sessionId, request, registration, source.Source,
                currentToken => validateCurrent(proof, currentToken), token).ConfigureAwait(false);
            RemoteToolResultReceiptValidation.ValidateAcknowledgment(acknowledgment, proof, request.Bytes);
            RemoteToolResultReceiptValidation.ValidateSource(acknowledgment, source.Source);
            return acknowledgment;
        }, token);
    }

    /// <summary>Reads a committed private result and revalidates the saved proof and current owner bindings.</summary>
    internal Task<SessionMaterialReadResult> ReadAsync(
        HttpContext context, string sessionId, RemoteToolResultProof proof, CancellationToken token)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        return ExecuteAsync(context, sessionId, proof, async (_, source, validateCurrent) =>
        {
            var result = await material.ReadAsync(context.User, sessionId,
                SessionMaterialValidation.RemoteToolResultEventId(proof), SessionMaterialKind.RemoteToolResult,
                async (recorded, currentToken) =>
                {
                    if (recorded.RemoteToolResultProof is not { } recordedProof ||
                        !SessionMaterialValidation.SameRemoteToolResultProof(
                            proof with { ContentClass = recordedProof.ContentClass }, recordedProof))
                        throw new RuntimeAuthorizationException("runtime_remote_tool_result_read_binding_invalid");
                    RemoteToolResultReceiptValidation.ValidateSource(recorded, source.Source);
                    await validateCurrent(recordedProof, currentToken).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            RemoteToolResultReceiptValidation.ValidateRead(result, proof);
            RemoteToolResultReceiptValidation.ValidateSource(result.Material, source.Source);
            return result;
        }, token);
    }

    /// <summary>Runs one protected read/write operation with authority rechecks around storage waits.</summary>
    private async Task<T> ExecuteAsync<T>(
        HttpContext context, string sessionId, RemoteToolResultProof proof,
        Func<RuntimeRegistration, RuntimeSdkSourceReceipt, Func<RemoteToolResultProof, CancellationToken, Task>, Task<T>> action,
        CancellationToken token)
    {
        RuntimeContractValidation.ValidateIdentifier(sessionId);
        if (proof.SessionId != sessionId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_scope_invalid");
        var first = await projects.GetCurrentAsync(context, token).ConfigureAwait(false);
        RequireScope(first, proof);
        var bearer = await NativeUsageApplicationService.BearerAsync(context, timeProvider).ConfigureAwait(false);
        try
        {
            var actor = new RuntimeActorAuthorization(bearer, proof.TenantId);
            var registration = await runtime.ReadCurrentAsync(proof.RuntimeInstanceId, actor, token).ConfigureAwait(false);
            var source = await runtime.ReadSourceAsync(registration, actor, token).ConfigureAwait(false);
            var pinned = await journal.GetProviderBindingAsync(context.User, sessionId, token).ConfigureAwait(false);

            async Task ValidateCurrentAsync(RemoteToolResultProof currentProof, CancellationToken currentToken)
            {
                RequireScope(await projects.GetCurrentAsync(context, currentToken).ConfigureAwait(false), currentProof);
                await bindings.VerifyPinnedAsync(context.User, pinned, currentToken).ConfigureAwait(false);
                var currentRegistration = await runtime.ReadCurrentAsync(
                    proof.RuntimeInstanceId, actor, currentToken).ConfigureAwait(false);
                if (currentRegistration != registration)
                    throw new RuntimeAuthorizationException("runtime_remote_tool_result_registration_changed");
                var currentSource = await runtime.ReadSourceAsync(
                    currentRegistration, actor, currentToken).ConfigureAwait(false);
                if (currentSource.CanonicalPayloadHash != source.CanonicalPayloadHash)
                    throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_changed");
                await runtime.VerifyAsync(currentProof, actor, currentToken).ConfigureAwait(false);
                RequireScope(await projects.GetCurrentAsync(context, currentToken).ConfigureAwait(false), currentProof);
                if (!bearer.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_remote_tool_result_actor_expired");
            }

            await ValidateCurrentAsync(proof, token).ConfigureAwait(false);
            var result = await action(registration, source, ValidateCurrentAsync).ConfigureAwait(false);
            await ValidateCurrentAsync(proof, token).ConfigureAwait(false);
            return result;
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    /// <summary>Requires the current Projects context to match the signed actor and exact proof scope.</summary>
    private static void RequireScope(ProjectsAuthorizationContextResponse current, RemoteToolResultProof proof)
    {
        if (current.ContractVersion != 1 || current.EffectiveAuthority.IsDefault ||
            current.MembershipRevision < 1 || current.Issuer != proof.ActorIssuer ||
            current.ActorId != proof.ActorId || current.TenantId != proof.TenantId ||
            current.BoundProjectId != proof.ProjectId || current.BoundRunId != proof.RunId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_scope_invalid");
        NativeUsageApplicationService.RequireReadAuthority(current);
    }
}
