using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator.Core;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Serializes a reviewed remote-tool call and dispatches it to the environment owner over the configured service channel.
/// The caller identity and pinned snapshot are checked both before and after the owner request.
/// </summary>
internal sealed class ReviewedRemoteToolTransportClient(
    HttpClient client,
    [FromKeyedServices(RemoteMcpCallVerificationComposition.TlsKey)] RuntimeServiceTlsOptions tlsOptions,
    OrchestratorOptions options,
    TimeProvider timeProvider) : IReviewedRemoteToolTransport
{
    private readonly Uri _ownerAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(
        tlsOptions.Validate("Orchestrator", "Environment").PeerOwnerAddress!, UriKind.Absolute));

    /// <summary>
    /// Serializes the canonical arguments as a JSON-RPC tools/call request.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the call is not bound to the supplied snapshot.</exception>
    public byte[] SerializeCall(ReviewedRemoteToolSnapshot snapshot, ReviewedRemoteToolCall call)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(call);
        if (!SameSnapshotReference(snapshot.Reference, call.Snapshot) || snapshot.ToolId != call.ToolId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_snapshot_invalid");
        using var arguments = JsonDocument.Parse(call.CanonicalArgumentsJson);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "tools/call",
            @params = new { name = call.ToolId, arguments = arguments.RootElement }
        });
    }

    /// <summary>
    /// Dispatches a validated native call to the environment owner and verifies the returned result envelope.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when the call, snapshot, actor, or result is invalid or changed.</exception>
    /// <exception cref="CoordinationException">Thrown when the environment owner is unavailable or times out.</exception>
    public async Task<ReviewedRemoteToolTransportResult> SendAsync(
        HttpContext context,
        RemoteToolResultProof proof,
        ReviewedRemoteToolSnapshot snapshot,
        ReadOnlyMemory<byte> serializedCall,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (serializedCall.Length is <= 0 or > SessionMaterialValidation.MaximumBytes)
            throw new RuntimeAuthorizationException("runtime_remote_tool_call_size_invalid");
        var request = new RemoteMcpNativeCallRequest(
            RemoteMcpNativeCallContract.CurrentVersion,
            RemoteMcpCurrentCallContract.CreateRequest(proof),
            serializedCall.ToArray());
        if (!SameSnapshotReference(snapshot.Reference, proof.Snapshot) ||
            ReviewedRemoteToolAgent.SnapshotBinding(snapshot) != proof.SnapshotBinding)
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_snapshot_invalid");
        try
        {
            RemoteMcpNativeCallContract.ValidateRequest(
                request, request.CurrentCall.CallBinding.Method,
                request.CurrentCall.CallBinding.CanonicalTarget, request.RequestBytes);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_remote_tool_call_serialization_invalid");
        }

        var expiry = await RequireActorAsync(context, proof).ConfigureAwait(false);
        var bearerValue = CoordinationIdentity.RequireBearer(context).Parameter!;
        if (bearerValue.Length > 16_384 || bearerValue.Any(char.IsWhiteSpace))
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_actor_invalid");
        var bearer = new SecretCredential(bearerValue, expiry, timeProvider);
        try
        {
            var result = await RuntimeOwnerHttpTransport.SendBoundedAsync<RemoteMcpNativeCallResult>(
                client, _ownerAddress, RemoteMcpNativeCallContract.DispatchPath,
                new(bearer, proof.TenantId), request, RemoteMcpNativeCallContract.MaximumEnvelopeBytes,
                allowNotFound: false, cancellationToken).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("runtime_remote_tool_transport_result_invalid");
            var content = RemoteMcpNativeCallContract.CopyContentBytes(result);
            var ownedResult = result with { ContentBytes = content };
            RemoteMcpNativeCallContract.ValidateResult(request, ownedResult);
            if (await RequireActorAsync(context, proof).ConfigureAwait(false) != expiry ||
                CoordinationIdentity.RequireBearer(context).Parameter != bearerValue || !bearer.IsUsable())
                throw new RuntimeAuthorizationException("runtime_remote_tool_transport_actor_changed");
            return new(content, ownedResult.Proof.ContentClass);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_result_invalid");
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "runtime_remote_tool_transport_owner_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoordinationException(
                "runtime_remote_tool_transport_owner_timeout", StatusCodes.Status503ServiceUnavailable, exception);
        }
        finally
        {
            bearer.Invalidate();
        }
    }

    /// <summary>
    /// Requires the authenticated actor, tenant selector, issuer, and bearer expiry to match the result proof.
    /// </summary>
    /// <exception cref="RuntimeAuthorizationException">Thrown when authentication, subject, issuer, tenant, or expiry is invalid.</exception>
    private async Task<DateTimeOffset> RequireActorAsync(HttpContext context, RemoteToolResultProof proof)
    {
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var principal = authentication.Principal;
        var expiry = principal?.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        var tenant = CoordinationIdentity.ReadTenantSelector(context);
        if (!authentication.Succeeded || principal?.Identity?.IsAuthenticated != true ||
            context.User.Identity?.IsAuthenticated != true ||
            SingleSubject(principal) != proof.ActorId || SingleSubject(context.User) != proof.ActorId ||
            !Guid.TryParseExact(proof.ActorId, "D", out _) || proof.ActorIssuer != options.Issuer ||
            tenant is not null && tenant != proof.TenantId)
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_actor_invalid");
        if (expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("runtime_remote_tool_transport_actor_expired");
        return expiry.Value;
    }

    /// <summary>
    /// Returns a subject only when the principal contains exactly one subject claim.
    /// </summary>
    private static string? SingleSubject(ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").Take(2).ToArray();
        return subjects.Length == 1 ? subjects[0].Value : null;
    }

    /// <summary>
    /// Compares all identity fields in two reviewed snapshot references.
    /// </summary>
    private static bool SameSnapshotReference(
        ReviewedRemoteToolSnapshotReference first, ReviewedRemoteToolSnapshotReference second) =>
        (first.ProjectId, first.SnapshotId, first.SnapshotDigest, first.AgentId, first.NodeId) ==
        (second.ProjectId, second.SnapshotId, second.SnapshotDigest, second.AgentId, second.NodeId);
}
