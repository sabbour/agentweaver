using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeSessionMaterialHttpClient(
    HttpClient client, Uri eventsAddress, RuntimeActorAuthorization actor)
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(eventsAddress);
    private static readonly UTF8Encoding Utf8 = new(false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JournalJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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

    internal Task<SessionMaterialAcknowledgment> WriteRuntimeConfigurationAsync(
        AuthorizedRuntimeSession session, Guid eventId, byte[] configuration, CancellationToken cancellationToken) =>
        WriteAsync(session, eventId, SessionMaterialKind.RuntimeConfiguration, configuration, null, cancellationToken);

    internal Task<SessionMaterialAcknowledgment> WriteRemoteToolResultAsync(
        RemoteToolResultProof proof, ReadOnlyMemory<byte> resultBytes, CancellationToken cancellationToken) =>
        Task.FromException<SessionMaterialAcknowledgment>(
            new RuntimeAuthorizationException("runtime_remote_tool_result_route_unavailable"));

    internal Task<SessionMaterialReadResult> ReadRemoteToolResultAsync(
        RemoteToolResultProof proof, CancellationToken cancellationToken) =>
        Task.FromException<SessionMaterialReadResult>(
            new RuntimeAuthorizationException("runtime_remote_tool_result_route_unavailable"));

    private async Task<SessionMaterialAcknowledgment> WriteAsync(
        AuthorizedRuntimeSession session, Guid eventId, SessionMaterialKind kind, byte[] bytes,
        string? role, CancellationToken cancellationToken)
    {
        if (kind == SessionMaterialKind.RemoteToolResult)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_route_unavailable");
        var registration = session.Registration;
        var request = new SessionMaterialWriteRequest(1, eventId, registration.RuntimeInstanceId,
            registration.Revision, registration.Binding.ExecutionFence, kind,
            bytes, role, session.Facts.SdkVersion, session.Facts.ModelId)
        {
            MaxPromptTokens = session.Facts.MaxPromptTokens
        };
        SessionMaterialValidation.Validate(request);
        if (kind == SessionMaterialKind.RuntimeConfiguration)
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new RuntimeAuthorizationException("runtime_configuration_invalid");
        }
        var route = kind == SessionMaterialKind.RuntimeConfiguration
            ? "runtime-configuration"
            : "material";
        var receipt = await RuntimeOwnerHttpTransport.SendAsync<SessionMaterialAcknowledgment>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(registration.Binding.SessionId)}/{route}",
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
            material.MaxPromptTokens != session.Facts.MaxPromptTokens ||
            material.RemoteToolResultProof is not null)
            throw new RuntimeAuthorizationException("runtime_material_receipt_invalid");
        return receipt;
    }

    internal async Task<RuntimeSessionRecovery> ReadRecoveryAsync(
        RuntimeRegistration registration,
        CancellationToken cancellationToken,
        SessionMaterialAcknowledgment? expectedCacheAcknowledgment = null)
    {
        var binding = registration.Binding;
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
        if (expectedCacheAcknowledgment is { } expectedCache)
        {
            SessionMaterialValidation.Validate(expectedCache.Reference);
            if (expectedCache.ContractVersion != 1 || expectedCache.Identity != identity ||
                expectedCache.EventId == Guid.Empty ||
                expectedCache.Reference.Material!.Kind != SessionMaterialKind.SdkCache)
                throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
            var recordedCache = await ReadRecordedAsync(
                identity, expectedCache.EventId, SessionMaterialKind.SdkCache, cancellationToken)
                .ConfigureAwait(false);
            if (!RuntimeWorkspaceResumeHttpClient.SameJson(recordedCache.Material, expectedCache))
                throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
            return new(identity, binding.TenantId, recordedCache, Array.Empty<(string Role, string Content)>());
        }
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

    internal async Task<RuntimeSessionRecovery> ReadResumeCacheAsync(
        RuntimeRegistration registration,
        RuntimeResumeBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (registration != request.Registration)
            throw new RuntimeAuthorizationException("runtime_resume_registration_stale");
        var binding = registration.Binding;
        var identity = request.Resume.SourceManifest.Identity;
        var expected = request.Resume.SourceManifest.CacheAcknowledgment
            ?? throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialReadResult>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{expected.EventId:D}/SdkCache",
            actor, null, 2 * SessionMaterialValidation.MaximumBytes, allowNotFound: true, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return new(identity, binding.TenantId, null, Array.Empty<(string Role, string Content)>());

        ValidateRead(result, identity, expected.EventId, SessionMaterialKind.SdkCache);
        if (!RuntimeWorkspaceResumeHttpClient.SameJson(result.Material, expected))
            throw new RuntimeAuthorizationException("runtime_resume_cache_receipt_invalid");
        return new(identity, binding.TenantId, result, Array.Empty<(string Role, string Content)>());
    }

    internal async Task<RuntimeSessionRecovery> ReadJournalRebuildAsync(
        RuntimeRegistration registration,
        RuntimeResumeBootstrapRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (registration != request.Registration)
            throw new RuntimeAuthorizationException("runtime_resume_registration_stale");
        var binding = registration.Binding;
        var source = request.Resume.SourceManifest;
        var identity = source.Identity;
        var sourceCacheAcknowledgment = source.CacheAcknowledgment
            ?? throw new RuntimeAuthorizationException("runtime_resume_journal_source_missing");
        SessionMaterialValidation.Validate(sourceCacheAcknowledgment.Reference);
        var throughPosition = source.FlushedJournalPosition
            ?? throw new RuntimeAuthorizationException("runtime_resume_journal_source_missing");
        if (binding.ProjectId != identity.ProjectId || binding.RunId != identity.RunId ||
            binding.SessionId != identity.SessionId)
            throw new RuntimeAuthorizationException("runtime_resume_journal_scope_invalid");

        var prefix = ImmutableArray.CreateBuilder<SessionEventEnvelope>();
        var turns = new List<(string Role, string Content)>();
        var eventIds = new HashSet<Guid>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var totalPrefixBytes = 2L;
        long totalContentBytes = 0;
        long lastPosition = 0;
        string? cursor = null;
        var reachedBoundary = false;
        while (!reachedBoundary)
        {
            var path = $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/events?limit=100";
            if (cursor is not null)
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            var page = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionEventPage>(
                client, _address, path, actor, null,
                4 * SessionMaterialValidation.MaximumBytes, allowNotFound: false, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_journal_page_invalid");
            if (page.Events.IsDefault)
                throw new RuntimeAuthorizationException("runtime_journal_page_invalid");

            foreach (var recorded in page.Events)
            {
                if (recorded is null || recorded.Identity.ProjectId != identity.ProjectId ||
                    recorded.Identity.RunId != identity.RunId || recorded.EventId == Guid.Empty ||
                    recorded.Position <= lastPosition)
                    throw new RuntimeAuthorizationException("runtime_journal_page_invalid");
                lastPosition = recorded.Position;
                if (recorded.Position > throughPosition)
                {
                    reachedBoundary = true;
                    break;
                }
                if (!eventIds.Add(recorded.EventId))
                    throw new RuntimeAuthorizationException("runtime_journal_page_invalid");

                var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(recorded, JournalJson).Length;
                totalPrefixBytes += envelopeBytes + (prefix.Count == 0 ? 0 : 1);
                if (totalPrefixBytes > SessionMaterialValidation.MaximumBytes)
                    throw new RuntimeAuthorizationException("runtime_resume_journal_prefix_too_large");
                prefix.Add(recorded);

                if (recorded.Payload is not TurnSessionPayload turn)
                    continue;
                if (turn.Role is not ("user" or "assistant"))
                    throw new RuntimeAuthorizationException("runtime_journal_role_invalid");
                var material = await ReadRecordedAsync(
                    recorded.Identity, recorded.EventId, SessionMaterialKind.TurnContent, cancellationToken)
                    .ConfigureAwait(false);
                var turnBinding = material.Material.Reference.Material!;
                if (recorded.ObjectReferences is not [{ Reference: var storedReference }] ||
                    !RuntimeWorkspaceResumeHttpClient.SameJson(storedReference, turn.Content) ||
                    !RuntimeWorkspaceResumeHttpClient.SameJson(storedReference, material.Material.Reference) ||
                    !RuntimeWorkspaceResumeHttpClient.SameJson(material.Material.Reference, turn.Content) ||
                    material.Material.Position != recorded.Position ||
                    turnBinding.TenantId != binding.TenantId ||
                    material.Bytes.Length > SessionMaterialValidation.MaximumBytes - totalContentBytes)
                    throw new RuntimeAuthorizationException("runtime_journal_turn_invalid");
                totalContentBytes += material.Bytes.Length;
                try
                {
                    turns.Add((turn.Role, Utf8.GetString(material.Bytes)));
                }
                catch (DecoderFallbackException)
                {
                    throw new RuntimeAuthorizationException("runtime_journal_turn_invalid");
                }
            }

            if (reachedBoundary || !page.HasMore)
                break;
            if (page.NextCursor is not { Length: > 0 } next ||
                !cursors.Add(next) || page.Events.Length == 0)
                throw new RuntimeAuthorizationException("runtime_journal_cursor_invalid");
            cursor = next;
        }

        var immutablePrefix = prefix.ToImmutable();
        var checkpoint = source.Checkpoint
            ?? throw new RuntimeAuthorizationException("runtime_resume_journal_source_missing");
        var receipt = new RuntimeResumeJournalRebuildReceipt(
            checkpoint,
            throughPosition,
            RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, immutablePrefix),
            request.ResumeAttemptId,
            RuntimeResumeNativeCacheState.PendingUntilAuthorizedTurn).ValidateFor(request);
        return new RuntimeSessionRecovery(identity, binding.TenantId, null, turns)
        {
            JournalRebuild = receipt
        };
    }

    internal async Task<SessionMaterialReadResult?> ReadOptionalTurnAsync(
        RuntimeRegistration registration, SdkSessionFacts facts, Guid eventId,
        CancellationToken cancellationToken)
    {
        var binding = registration.Binding;
        var identity = new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId);
        var result = await RuntimeOwnerHttpTransport.ReadOptionalAsync<SessionMaterialReadResult>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{eventId:D}/TurnContent",
            actor, cancellationToken).ConfigureAwait(false);
        if (result is not null)
        {
            ValidateRead(result, identity, eventId, SessionMaterialKind.TurnContent);
            var material = result.Material.Reference.Material!;
            if (material.RuntimeInstanceId != registration.RuntimeInstanceId ||
                material.RegistrationRevision != registration.Revision ||
                material.ExecutionFence != binding.ExecutionFence ||
                material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                material.TenantId != binding.TenantId ||
                material.SdkVersion != facts.SdkVersion ||
                material.RuntimeVersion != facts.RuntimeVersion ||
                material.ModelSelectionReference != facts.ModelSelectionReference ||
                material.ModelId != facts.ModelId)
                throw new RuntimeAuthorizationException("runtime_material_read_binding_invalid");
        }
        return result;
    }

    public async Task<SessionMaterialReadResult> ReadRecordedAsync(
        SessionIdentity identity, Guid eventId, SessionMaterialKind kind, CancellationToken cancellationToken)
    {
        if (eventId == Guid.Empty || !Enum.IsDefined(kind))
            throw new ArgumentException("A recorded session event and material kind are required.");
        if (kind == SessionMaterialKind.RemoteToolResult)
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_route_unavailable");
        if (kind == SessionMaterialKind.RuntimeConfiguration)
            throw new RuntimeAuthorizationException("runtime_configuration_route_unavailable");
        var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialReadResult>(
            client, _address,
            $"/internal/sessions/{Uri.EscapeDataString(identity.SessionId)}/material/{eventId:D}/{kind}",
            actor, null, 2 * SessionMaterialValidation.MaximumBytes,
            allowNotFound: false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_material_read_invalid");
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
