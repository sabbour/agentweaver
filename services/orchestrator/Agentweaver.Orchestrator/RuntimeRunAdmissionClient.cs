using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;

namespace Agentweaver.Orchestrator;

internal sealed class RuntimeRunAdmissionClient(
    HttpClient client, OrchestratorOptions options, TimeProvider timeProvider)
{
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
