using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Requests an Events-owner receipt that the accepted run is admitted for runtime usage.
/// The receipt is validated against the exact authorized selection and a short-lived caller bearer.
/// </summary>
internal sealed class RuntimeRunAdmissionClient(
    HttpClient client, OrchestratorOptions options, TimeProvider timeProvider)
{
    /// <summary>
    /// Reads and validates run admission for the authorized project and run selection.
    /// </summary>
    /// <param name="context">The authenticated request context forwarded to Events.</param>
    /// <param name="selection">The accepted selection and tenant authority for the run.</param>
    /// <param name="cancellationToken">Cancels the owner call.</param>
    /// <returns>The validated runtime run-admission receipt.</returns>
    /// <exception cref="CoordinationException">Thrown when the caller is expired, the owner denies or fails the request, or the receipt does not match the selection.</exception>
    /// <remarks>The bearer credential is invalidated after the request, and the owner address is resolved through the trusted Events configuration.</remarks>
    internal async Task<RuntimeRunAdmissionReceipt> ReadAsync(
        HttpContext context, AuthorizedRunSelection selection, CancellationToken cancellationToken)
    {
        var scope = selection.Selection;
        var owner = EventsAddressedMessageClient.RequireEventsOwner(
            context, new(scope.ProjectId, scope.RunId), options);
        var authentication = await context.AuthenticateAsync().ConfigureAwait(false);
        var expiry = context.User.GetExpirationDate() ?? authentication.Properties?.ExpiresUtc;
        if (!authentication.Succeeded || expiry is null || expiry <= timeProvider.GetUtcNow())
            throw new CoordinationException("runtime_run_admission_unavailable", StatusCodes.Status503ServiceUnavailable);
        var bearer = new Agentweaver.Abstractions.SecretCredential(
            CoordinationIdentity.RequireBearer(context).Parameter!, expiry.Value, timeProvider);
        try
        {
            var request = new RuntimeRunAdmissionRequest(1, RuntimeRunAdmissionContract.SelectionHash(scope.Snapshot));
            var receipt = await RuntimeOwnerHttpTransport.SendAsync<RuntimeRunAdmissionReceipt>(
                client, owner,
                $"/internal/projects/{Uri.EscapeDataString(scope.ProjectId)}/runs/{Uri.EscapeDataString(scope.RunId)}" +
                "/usage/copilot-run-admission",
                new(bearer, selection.Authorization.TenantId), request, cancellationToken).ConfigureAwait(false);
            RuntimeRunAdmissionContract.ValidateReceipt(
                receipt, request, selection.Authorization.TenantId, scope.ProjectId, scope.RunId, scope.Snapshot);
            if (!bearer.IsUsable())
                throw new CoordinationException(
                    "runtime_run_admission_actor_expired", StatusCodes.Status403Forbidden);
            return receipt;
        }
        catch (RuntimeAuthorizationException exception)
        {
            throw new CoordinationException(
                exception.Code == "runtime_owner_denied" ? "runtime_run_admission_denied" : exception.Code,
                exception.Code == "runtime_owner_denied"
                    ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new CoordinationException(
                "runtime_run_admission_unavailable", StatusCodes.Status503ServiceUnavailable, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CoordinationException(
                "runtime_run_admission_timeout", StatusCodes.Status503ServiceUnavailable, exception);
        }
        finally
        {
            bearer.Invalidate();
        }
    }
}
