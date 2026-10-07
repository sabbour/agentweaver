using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeSessionBootstrap(
    IRuntimeRegistrationOwner owner,
    RuntimeBrokerCredentialClient broker,
    RuntimeCopilotSessionFactory sessions,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider)
{
    public async Task<AuthorizedRuntimeSession> ConfigureAsync(
        ReadOnlyMemory<byte> configuration,
        RuntimeCredentialProof bootstrap,
        Guid consumeOperationId,
        Guid exchangeOperationId,
        SecretCredential sdkCredential,
        CancellationToken cancellationToken)
    {
        if (consumeOperationId == Guid.Empty || exchangeOperationId == Guid.Empty ||
            consumeOperationId == exchangeOperationId ||
            bootstrap.Purpose != RuntimeCredentialPurpose.Configure ||
            RuntimeContractValidation.Hash(configuration.Span) != bootstrap.ConfigurationHash)
            throw new RuntimeAuthorizationException("runtime_configuration_invalid");

        var registration = await owner.ReadCurrentAsync(
            bootstrap.RuntimeInstanceId, actor, cancellationToken);
        RequireCurrent(registration, actor, timeProvider);
        var modelReference = registration.Binding.ModelSelectionReference;
        if (modelReference is null)
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        if (bootstrap.Audience != registration.Binding.ConfigureEndpoint)
            throw new RuntimeAuthorizationException("runtime_configuration_audience_invalid");

        var consumed = await broker.ConsumeBootstrapAsync(bootstrap, consumeOperationId, cancellationToken);
        RequireRegistrationReceipt(consumed, registration);
        await RequireUnchangedAsync(registration, cancellationToken);

        RuntimeCredentialExchange? source = null;
        RuntimeCopilotSession? session = null;
        try
        {
            var consumedProof = new RuntimeCredentialProof(
                bootstrap.GrantId, bootstrap.RuntimeInstanceId, consumed.Revision,
                bootstrap.Purpose, bootstrap.Audience, bootstrap.ConfigurationHash, bootstrap.Credential);
            source = await broker.ExchangeBootstrapAsync(consumedProof, exchangeOperationId, cancellationToken);
            RequireRegistrationReceipt(source.Receipt, registration);
            if (source.IsReplay || source.Credential is null ||
                source.Receipt.Audience != registration.Binding.ObservationEndpoint)
                throw new RuntimeAuthorizationException("runtime_source_credential_unavailable");
            var sourceProof = SourceProof(source);
            await broker.VerifySourceAsync(sourceProof, cancellationToken);
            await RequireUnchangedAsync(registration, cancellationToken);
            session = await sessions.CreateHostedAsync(
                registration, modelReference, sdkCredential, cancellationToken);
            await broker.VerifySourceAsync(sourceProof, cancellationToken);
            await RequireUnchangedAsync(registration, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.Credential.IsUsable() || !sdkCredential.IsUsable())
                throw new RuntimeAuthorizationException("runtime_credential_unavailable");
            return new AuthorizedRuntimeSession(registration, source.Receipt, source.Credential,
                session, owner, broker, actor, timeProvider);
        }
        catch (Exception failure)
        {
            try
            {
                if (source?.Credential is { } credential && credential.IsUsable())
                    await broker.RevokeAsync(SourceProof(source), Guid.NewGuid(), CancellationToken.None);
            }
            catch (Exception revocationFailure)
            {
                throw new AggregateException("Runtime initialization and credential revocation failed.",
                    failure, revocationFailure);
            }
            finally
            {
                source?.Credential?.Invalidate();
                if (session is not null)
                    await session.DisposeAsync();
            }
            throw;
        }
    }

    private async Task RequireUnchangedAsync(
        RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        var current = await owner.ReadCurrentAsync(expected.RuntimeInstanceId, actor, cancellationToken);
        RequireCurrent(current, actor, timeProvider);
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    internal static void RequireCurrent(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, TimeProvider timeProvider)
    {
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
    }

    private static void RequireRegistrationReceipt(
        RuntimeGrantReceipt receipt, RuntimeRegistration registration)
    {
        if (receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_grant_registration_mismatch");
    }

    private static RuntimeCredentialProof SourceProof(RuntimeCredentialExchange source) =>
        new(source.Receipt.GrantId, source.Receipt.RuntimeInstanceId, source.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, source.Receipt.Audience, source.Receipt.ConfigurationHash,
            source.Credential ?? throw new RuntimeAuthorizationException("runtime_source_credential_unavailable"));
}
