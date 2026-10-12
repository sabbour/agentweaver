using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Http;

namespace Agentweaver.Environment;

public sealed record RemoteMcpCurrentCallVerificationOptions(string ActorIssuer)
{
    public RemoteMcpCurrentCallVerificationOptions Validate()
    {
        if (!Uri.TryCreate(ActorIssuer, UriKind.Absolute, out var issuer) ||
            issuer.Scheme != Uri.UriSchemeHttps || issuer.UserInfo.Length != 0 ||
            issuer.Fragment.Length != 0 || issuer.AbsoluteUri != ActorIssuer)
            throw new ArgumentException("A canonical HTTPS actor issuer is required.", nameof(ActorIssuer));
        return this;
    }
}

public sealed class RemoteMcpCurrentCallVerificationService(
    IHttpClientFactory clients,
    [FromKeyedServices(RemoteMcpTrustedServiceTlsComposition.OrchestratorTlsKey)]
    RuntimeServiceTlsOptions orchestratorTlsOptions,
    RemoteMcpCurrentCallVerificationOptions options,
    RemoteMcpCurrentGatewayPolicyReader currentGatewayPolicy,
    TimeProvider timeProvider)
{
    private const int OwnerRequestLimit = 2 * SessionMaterialValidation.MaximumBytes;
    private static readonly TimeSpan OwnerDeadline = TimeSpan.FromSeconds(15);
    private readonly Uri _orchestratorAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(new Uri(
        orchestratorTlsOptions.Validate("Environment", "Orchestrator").PeerOwnerAddress!, UriKind.Absolute));
    private readonly string _actorIssuer = options.Validate().ActorIssuer;
    private readonly HttpClient _orchestratorClient =
        clients.CreateClient(RemoteMcpTrustedServiceTlsComposition.OrchestratorClientName);

    public async Task<RemoteMcpCurrentCallVerificationAcknowledgment> VerifyAsync(
        HttpContext context,
        RemoteMcpCurrentCallVerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        RemoteMcpCurrentCallContract.Validate(request);
        var proof = request.Proof;
        var actor = ReadActor(context, proof);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(OwnerDeadline);
        try
        {
            var registration = await ReadCurrentRegistrationAsync(
                proof.RuntimeInstanceId, actor, deadline.Token).ConfigureAwait(false);
            var verification = await RuntimeOwnerHttpTransport.SendBoundedAsync<RemoteToolResultVerificationReceipt>(
                _orchestratorClient, _orchestratorAddress, "/internal/runtime/remote-tool-results/verify",
                actor, proof, OwnerRequestLimit, allowNotFound: false, deadline.Token).ConfigureAwait(false)
                ?? throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
            var currentCall = RemoteMcpNativeCallContext.CreateVerified(
                context.User, _actorIssuer, proof, registration, verification, actor, timeProvider);
            var policy = await currentGatewayPolicy.VerifyForDispatchAsync(
                currentCall, deadline.Token).ConfigureAwait(false);
            currentCall.RequireUsable();
            var acknowledgment = new RemoteMcpCurrentCallVerificationAcknowledgment(
                1,
                SessionMaterialValidation.RemoteToolResultProofHash(proof),
                RemoteMcpCurrentCallContract.CallBindingHash(request.CallBinding),
                verification,
                proof.SnapshotBinding,
                policy);
            RemoteMcpCurrentCallContract.ValidateAcknowledgment(request, acknowledgment);
            return acknowledgment;
        }
        finally
        {
            actor.Bearer.Invalidate();
        }
    }

    private async Task<RuntimeRegistration> ReadCurrentRegistrationAsync(
        Guid runtimeInstanceId,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        var registration = await RuntimeOwnerHttpTransport.SendBoundedAsync<RuntimeRegistration>(
            _orchestratorClient, _orchestratorAddress,
            $"/internal/runtime/registrations/{runtimeInstanceId:D}", actor, null,
            OwnerRequestLimit, allowNotFound: false, cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
        try
        {
            RuntimeContractValidation.Validate(registration);
        }
        catch (ArgumentException)
        {
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
        }
        if (registration.RuntimeInstanceId != runtimeInstanceId)
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_owner_invalid");
        return registration;
    }

    private RuntimeActorAuthorization ReadActor(HttpContext context, RemoteToolResultProof proof)
    {
        var authorization = context.Request.Headers.Authorization;
        var tenant = context.Request.Headers["X-Agentweaver-Tenant"];
        var subjects = context.User.FindAll("sub").Take(2).ToArray();
        var expirations = context.User.FindAll("exp").Take(2).ToArray();
        if (context.User.Identity?.IsAuthenticated != true ||
            context.Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || header.Parameter.Length > 16_384 ||
            header.Parameter.Any(char.IsWhiteSpace) ||
            subjects.Length != 1 || subjects[0].Value != proof.ActorId ||
            expirations.Length != 1 ||
            !long.TryParse(expirations[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 253402300799 ||
            proof.ActorIssuer != _actorIssuer ||
            tenant.Count != 1 || tenant[0] != proof.TenantId)
            throw new RuntimeAuthorizationException("remote_mcp_current_verification_actor_invalid");
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (expiresAt <= timeProvider.GetUtcNow())
            throw new RuntimeAuthorizationException("remote_mcp_native_actor_expired");
        return new(new SecretCredential(header.Parameter, expiresAt, timeProvider), proof.TenantId);
    }
}

public static class RemoteMcpCurrentCallVerificationEndpoints
{
    public static IEndpointRouteBuilder MapRemoteMcpCurrentCallVerification(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(RemoteMcpCurrentCallContract.VerificationPath, async (
            HttpContext context,
            RemoteMcpCurrentCallVerificationService verifier,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                RemoteMcpTrustedServiceTlsComposition.RequireIdentityPeer(context);
                const int maximumBytes = 2 * SessionMaterialValidation.MaximumBytes;
                if (context.Request.ContentLength > maximumBytes)
                    return Results.Json(new { code = "remote_mcp_current_verification_request_too_large" },
                        statusCode: StatusCodes.Status413PayloadTooLarge);
                if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
                    !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { code = "remote_mcp_current_verification_content_type_invalid" },
                        statusCode: StatusCodes.Status415UnsupportedMediaType);
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = maximumBytes;
                var request = await context.Request.ReadFromJsonAsync<RemoteMcpCurrentCallVerificationRequest>(
                    cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new ArgumentException("A Remote MCP current-call request is required.");
                var acknowledgment = await verifier.VerifyAsync(context, request, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Ok(acknowledgment);
            }
            catch (RuntimeAuthorizationException failure)
            {
                var status = failure.Code is "remote_mcp_forced_network_enforcement_unavailable" or
                    "remote_mcp_current_verification_route_unavailable" or
                    "runtime_owner_unavailable" or "runtime_owner_contract_invalid"
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status403Forbidden;
                return Results.Json(new { code = failure.Code }, statusCode: status);
            }
            catch (ProjectsConfigApiException failure)
            {
                return EnvironmentEndpoints.ToProjectAuthorizationResult(failure);
            }
            catch (EnvironmentLifecycleException failure)
            {
                return Results.Conflict(new { code = failure.Code });
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { code = "remote_mcp_current_verification_invalid" });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { code = "remote_mcp_current_verification_invalid" });
            }
            catch (HttpRequestException)
            {
                return Results.Json(new { code = "remote_mcp_current_verification_owner_unavailable" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Results.Json(new { code = "remote_mcp_current_verification_owner_timeout" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization();
        return endpoints;
    }
}
