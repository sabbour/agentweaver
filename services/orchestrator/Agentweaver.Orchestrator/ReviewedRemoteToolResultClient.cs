using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

internal sealed class ReviewedRemoteToolResultClient(
    HttpClient client, RuntimeServiceTlsOptions tlsOptions,
    ReviewedRemoteToolResultVerifier verifier, RuntimeUsageSourceOwner sources, TimeProvider timeProvider)
    : IReviewedRemoteToolResultWriter
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(
        new Uri(tlsOptions.Validate("Orchestrator", "EventsAndSessions").PeerOwnerAddress!));

    public async Task<SessionMaterialAcknowledgment> WriteAndAcknowledgeAsync(
        HttpContext context, RemoteToolResultProof proof, ReadOnlyMemory<byte> resultBytes,
        CancellationToken cancellationToken)
    {
        var bytes = resultBytes.ToArray();
        var request = new SessionMaterialWriteRequest(
            1, SessionMaterialValidation.RemoteToolResultEventId(proof), proof.RuntimeInstanceId,
            proof.RegistrationRevision, proof.ExecutionFence, SessionMaterialKind.RemoteToolResult, bytes)
        {
            RemoteToolResultProof = proof
        };
        SessionMaterialValidation.Validate(request);
        return await ExecuteAsync(context, proof, async actor =>
        {
            var acknowledgment = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialAcknowledgment>(
                client, _address, $"/internal/sessions/{Uri.EscapeDataString(proof.SessionId)}/remote-tool-results",
                actor, request, 4 * SessionMaterialValidation.MaximumBytes, false, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_remote_tool_result_acknowledgment_invalid");
            RemoteToolResultReceiptValidation.ValidateAcknowledgment(acknowledgment, proof, bytes);
            return acknowledgment;
        }, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("runtime_remote_tool_result_acknowledgment_invalid");
    }

    public async Task<SessionMaterialReadResult?> ReadOptionalAsync(
        HttpContext context, RemoteToolResultProof proof, CancellationToken cancellationToken)
    {
        SessionMaterialValidation.ValidateRemoteToolResultProof(proof);
        return await ExecuteAsync(context, proof, async actor =>
        {
            var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<SessionMaterialReadResult>(
                client, _address, $"/internal/sessions/{Uri.EscapeDataString(proof.SessionId)}/remote-tool-results/read",
                actor, proof, 4 * SessionMaterialValidation.MaximumBytes, true, cancellationToken)
                .ConfigureAwait(false);
            if (result is not null)
                _ = RemoteToolResultReceiptValidation.ValidateRead(result, proof);
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> ExecuteAsync<T>(
        HttpContext context, RemoteToolResultProof proof, Func<RuntimeActorAuthorization, Task<T?>> send,
        CancellationToken cancellationToken) where T : class
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_remote_tool_result_actor_expired");
        var bearer = new SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider);
        try
        {
            await verifier.VerifyAsync(context, proof, cancellationToken).ConfigureAwait(false);
            var source = await sources.ReadCurrentSourceAsync(context, proof.RuntimeInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (source.RuntimeInstanceId != proof.RuntimeInstanceId ||
                source.RegistrationRevision != proof.RegistrationRevision)
                throw new RuntimeAuthorizationException("runtime_remote_tool_result_source_invalid");
            await verifier.VerifyAsync(context, proof, cancellationToken).ConfigureAwait(false);
            var result = await send(new(bearer, proof.TenantId)).ConfigureAwait(false);
            await verifier.VerifyAsync(context, proof, cancellationToken).ConfigureAwait(false);
            var currentSource = await sources.ReadCurrentSourceAsync(context, proof.RuntimeInstanceId, cancellationToken)
                .ConfigureAwait(false);
            if (currentSource.CanonicalPayloadHash != source.CanonicalPayloadHash || !bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_remote_tool_result_authority_changed");
            if (result is SessionMaterialAcknowledgment acknowledgment)
                RemoteToolResultReceiptValidation.ValidateSource(acknowledgment, source.Source);
            else if (result is SessionMaterialReadResult read)
                RemoteToolResultReceiptValidation.ValidateSource(read.Material, source.Source);
            await verifier.VerifyAsync(context, proof, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
