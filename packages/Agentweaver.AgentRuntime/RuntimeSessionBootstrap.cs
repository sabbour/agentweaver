using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.AgentRuntime;

public sealed class RuntimeSessionBootstrap(
    IRuntimeRegistrationOwner owner,
    RuntimeBrokerCredentialClient broker,
    RuntimeCopilotSessionFactory sessions,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider,
    SandboxImageIdentity? expectedImage = null,
    RuntimeSessionMaterialHttpClient? material = null,
    RuntimeActionHttpClient? actions = null,
    Func<RuntimeRegistration, CancellationToken, Task>? requireReadiness = null)
{
    public async Task<AuthorizedRuntimeSession> ConfigureAsync(
        ReadOnlyMemory<byte> configuration,
        RuntimeCredentialProof bootstrap,
        Guid consumeOperationId,
        Guid exchangeOperationId,
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
        if (expectedImage is { } image)
        {
            image.Validate();
            if (registration.Binding.Image != image)
                throw new RuntimeAuthorizationException("runtime_image_mismatch");
        }
        var modelReference = registration.Binding.ModelSelectionReference;
        if (modelReference is null)
            throw new RuntimeAuthorizationException("runtime_model_reference_unavailable");
        if (registration.Binding.ModelSourceMode == ModelSourceMode.HostedCopilot
            ? registration.Binding.ModelConnectionId is null || registration.Binding.ModelCredentialReference is not null
            : registration.Binding.ModelCredentialReference is null || registration.Binding.ModelConnectionId is not null)
            throw new RuntimeAuthorizationException("runtime_model_credential_unavailable");
        if (registration.Binding.ModelSourceMode is null)
            throw new RuntimeAuthorizationException("runtime_model_source_mode_unavailable");
        if (bootstrap.Audience != registration.Binding.ConfigureEndpoint)
            throw new RuntimeAuthorizationException("runtime_configuration_audience_invalid");

        var consumed = await broker.ConsumeBootstrapAsync(bootstrap, consumeOperationId, cancellationToken);
        RequireRegistrationReceipt(consumed, registration);
        await RequireUnchangedAsync(registration, cancellationToken);

        RuntimeCredentialExchange? source = null;
        SecretCredential? sdkCredential = null;
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
            var modelCredential = await broker.RedeemModelCredentialAsync(
                registration, consumeOperationId, cancellationToken);
            sdkCredential = modelCredential.Credential;
            await RequireUnchangedAsync(registration, cancellationToken);
            var sourceProof = SourceProof(source);
            AuthorizedRuntimeSession? authorized = null;
            async Task RequireSourceAuthorityAsync(CancellationToken token)
            {
                if (authorized is not null)
                {
                    await authorized.RequireCurrentAsync(token).ConfigureAwait(false);
                    return;
                }
                await RequireUnchangedAsync(registration, token);
                if (!sourceProof.Credential.IsUsable() || !modelCredential.Credential.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_credential_unavailable");
                if (requireReadiness is not null)
                    await requireReadiness(registration, token).ConfigureAwait(false);
                var verifiedSource = await broker.VerifySourceAsync(sourceProof, token);
                RequireRegistrationReceipt(verifiedSource, registration);
                await broker.VerifyModelCredentialAsync(modelCredential, token);
                RequireCurrent(registration, actor, timeProvider);
                token.ThrowIfCancellationRequested();
                if (verifiedSource.ExpiresAt <= timeProvider.GetUtcNow() ||
                    !sourceProof.Credential.IsUsable() || !modelCredential.Credential.IsUsable())
                    throw new RuntimeAuthorizationException("runtime_credential_unavailable");
            }
            await RequireSourceAuthorityAsync(cancellationToken);
            var recovery = material is null ? null :
                await material.ReadRecoveryAsync(registration, cancellationToken).ConfigureAwait(false);
            await RequireSourceAuthorityAsync(cancellationToken);
            session = await sessions.CreateAsync(
                registration, modelReference, modelCredential.Credential, RequireSourceAuthorityAsync,
                cancellationToken, recovery,
                actions is null ? null : (action, input, toolInvocation, token) =>
                    actions.RequireAsync(registration, action, input, RequireSourceAuthorityAsync, token,
                        isToolInvocation: toolInvocation));
            await RequireSourceAuthorityAsync(cancellationToken);
            authorized = new AuthorizedRuntimeSession(registration, source.Receipt, source.Credential,
                session, modelCredential, owner, broker, actor, timeProvider, actions, requireReadiness);
            return authorized;
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
                sdkCredential?.Invalidate();
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
