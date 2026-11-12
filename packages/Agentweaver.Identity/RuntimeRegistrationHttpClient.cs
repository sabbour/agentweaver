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
        => await ReadCurrentAsync(
            runtimeInstanceId, actor, null, cancellationToken).ConfigureAwait(false);

    public async Task<RuntimeRegistration> ReadCurrentForCheckpointRecoveryAsync(
        Guid runtimeInstanceId, Guid operationId, RuntimeActorAuthorization actor,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_checkpoint_recovery_unavailable");
        return await ReadCurrentAsync(
            runtimeInstanceId, actor, operationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RuntimeRegistration> ReadCurrentAsync(
        Guid runtimeInstanceId, RuntimeActorAuthorization actor, Guid? checkpointRecoveryOperationId,
        CancellationToken cancellationToken)
    {
        if (runtimeInstanceId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_registration_invalid");
        var path = $"/internal/runtime/registrations/{runtimeInstanceId:D}";
        if (checkpointRecoveryOperationId is { } operationId)
            path += $"/checkpoint-recovery/{operationId:D}";
        var registration = await RuntimeOwnerHttpTransport.SendAsync<RuntimeRegistration>(
            client, _orchestratorAddress, path, actor,
            null, cancellationToken).ConfigureAwait(false);
        RuntimeContractValidation.Validate(registration);
        if (registration.RuntimeInstanceId != runtimeInstanceId)
            throw new RuntimeAuthorizationException("runtime_registration_mismatch");
        return registration;
    }
}
