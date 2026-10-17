using System.Security.Claims;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.EventsAndSessions;

/// <summary>Signals that stored material bytes or their journal reference do not match the recorded integrity contract.</summary>
internal sealed class SessionMaterialIntegrityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Writes bounded producer material to Object Store before its journal reference and verifies reads by digest.</summary>
internal sealed class SessionMaterialStore(PostgresSessionsJournal journal, IObjectStore objects)
{
    /// <summary>Persists bytes and a typed event reference after checking the runtime source and current authority.</summary>
    internal async Task<SessionMaterialAcknowledgment> WriteAsync(
        ClaimsPrincipal principal, string sessionId, SessionMaterialWriteRequest input,
        RuntimeRegistration registration, SdkSessionFacts source,
        Func<CancellationToken, Task> validateCurrentAuthority, CancellationToken cancellationToken)
    {
        SessionMaterialValidation.Validate(input);
        RuntimeContractValidation.Validate(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source);
        var binding = registration.Binding;
        if (input.Kind == SessionMaterialKind.RemoteToolResult)
            ValidateRemoteToolResultBinding(input, registration);
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        var issuers = principal.FindAll("iss").Take(2).ToArray();
        var sourceBindingMatches = input.Kind == SessionMaterialKind.RemoteToolResult
            ? input.SdkVersion is null && input.ModelId is null && input.MaxPromptTokens is null
            : input.SdkVersion == source.SdkVersion && input.ModelId == source.ModelId &&
                input.MaxPromptTokens == source.MaxPromptTokens;
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope) || scope is null ||
            subjects.Length != 1 || subjects[0].Value != binding.ActorId ||
            issuers.Length != 1 || issuers[0].Value != binding.ActorIssuer ||
            scope.Value.ProjectId != binding.ProjectId || scope.Value.RunId != binding.RunId ||
            sessionId != binding.SessionId || input.RuntimeInstanceId != registration.RuntimeInstanceId ||
            input.RegistrationRevision != registration.Revision || input.ExecutionFence != binding.ExecutionFence ||
            !sourceBindingMatches)
            throw new RuntimeAuthorizationException("runtime_material_binding_invalid");
        var identity = scope.Value.ForSession(sessionId);
        var bytes = input.Bytes.ToArray();
        var digest = RuntimeContractValidation.Hash(bytes);
        // A deterministic key makes an identical retry inspect the existing object rather than replace it.
        var keyScope = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"{binding.TenantId}\0{identity.ProjectId}\0{identity.RunId}\0{identity.SessionId}"));
        var reference = new SessionObjectReference(
            new ObjectKey($"session-material/v1/{keyScope}/{input.EventId:D}/{digest}"),
            SessionMaterialValidation.Purpose(input.Kind), bytes.Length)
        {
            Material = new(1, input.Kind, binding.TenantId, digest, registration.RuntimeInstanceId,
                registration.Revision, binding.ExecutionFence, binding.AcceptedSelectionHash,
                source.SdkVersion, source.RuntimeVersion, source.ModelSelectionReference, source.ModelId)
            {
                MaxPromptTokens = source.MaxPromptTokens,
                RemoteToolResultProof = input.RemoteToolResultProof
            }
        };
        SessionMaterialValidation.Validate(reference);
        SessionEventPayload payload = input.Kind switch
        {
            SessionMaterialKind.TurnContent => new TurnSessionPayload(input.Role!, reference),
            SessionMaterialKind.SdkCache =>
                new CacheReferenceSessionPayload(reference, source.RuntimeVersion, source.ModelSelectionReference),
            SessionMaterialKind.RemoteToolResult =>
                CreateRemoteToolResultPayload(input.RemoteToolResultProof!, reference),
            SessionMaterialKind.RuntimeConfiguration => new RuntimeConfigurationSessionPayload(reference),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };
        await validateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        var result = await journal.AppendMaterialAsync(principal, sessionId,
            new(input.EventId, SessionsContractVersions.CurrentSchemaVersion,
                SessionsContractVersions.CurrentEventVersion, payload),
            async token =>
            {
                await validateCurrentAuthority(token).ConfigureAwait(false);
                using var existing = await objects.ReadAsync(reference.Key, token).ConfigureAwait(false);
                if (existing is not null)
                    _ = await ReadVerifiedBytesAsync(existing, reference, token).ConfigureAwait(false);
                else
                {
                    using var content = new MemoryStream(bytes, writable: false);
                    await objects.WriteAsync(reference.Key, content, token).ConfigureAwait(false);
                }
                await validateCurrentAuthority(token).ConfigureAwait(false);
            }, validateCurrentAuthority, cancellationToken).ConfigureAwait(false);
        await validateCurrentAuthority(cancellationToken).ConfigureAwait(false);
        return Acknowledgment(result.Event, reference);
    }

    /// <summary>Reads only material named by an event in the authorized session and verifies its byte length and hash.</summary>
    internal async Task<SessionMaterialReadResult> ReadAsync(
        ClaimsPrincipal principal, string sessionId, Guid eventId, SessionMaterialKind kind,
        Func<SessionMaterialBinding, CancellationToken, Task> validateReadAuthority,
        CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("A recorded session event and typed material kind are required.");
        var envelope = await journal.ReadMaterialEventAsync(
            principal, sessionId, eventId, cancellationToken).ConfigureAwait(false);
        var reference = envelope.Payload switch
        {
            TurnSessionPayload turn when kind == SessionMaterialKind.TurnContent => turn.Content,
            CacheReferenceSessionPayload cache when kind == SessionMaterialKind.SdkCache => cache.Cache,
            RuntimeConfigurationSessionPayload { Configuration: { } configuration }
                when kind == SessionMaterialKind.RuntimeConfiguration => configuration,
            ToolCallSessionPayload toolCall when kind == SessionMaterialKind.RemoteToolResult =>
                RequireRemoteToolResultReference(envelope, toolCall),
            _ => throw new SessionNotFoundException("The event does not record the requested material kind.")
        };
        if (reference.Material is null)
            throw new SessionNotFoundException("The event does not record producer-owned session material.");
        SessionMaterialValidation.Validate(reference);
        if (reference.Material.Kind != kind ||
            envelope.ObjectReferences is not [var stored] || !SameReference(stored.Reference, reference))
            throw new SessionMaterialIntegrityException("session_material_record_invalid");
        if (kind == SessionMaterialKind.RemoteToolResult &&
            (reference.Material.RemoteToolResultProof is not { } proof ||
                SessionMaterialValidation.RemoteToolResultEventId(proof) != envelope.EventId))
            throw new SessionMaterialIntegrityException("session_remote_tool_result_record_invalid");
        await validateReadAuthority(reference.Material, cancellationToken).ConfigureAwait(false);
        using var download = await objects.ReadAsync(reference.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new SessionMaterialIntegrityException("session_material_object_missing");
        var bytes = await ReadVerifiedBytesAsync(download, reference, cancellationToken).ConfigureAwait(false);
        await validateReadAuthority(reference.Material, cancellationToken).ConfigureAwait(false);
        return new(Acknowledgment(envelope, reference), bytes);
    }

    /// <summary>Compares object references, including any full private tool-result proof.</summary>
    private static bool SameReference(SessionObjectReference first, SessionObjectReference second)
    {
        if (first.Key != second.Key || first.Purpose != second.Purpose || first.ByteLength != second.ByteLength)
            return false;
        if (first.Material is not { } firstMaterial || second.Material is not { } secondMaterial)
            return first.Material is null && second.Material is null;
        var proofMatches = firstMaterial.RemoteToolResultProof is null &&
                secondMaterial.RemoteToolResultProof is null ||
            SessionMaterialValidation.SameRemoteToolResultProof(
                firstMaterial.RemoteToolResultProof, secondMaterial.RemoteToolResultProof);
        return (firstMaterial with { RemoteToolResultProof = secondMaterial.RemoteToolResultProof }) == secondMaterial &&
            proofMatches;
    }

    /// <summary>Requires a completed result event whose call identity and event ID are derived from its proof.</summary>
    private static SessionObjectReference RequireRemoteToolResultReference(
        SessionEventEnvelope envelope, ToolCallSessionPayload payload)
    {
        if (payload.State != "completed" || payload.Arguments is not null ||
            payload.Result?.Material is not { Kind: SessionMaterialKind.RemoteToolResult } material ||
            material.RemoteToolResultProof is not { } proof ||
            payload.CallId != SessionMaterialValidation.RemoteToolResultCallId(proof) ||
            payload.ToolName != SessionMaterialValidation.RemoteToolResultToolName(proof) ||
            SessionMaterialValidation.RemoteToolResultEventId(proof) != envelope.EventId)
            throw new SessionMaterialIntegrityException("session_remote_tool_result_record_invalid");
        return payload.Result;
    }

    /// <summary>Builds the private result event using only proof-derived tool identity and the stored object reference.</summary>
    private static ToolCallSessionPayload CreateRemoteToolResultPayload(
        RemoteToolResultProof proof, SessionObjectReference reference) =>
        new(SessionMaterialValidation.RemoteToolResultCallId(proof),
            SessionMaterialValidation.RemoteToolResultToolName(proof), "completed", null, reference);

    /// <summary>Checks that every proof binding matches the current immutable runtime registration.</summary>
    private static void ValidateRemoteToolResultBinding(
        SessionMaterialWriteRequest input, RuntimeRegistration registration)
    {
        var proof = input.RemoteToolResultProof
            ?? throw new ArgumentException("A remote tool result proof is required.", nameof(input));
        var binding = registration.Binding;
        if (input.EventId != SessionMaterialValidation.RemoteToolResultEventId(proof) ||
            proof.RuntimeInstanceId != registration.RuntimeInstanceId ||
            proof.RegistrationRevision != registration.Revision ||
            proof.ExecutionFence != binding.ExecutionFence ||
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
            proof.Snapshot.ProjectId != binding.ProjectId ||
            proof.Snapshot.AgentId != binding.AgentId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_binding_invalid");
    }

    /// <summary>Builds the acknowledgment from the committed event position and its exact material reference.</summary>
    private static SessionMaterialAcknowledgment Acknowledgment(
        SessionEventEnvelope envelope, SessionObjectReference reference) =>
        new(1, envelope.Identity, envelope.EventId, envelope.Position, reference);

    /// <summary>Reads exactly the recorded size, rejects trailing bytes, and verifies the SHA-256 digest.</summary>
    private static async Task<byte[]> ReadVerifiedBytesAsync(
        ObjectRead download, SessionObjectReference reference, CancellationToken cancellationToken)
    {
        if (download.Length != reference.ByteLength || download.Length is <= 0 or > SessionMaterialValidation.MaximumBytes)
            throw new SessionMaterialIntegrityException("session_material_length_invalid");
        var bytes = new byte[checked((int)download.Length)];
        try
        {
            await download.Content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            throw new SessionMaterialIntegrityException("session_material_length_invalid");
        }
        if (await download.Content.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0 ||
            RuntimeContractValidation.Hash(bytes) != reference.Material!.Sha256)
            throw new SessionMaterialIntegrityException("session_material_digest_invalid");
        return bytes;
    }
}
