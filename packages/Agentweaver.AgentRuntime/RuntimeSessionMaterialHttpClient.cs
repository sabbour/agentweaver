using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeSessionMaterialHttpClient(
    HttpClient client, Uri eventsAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsAddress);
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);

    internal async Task<SessionMaterialAcknowledgment> WriteTurnContentAsync(
        AuthorizedRuntimeSession session, Guid eventId, string role, string content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Utf8.GetByteCount(content) > SessionMaterialValidation.MaximumBytes)
            throw new ArgumentException("The actual turn content exceeds the bounded material contract.");
        return await WriteAsync(session, eventId, SessionMaterialKind.TurnContent,
            Utf8.GetBytes(content), role, cancellationToken).ConfigureAwait(false);
    }

    internal Task<SessionMaterialAcknowledgment> WriteCacheAsync(
        AuthorizedRuntimeSession session, Guid eventId, byte[] nativeFiles, CancellationToken cancellationToken) =>
        WriteAsync(session, eventId, SessionMaterialKind.SdkCache, nativeFiles, null, cancellationToken);

    private async Task<SessionMaterialAcknowledgment> WriteAsync(
        AuthorizedRuntimeSession session, Guid eventId, SessionMaterialKind kind, byte[] bytes,
        string? role, CancellationToken cancellationToken)
    {
        var registration = session.Registration;
        var request = new SessionMaterialWriteRequest(1, eventId, registration.RuntimeInstanceId,
            registration.Revision, registration.Binding.ExecutionFence, kind,
            bytes, role, session.Facts.SdkVersion, session.Facts.ModelId)
        {
            MaxPromptTokens = session.Facts.MaxPromptTokens
        };
        SessionMaterialValidation.Validate(request);
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<SessionMaterialAcknowledgment>(
            client, _address, $"/internal/sessions/{Uri.EscapeDataString(registration.Binding.SessionId)}/material",
            actor, request, cancellationToken).ConfigureAwait(false);
        Validate(receipt);
        var material = receipt.Reference.Material!;
        if (receipt.Identity != new SessionIdentity(registration.Binding.ProjectId, registration.Binding.RunId,
                registration.Binding.SessionId) || receipt.EventId != eventId ||
            receipt.Reference.ByteLength != request.Bytes.Length || material.Kind != request.Kind ||
            material.RuntimeInstanceId != registration.RuntimeInstanceId ||
            material.RegistrationRevision != registration.Revision ||
            material.ExecutionFence != registration.Binding.ExecutionFence ||
            material.AcceptedSelectionHash != registration.Binding.AcceptedSelectionHash ||
            material.TenantId != registration.Binding.TenantId ||
            material.Sha256 != RuntimeContractValidation.Hash(request.Bytes) ||
            material.SdkVersion != session.Facts.SdkVersion ||
            material.RuntimeVersion != session.Facts.RuntimeVersion ||
            material.ModelId != session.Facts.ModelId ||
            material.ModelSelectionReference != session.Facts.ModelSelectionReference ||
            material.MaxPromptTokens != session.Facts.MaxPromptTokens)
            throw new RuntimeAuthorizationException("runtime_material_receipt_invalid");
        return receipt;
    }

    internal async Task<RuntimeSessionRecovery> ReadRecoveryAsync(
        RuntimeRegistration registration, CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
        var turns = new List<(string Role, string Content)>();
        SessionEventEnvelope? latestCache = null;
        long lastPosition = 0;
        long lastTurnPosition = 0;
        long totalContentBytes = 0;
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var path = $"/internal/sessions/{Uri.EscapeDataString(binding.SessionId)}/events?limit=100";
            if (cursor is not null)
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await RuntimeOwnerHttpTransport.SendAsync<SessionEventPage>(
                client, _address, path, actor, null, cancellationToken).ConfigureAwait(false);
            foreach (var recorded in page.Events)
            {
                if (recorded.Identity != identity || recorded.Position <= lastPosition ||
                    recorded.EventId == Guid.Empty)
                    throw new RuntimeAuthorizationException("runtime_journal_page_invalid");
                lastPosition = recorded.Position;
                if (recorded.Payload is TurnSessionPayload turn)
                {
                    if (turn.Role is not ("user" or "assistant"))
                        throw new RuntimeAuthorizationException("runtime_journal_role_invalid");
                    var material = await ReadRecordedAsync(
                        identity, recorded.EventId, SessionMaterialKind.TurnContent, cancellationToken)
                        .ConfigureAwait(false);
                    if (material.Material.Reference != turn.Content ||
                        material.Material.Position != recorded.Position ||
                        material.Material.Reference.Material!.TenantId != binding.TenantId ||
                        (totalContentBytes += material.Bytes.Length) > SessionMaterialValidation.MaximumBytes)
                        throw new RuntimeAuthorizationException("runtime_journal_turn_invalid");
                    turns.Add((turn.Role, Utf8.GetString(material.Bytes)));
                    lastTurnPosition = recorded.Position;
                }
                else if (recorded.Payload is CacheReferenceSessionPayload)
                    latestCache = recorded;
            }
            if (!page.HasMore)
                break;
            if (page.NextCursor is not { Length: > 0 } next || !cursors.Add(next) || page.Events.Length == 0)
                throw new RuntimeAuthorizationException("runtime_journal_cursor_invalid");
            cursor = next;
        }

        SessionMaterialReadResult? cache = null;
        if (latestCache is not null && latestCache.Position > lastTurnPosition)
        {
            cache = await RuntimeOwnerHttpTransport.ReadOptionalAsync<SessionMaterialReadResult>(
                client, _address,
                $"/internal/sessions/{Uri.EscapeDataString(binding.SessionId)}/material/{latestCache.EventId:D}/SdkCache",
                actor, cancellationToken).ConfigureAwait(false);
            if (cache is not null)
            {
                ValidateRead(cache, identity, latestCache.EventId, SessionMaterialKind.SdkCache);
                if (cache.Material.Reference != ((CacheReferenceSessionPayload)latestCache.Payload).Cache ||
                    cache.Material.Position != latestCache.Position)
                    throw new RuntimeAuthorizationException("runtime_journal_cache_invalid");
            }
        }
        return new(identity, binding.TenantId, cache, turns);
    }

    internal async Task<SessionMaterialReadResult?> ReadOptionalTurnAsync(
        SessionIdentity identity, Guid eventId, CancellationToken cancellationToken)
    {
        var result = await RuntimeOwnerHttpTransport.ReadOptionalAsync<SessionMaterialReadResult>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{eventId:D}/TurnContent",
            actor, cancellationToken).ConfigureAwait(false);
        if (result is not null)
            ValidateRead(result, identity, eventId, SessionMaterialKind.TurnContent);
        return result;
    }

    public async Task<SessionMaterialReadResult> ReadRecordedAsync(
        SessionIdentity identity, Guid eventId, SessionMaterialKind kind, CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("A recorded session event and material kind are required.");
        var result = await RuntimeOwnerHttpTransport.SendAsync<SessionMaterialReadResult>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{eventId:D}/{kind}",
            actor, null, cancellationToken).ConfigureAwait(false);
        ValidateRead(result, identity, eventId, kind);
        return result;
    }

    private static void ValidateRead(
        SessionMaterialReadResult result, SessionIdentity identity, Guid eventId, SessionMaterialKind kind)
    {
        Validate(result.Material);
        if (result.Material.Identity != identity || result.Material.EventId != eventId ||
            result.Material.Reference.Material!.Kind != kind ||
            result.Bytes is not { Length: > 0 and <= SessionMaterialValidation.MaximumBytes } ||
            result.Bytes.LongLength != result.Material.Reference.ByteLength ||
            RuntimeContractValidation.Hash(result.Bytes) != result.Material.Reference.Material.Sha256)
            throw new RuntimeAuthorizationException("runtime_material_read_invalid");
    }

    private static void Validate(SessionMaterialAcknowledgment receipt)
    {
        if (receipt.ContractVersion != 1 || receipt.EventId == Guid.Empty || receipt.Position <= 0)
            throw new RuntimeAuthorizationException("runtime_material_receipt_invalid");
        SessionMaterialValidation.Validate(receipt.Reference);
    }
}
