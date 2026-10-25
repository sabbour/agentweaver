using System.Net;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Asks the Environment owner to verify current egress without applying or mutating policy.</summary>
public sealed class RuntimeEgressVerificationHttpClient(HttpClient client, Uri environmentOwnerAddress)
{
    private readonly Uri _owner = RuntimeOwnerHttpTransport.RequireOwnerAddress(environmentOwnerAddress);

    /// <summary>Forwards the caller's existing authorization and accepts only a matching fence-bound receipt.</summary>
    public async Task<RuntimeEgressVerificationReceipt> VerifyAsync(
        RuntimeEgressVerificationRequest request,
        RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(actor);
        try
        {
            if (!actor.Bearer.IsUsable())
                throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.AuthorizationChanged);
            // The owner response includes typed failures on non-success HTTP statuses so callers can distinguish
            // missing, stale, unauthorized, unverified, and unavailable evidence without treating any as ready.
            var (status, response) = await RuntimeOwnerHttpTransport.SendBoundedOutcomeAsync<
                RuntimeEgressVerificationResponse>(
                client,
                _owner,
                "/api/network-egress/verify",
                actor,
                request,
                SessionMaterialValidation.MaximumBytes,
                cancellationToken).ConfigureAwait(false);
            if (!actor.Bearer.IsUsable())
                throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.AuthorizationChanged);

            if (!IsSuccessStatusCode(status))
            {
                if (response is { Receipt: null, FailureCode: not null })
                {
                    // Validate the owner's error envelope before preserving its distinct fail-closed code.
                    try
                    {
                        _ = response.ValidateFor(request);
                    }
                    catch (RuntimeAuthorizationException failure) when (IsKnownFailureCode(failure.Code))
                    {
                        throw;
                    }
                }
                throw new RuntimeAuthorizationException(status switch
                {
                    HttpStatusCode.NotFound => RuntimeEgressVerificationFailureCodes.Missing,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                        RuntimeEgressVerificationFailureCodes.AuthorizationChanged,
                    HttpStatusCode.Conflict => RuntimeEgressVerificationFailureCodes.StaleFence,
                    _ => RuntimeEgressVerificationFailureCodes.Unavailable
                });
            }

            return (response ?? throw new RuntimeAuthorizationException(
                "egress_verification_contract_invalid")).ValidateFor(request);
        }
        catch (RuntimeAuthorizationException exception) when (
            exception.Code is "runtime_owner_denied" or "runtime_owner_binding_changed")
        {
            throw new RuntimeAuthorizationException(
                exception.Code == "runtime_owner_denied"
                    ? RuntimeEgressVerificationFailureCodes.AuthorizationChanged
                    : RuntimeEgressVerificationFailureCodes.SelectionChanged);
        }
        catch (RuntimeAuthorizationException exception) when (
            exception.Code is "runtime_owner_unavailable" or "runtime_owner_redirect_rejected")
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.Unavailable);
        }
        catch (RuntimeAuthorizationException exception) when (
            exception.Code.StartsWith("runtime_owner_", StringComparison.Ordinal))
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.Unavailable);
        }
        catch (HttpRequestException)
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.Unavailable);
        }
        catch (IOException)
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.Unavailable);
        }
        catch (InvalidOperationException) when (!actor.Bearer.IsUsable())
        {
            throw new RuntimeAuthorizationException(RuntimeEgressVerificationFailureCodes.AuthorizationChanged);
        }
    }

    /// <summary>Restricts remote failure codes to the documented fail-closed outcomes.</summary>
    internal static bool IsKnownFailureCode(string code) => code is
        RuntimeEgressVerificationFailureCodes.Missing or
        RuntimeEgressVerificationFailureCodes.StaleFence or
        RuntimeEgressVerificationFailureCodes.GenerationUnverified or
        RuntimeEgressVerificationFailureCodes.AuthorizationChanged or
        RuntimeEgressVerificationFailureCodes.SelectionChanged or
        RuntimeEgressVerificationFailureCodes.Unavailable;

    private static bool IsSuccessStatusCode(HttpStatusCode status) =>
        (int)status is >= 200 and <= 299;
}
