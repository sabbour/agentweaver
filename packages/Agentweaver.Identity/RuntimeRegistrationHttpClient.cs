namespace Agentweaver.Identity;

/// <summary>Reads current runtime registration state from the Orchestrator owner.</summary>
public sealed class RuntimeRegistrationHttpClient(
    HttpClient client, Uri orchestratorAddress) : IRuntimeRegistrationOwner
{
    private readonly Uri _orchestratorAddress = RuntimeOwnerHttpTransport.RequireOwnerAddress(orchestratorAddress);

    /// <summary>Retrieves and validates the exact requested runtime registration.</summary>
    /// <exception cref="RuntimeAuthorizationException">The ID is invalid or the returned registration does not match.</exception>
    public async Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        var registration = await RuntimeOwnerHttpTransport.SendAsync<RuntimeRegistration>(
            client, _orchestratorAddress, $"/internal/runtime/registrations/{runtimeInstanceId:D}", actor,
            null, cancellationToken).ConfigureAwait(false);
        RuntimeContractValidation.Validate(registration);
        if (registration.RuntimeInstanceId != runtimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_registration_mismatch");
        return registration;
    }
}
