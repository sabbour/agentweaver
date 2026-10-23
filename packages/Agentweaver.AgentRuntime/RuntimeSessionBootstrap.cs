using Agentweaver.Abstractions;
using Agentweaver.Identity;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Consumes a configure credential, obtains observe and model credentials, loads accepted skill
/// and recovery material, then creates an authorized Copilot session.
/// </summary>
public sealed class RuntimeSessionBootstrap(
    IRuntimeRegistrationOwner owner,
    RuntimeBrokerCredentialClient broker,
    RuntimeCopilotSessionFactory sessions,
    RuntimeSkillContentHttpClient skillContent,
    RuntimeActorAuthorization actor,
    TimeProvider timeProvider,
    SandboxImageIdentity? expectedImage = null,
    RuntimeSessionMaterialHttpClient? material = null,
    RuntimeActionHttpClient? actions = null,
    Func<RuntimeRegistration, CancellationToken, Task>? requireReadiness = null,
    RuntimeMcpServerEndpoint? capturedMcpServer = null,
    RuntimeHostedModelReasoningCatalogHttpClient? hostedReasoningCatalog = null,
    ILogger? logger = null)
{
    /// <summary>
    /// Creates a session only while owner registration, credentials, model binding, skills,
    /// placement, and optional resume authority remain unchanged.
    /// </summary>
    public async Task<AuthorizedRuntimeSession> ConfigureAsync(
        ReadOnlyMemory<byte> configuration,
        RuntimeCredentialProof bootstrap,
        Guid consumeOperationId,
        Guid exchangeOperationId,
        CancellationToken cancellationToken,
        RuntimeResumeBootstrapRequest? resumeBootstrap = null)
    {
        if (consumeOperationId == Guid.Empty || exchangeOperationId == Guid.Empty ||
            consumeOperationId == exchangeOperationId ||
            bootstrap.Purpose != RuntimeCredentialPurpose.Configure ||
            RuntimeContractValidation.Hash(configuration.Span) != bootstrap.ConfigurationHash ||
            capturedMcpServer is { IsValid: false })
            throw new RuntimeAuthorizationException("runtime_configuration_invalid");
        if (resumeBootstrap is not null)
        {
            resumeBootstrap.Validate();
            var expectedConfiguration = RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(
                resumeBootstrap.Resume.SourceRelease.HostReceipt);
            if (resumeBootstrap.ConsumeOperationId != consumeOperationId ||
                resumeBootstrap.ExchangeOperationId != exchangeOperationId ||
                resumeBootstrap.BootstrapOperationId == Guid.Empty ||
                expectedConfiguration.Reference.Material!.Sha256 != bootstrap.ConfigurationHash)
                throw new RuntimeAuthorizationException("runtime_resume_bootstrap_request_invalid");
        }

        var registration = await owner.ReadCurrentAsync(
            bootstrap.RuntimeInstanceId, actor, cancellationToken);
        RequireCurrent(registration, actor, timeProvider);
        if (resumeBootstrap is not null && resumeBootstrap.Registration != registration)
            throw new RuntimeAuthorizationException("runtime_resume_registration_stale");
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
        var acceptedSkills = await skillContent.ReadAsync(registration, cancellationToken)
            .ConfigureAwait(false);
        await RequireUnchangedAsync(registration, cancellationToken).ConfigureAwait(false);

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
            RuntimeSessionRecovery? recovery = null;
            Func<CancellationToken, Task<RuntimeSessionRecovery>>? journalFallback = null;
            async Task RequireSkillContentCurrentAsync(CancellationToken token)
            {
                var current = await skillContent.ReadAsync(registration, token).ConfigureAwait(false);
                if (!SameAcceptedSkills(acceptedSkills, current))
                    throw new RuntimeAuthorizationException("runtime_skill_content_stale");
            }
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
                await RequireSkillContentCurrentAsync(token).ConfigureAwait(false);
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
            if (resumeBootstrap is not null && material is null)
                throw new RuntimeAuthorizationException("runtime_resume_cache_unavailable");
            if (material is not null)
            {
                if (resumeBootstrap is null)
                    recovery = await material.ReadRecoveryAsync(
                        registration, cancellationToken).ConfigureAwait(false);
                else
                {
                    recovery = await material.ReadResumeCacheAsync(
                        registration, resumeBootstrap, cancellationToken).ConfigureAwait(false);
                    journalFallback = async token =>
                    {
                        await RequireSourceAuthorityAsync(token).ConfigureAwait(false);
                        var journal = await material.ReadJournalRebuildAsync(
                            registration, resumeBootstrap, token).ConfigureAwait(false);
                        await RequireSourceAuthorityAsync(token).ConfigureAwait(false);
                        return journal;
                    };
                }
            }
            await RequireSourceAuthorityAsync(cancellationToken);
            session = await sessions.CreateAsync(
                registration, modelReference, modelCredential.Credential, RequireSourceAuthorityAsync,
                cancellationToken, acceptedSkills, recovery,
                actions is null ? null : (request, token) =>
                    actions.RequireAsync(request, RequireSourceAuthorityAsync, token),
                capturedMcpServer,
                journalFallback);
            if (session.VerifiedHostedReasoningEfforts is { } supportedEfforts &&
                session.VerifiedHostedReasoningCatalogAt is { } verifiedAt &&
                registration.Binding.ModelConnectionId is { } connectionId &&
                registration.Binding.ModelSelectionReference is { } reference &&
                hostedReasoningCatalog is not null)
            {
                // The publication is a UI capability hint; the live check above remains the authority for this run.
                await RequireSourceAuthorityAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var status = await hostedReasoningCatalog.PublishAsync(
                        registration.Binding.ProjectId,
                        new HostedModelReasoningCatalogPublicationV1(
                            sessions.ModelBindingsRevision, reference, connectionId, supportedEfforts, verifiedAt),
                        cancellationToken).ConfigureAwait(false);
                    if (status is not (HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent))
                        (logger ?? NullLogger.Instance).LogWarning(
                            "AgentHost could not publish verified hosted reasoning levels; run-start verification remains authoritative. Projects & Config returned {StatusCode}.",
                            (int)status);
                }
                catch (HttpRequestException exception)
                {
                    (logger ?? NullLogger.Instance).LogWarning(
                        exception,
                        "AgentHost could not publish verified hosted reasoning levels; run-start verification remains authoritative.");
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    (logger ?? NullLogger.Instance).LogWarning(
                        exception,
                        "AgentHost timed out publishing verified hosted reasoning levels; run-start verification remains authoritative.");
                }
                catch (RuntimeAuthorizationException exception)
                    when (exception.Code == "runtime_owner_response_contract_invalid")
                {
                    (logger ?? NullLogger.Instance).LogWarning(
                        exception,
                        "Projects & Config did not honor the hosted catalog response contract; run-start verification remains authoritative.");
                }
                await RequireSourceAuthorityAsync(cancellationToken).ConfigureAwait(false);
            }
            if (resumeBootstrap is not null &&
                session.RecoveryMode is not (RuntimeSessionRecoveryMode.NativeCache or RuntimeSessionRecoveryMode.JournalRebuild))
                throw new RuntimeAuthorizationException("runtime_resume_cache_unavailable");
            await RequireSourceAuthorityAsync(cancellationToken);
            authorized = new AuthorizedRuntimeSession(registration, source.Receipt, source.Credential,
                session, modelCredential, owner, broker, actor, timeProvider, actions, requireReadiness,
                RequireSkillContentCurrentAsync);
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

    /// <summary>Reads the owner again and fails if the registration changed during bootstrap.</summary>
    private async Task RequireUnchangedAsync(
        RuntimeRegistration expected, CancellationToken cancellationToken)
    {
        var current = await owner.ReadCurrentAsync(expected.RuntimeInstanceId, actor, cancellationToken);
        RequireCurrent(current, actor, timeProvider);
        if (current != expected)
            throw new RuntimeAuthorizationException("runtime_registration_stale");
    }

    /// <summary>Validates registration shape, active state, expiry, and actor bearer lifetime.</summary>
    internal static void RequireCurrent(
        RuntimeRegistration registration, RuntimeActorAuthorization actor, TimeProvider timeProvider)
    {
        RuntimeContractValidation.Validate(registration);
        if (registration.State != RuntimeRegistrationState.Active ||
            registration.ExpiresAt <= timeProvider.GetUtcNow() || !actor.Bearer.IsUsable())
            throw new RuntimeAuthorizationException("runtime_registration_unavailable");
    }

    /// <summary>Ensures a broker receipt belongs to this registration and does not outlive it.</summary>
    private static void RequireRegistrationReceipt(
        RuntimeGrantReceipt receipt, RuntimeRegistration registration)
    {
        if (receipt.RuntimeInstanceId != registration.RuntimeInstanceId ||
            receipt.RegistrationRevision != registration.Revision ||
            receipt.ExpiresAt > registration.ExpiresAt)
            throw new RuntimeAuthorizationException("runtime_grant_registration_mismatch");
    }

    /// <summary>Builds the observe proof from an exchange that contains a live source credential.</summary>
    private static RuntimeCredentialProof SourceProof(RuntimeCredentialExchange source) =>
        new(source.Receipt.GrantId, source.Receipt.RuntimeInstanceId, source.Receipt.Revision,
            RuntimeCredentialPurpose.Observe, source.Receipt.Audience, source.Receipt.ConfigurationHash,
            source.Credential ?? throw new RuntimeAuthorizationException("runtime_source_credential_unavailable"));

    /// <summary>Compares accepted skills by ID, revision, digest, and count.</summary>
    private static bool SameAcceptedSkills(
        SkillRuntimeContentProjectionV1 expected,
        SkillRuntimeContentProjectionV1 current) =>
        expected.Skills.Length == current.Skills.Length &&
        expected.Skills.Zip(current.Skills).All(pair =>
            pair.First.SkillId == pair.Second.SkillId &&
            pair.First.Revision == pair.Second.Revision &&
            pair.First.ContentDigest == pair.Second.ContentDigest);
}
