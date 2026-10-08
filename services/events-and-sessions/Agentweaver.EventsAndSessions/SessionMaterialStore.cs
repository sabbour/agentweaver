using System.Security.Claims;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.EventsAndSessions;

internal sealed class SessionMaterialIntegrityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class SessionMaterialStore(PostgresSessionsJournal journal, IObjectStore objects)
{
    internal async Task<SessionMaterialAcknowledgment> WriteAsync(
        ClaimsPrincipal principal, string sessionId, SessionMaterialWriteRequest input,
        RuntimeRegistration registration, SdkSessionFacts source,
        Func<CancellationToken, Task> validateCurrentAuthority, CancellationToken cancellationToken)
    {
        SessionMaterialValidation.Validate(input);
        RuntimeContractValidation.Validate(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, source);
        var binding = registration.Binding;
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        var issuers = principal.FindAll("iss").Take(2).ToArray();
        if (!SessionIdentityClaims.TryGetScope(principal, out var scope) || scope is null ||
            subjects.Length != 1 || subjects[0].Value != binding.ActorId ||
            issuers.Length != 1 || issuers[0].Value != binding.ActorIssuer ||
            scope.Value.ProjectId != binding.ProjectId || scope.Value.RunId != binding.RunId ||
            sessionId != binding.SessionId || input.RuntimeInstanceId != registration.RuntimeInstanceId ||
            input.RegistrationRevision != registration.Revision || input.ExecutionFence != binding.ExecutionFence ||
            input.SdkVersion != source.SdkVersion || input.ModelId != source.ModelId)
            throw new RuntimeAuthorizationException("runtime_material_binding_invalid");
        var identity = scope.Value.ForSession(sessionId);
        var bytes = input.Bytes.ToArray();
        var digest = RuntimeContractValidation.Hash(bytes);
        var keyScope = RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(
            $"{binding.TenantId}\0{identity.ProjectId}\0{identity.RunId}\0{identity.SessionId}"));
        var reference = new SessionObjectReference(
            new ObjectKey($"session-material/v1/{keyScope}/{input.EventId:D}/{digest}"),
            SessionMaterialValidation.Purpose(input.Kind), bytes.Length)
        {
            Material = new(1, input.Kind, binding.TenantId, digest, registration.RuntimeInstanceId,
                registration.Revision, binding.ExecutionFence, binding.AcceptedSelectionHash,
                source.SdkVersion, source.RuntimeVersion, source.ModelSelectionReference, source.ModelId)
        };
        SessionMaterialValidation.Validate(reference);
        SessionEventPayload payload = input.Kind == SessionMaterialKind.TurnContent
            ? new TurnSessionPayload(input.Role!, reference)
            : new CacheReferenceSessionPayload(reference, source.RuntimeVersion, source.ModelSelectionReference);
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
            _ => throw new SessionNotFoundException("The event does not record the requested material kind.")
        };
        if (reference.Material is null)
            throw new SessionNotFoundException("The event does not record producer-owned session material.");
        SessionMaterialValidation.Validate(reference);
        if (reference.Material.Kind != kind ||
            envelope.ObjectReferences is not [var stored] || stored.Reference != reference)
            throw new SessionMaterialIntegrityException("session_material_record_invalid");
        await validateReadAuthority(reference.Material, cancellationToken).ConfigureAwait(false);
        using var download = await objects.ReadAsync(reference.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new SessionMaterialIntegrityException("session_material_object_missing");
        var bytes = await ReadVerifiedBytesAsync(download, reference, cancellationToken).ConfigureAwait(false);
        await validateReadAuthority(reference.Material, cancellationToken).ConfigureAwait(false);
        return new(Acknowledgment(envelope, reference), bytes);
    }

    private static SessionMaterialAcknowledgment Acknowledgment(
        SessionEventEnvelope envelope, SessionObjectReference reference) =>
        new(1, envelope.Identity, envelope.EventId, envelope.Position, reference);

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
