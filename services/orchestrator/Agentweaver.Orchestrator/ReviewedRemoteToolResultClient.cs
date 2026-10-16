using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Writes or reads reviewed remote-tool results through the Events owner using verified mTLS and runtime authority.
/// Each operation validates the proof and source receipt before and after the owner request.
/// </summary>
internal sealed class ReviewedRemoteToolResultClient(
    HttpClient client, RuntimeServiceTlsOptions tlsOptions,
    ReviewedRemoteToolResultVerifier verifier, RuntimeUsageSourceOwner sources, TimeProvider timeProvider)
    : IReviewedRemoteToolResultWriter
{
    private readonly Uri _address = RuntimeOwnerHttpTransport.RequireOwnerAddress(
        new Uri(tlsOptions.Validate("Orchestrator", "EventsAndSessions").PeerOwnerAddress!));

    /// <summary>
    /// Writes result bytes to Events and verifies the returned material acknowledgment against the proof.
    /// </summary>
    /// <param name="context">The authenticated runtime request context.</param>
    /// <param name="proof">The execution, tool-call, and result binding to persist.</param>
    /// <param name="resultBytes">The verified result content.</param>
    /// <param name="cancellationToken">Cancels owner calls and result validation.</param>
    /// <returns>The acknowledgment for the stored result material.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the proof, current source, owner acknowledgment, or authority changes.</exception>
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

    /// <summary>
    /// Reads an existing reviewed result, returning <see langword="null"/> when Events has no material for the proof.
    /// </summary>
    /// <param name="context">The authenticated runtime request context.</param>
    /// <param name="proof">The proof identifying the exact result material.</param>
    /// <param name="cancellationToken">Cancels owner calls and content validation.</param>
    /// <returns>The validated material and bytes, or <see langword="null"/> when not found.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the proof, material, or current authority is inconsistent.</exception>
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

    /// <summary>
    /// Runs one result operation with a short-lived bearer and repeated proof and SDK-source checks.
    /// </summary>
    /// <typeparam name="T">The result contract returned by the owner.</typeparam>
    /// <param name="context">The authenticated request context.</param>
    /// <param name="proof">The remote-tool execution binding being checked.</param>
    /// <param name="send">The owner operation performed after initial validation.</param>
    /// <param name="cancellationToken">Cancels validation and owner calls.</param>
    /// <returns>The owner result, or <see langword="null"/> when the operation is an optional read with no content.</returns>
    /// <exception cref="RuntimeAuthorizationException">Thrown when authentication expires, proof/source state changes, or returned material is not bound to the source.</exception>
    /// <remarks>The bearer is invalidated in all cases and is not reused after authority checks fail.</remarks>
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
