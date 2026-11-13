using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Orchestrator;

internal sealed partial class RuntimeRunStartContinuation
{
    private async Task<CheckpointRecoveryMemberContinuation?> PrepareCheckpointRecoveryMemberAsync(
        HttpContext context,
        CoordinationActor actor,
        RuntimeActorAuthorization runtimeActor,
        RunExecutionTransitionResult result,
        CheckpointRecoveryStatus status,
        ChildCheckpointRecoveryManifest manifest,
        ChildRecoveryMember member,
        RuntimeRegistrationOwner registrationOwner,
        CoordinationOwnerStore recoveryStore,
        Func<CancellationToken, Task> requireBoundary,
        CancellationToken cancellationToken)
    {
        var intent = await store.ReadIntentForStatusAsync(
            result.Session.ProjectId, result.Session.RunId, member.Session.SessionId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        var links = ReadReceiptLinks(intent);
        if (intent.IntentId != member.Startup.IntentId ||
            intent.State != "ready" ||
            intent.ExecutionFence != status.PreviousExecutionFence &&
                intent.ExecutionFence != status.ExecutionFence ||
            intent.CleanupObligation ||
            intent.AcceptedSelectionHash != manifest.SelectionHash ||
            CoordinationRuntimeRunStartStore.ReadChildBinding(intent) != member.Startup.Binding)
            throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");

        var hasPreviousEvidence = HasPreviousCheckpointRecoveryEvidence(result.OperationId, links);
        if (!hasPreviousEvidence)
        {
            using var savedLinks = JsonDocument.Parse(intent.ReceiptLinks);
            if (intent.RowVersion != member.Startup.RowVersion ||
                ChildCheckpointRecoveryManifestCodec.Hash(savedLinks.RootElement) !=
                    member.Startup.ReceiptLinksHash)
                throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        }
        var previousRegistration = ReadLinked<RuntimeRegistration>(
                links,
                hasPreviousEvidence ? "checkpointRecoveryPreviousRegistration" : "registration")
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        var previousPlacement = ReadLinked<RuntimeRunStartPlacementReceipt>(
                links,
                hasPreviousEvidence ? "checkpointRecoveryPreviousPlacement" : "placement")
            ?? member.Startup.Placement
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        previousPlacement.Validate();
        RuntimeContractValidation.Validate(previousRegistration);
        if (member.Startup.RegistrationId != previousRegistration.RuntimeInstanceId ||
            member.Startup.RegistrationRevision != previousRegistration.Revision ||
            previousRegistration.Binding.SessionId != member.Session.SessionId ||
            previousRegistration.Binding.ExecutionFence != status.PreviousExecutionFence ||
            previousRegistration.State != RuntimeRegistrationState.Active ||
            member.Startup.Placement is { } savedPlacement && savedPlacement != previousPlacement)
            throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");

        var previousOwner = CreateOwnerContext(previousRegistration.Binding);
        var owner = CreateCheckpointRecoveryOwnerContext(
            previousRegistration.Binding, status.ExecutionFence, member.Session.LogicalTurnOrdinal);
        RequireReplacementRegistration(previousOwner, previousPlacement, previousRegistration);
        if (!hasPreviousEvidence)
        {
            var currentRegistration = await registrationOwner.ReadSavedRegistrationForCheckpointRecoveryAsync(
                actor,
                result.Session.ProjectId,
                result.Session.RunId,
                member.Session.SessionId,
                result.OperationId,
                previousRegistration.RuntimeInstanceId,
                cancellationToken)
                .ConfigureAwait(false);
            if (currentRegistration != previousRegistration)
                throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        }
        var previousBootstrapId = hasPreviousEvidence &&
            links.TryGetValue("checkpointRecoveryPreviousBootstrapOperationId", out var previousBootstrap)
            ? previousBootstrap.GetGuid()
            : intent.BootstrapOperationId;
        if (previousBootstrapId == Guid.Empty)
            previousBootstrapId = member.Startup.BootstrapOperationId;
        var bootstrapId = RuntimeRunStartKeys.ReplacementOperationId(
            result.OperationId, member.Session.SessionId, "checkpoint-bootstrap");
        var consumeId = RuntimeRunStartKeys.ReplacementOperationId(
            result.OperationId, member.Session.SessionId, "checkpoint-consume");
        var exchangeId = RuntimeRunStartKeys.ReplacementOperationId(
            result.OperationId, member.Session.SessionId, "checkpoint-exchange");

        SetPreviousCheckpointRecoveryEvidence(
            links,
            result.OperationId,
            JsonSerializer.SerializeToElement(previousRegistration, JsonOptions),
            JsonSerializer.SerializeToElement(previousPlacement, JsonOptions),
            JsonSerializer.SerializeToElement(previousBootstrapId, JsonOptions));
        links.Remove("placement");
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            bootstrapId,
            RuntimeRunStartKeys.EnvironmentId(owner),
            null,
            CancellationToken.None).ConfigureAwait(false);

        await requireBoundary(cancellationToken).ConfigureAwait(false);
        var retirementRequest = new RuntimeRunStartRetirementRequest(
            2,
            owner,
            previousBootstrapId,
            previousRegistration.RuntimeInstanceId,
            previousRegistration.Revision,
            previousPlacement).Validate();
        var retirement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                RuntimeRunStartRetirementReceipt>(
            clients.CreateClient("runtime-run-start-environment"),
            options.EnvironmentOwnerAddress,
            "/internal/runtime/run-start/replacement-retire",
            runtimeActor,
            new RuntimeReplacementRetirementRequest(
                2, result.OperationId, intent.IntentId, retirementRequest)
            {
                CheckpointRecoveryOperationId = result.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        retirement.ValidateFor(retirementRequest);
        if (retirement.State != "released")
            return null;

        links["checkpointRecoveryRetirement"] =
            JsonSerializer.SerializeToElement(retirement, JsonOptions);
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            bootstrapId,
            RuntimeRunStartKeys.EnvironmentId(owner),
            null,
            CancellationToken.None).ConfigureAwait(false);

        await requireBoundary(cancellationToken).ConfigureAwait(false);
        var placement = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                RuntimeRunStartPlacementReceipt>(
            clients.CreateClient("runtime-run-start-environment"),
            options.EnvironmentOwnerAddress,
            "/internal/runtime/run-start/replacement-placement",
            runtimeActor,
            new RuntimeReplacementPlacementRequest(
                2, result.OperationId, owner, previousBootstrapId, bootstrapId, previousPlacement)
            {
                CheckpointRecoveryOperationId = result.OperationId
            },
            cancellationToken).ConfigureAwait(false);
        placement.Validate();
        RequireReplacementPlacement(previousPlacement, placement, bootstrapId, owner);
        links["placement"] = JsonSerializer.SerializeToElement(placement, JsonOptions);
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            bootstrapId,
            placement.EnvironmentId,
            null,
            CancellationToken.None).ConfigureAwait(false);

        RuntimeRegistration? registration = null;
        var linkedRegistration = ReadLinked<RuntimeRegistration>(links, "registration");
        if (linkedRegistration is not null)
        {
            try
            {
                RequireReplacementRegistration(owner, placement, linkedRegistration);
                registration = linkedRegistration;
            }
            catch (RuntimeAuthorizationException)
            {
            }
        }
        if (registration is null)
        {
            await requireBoundary(cancellationToken).ConfigureAwait(false);
            var authorization = await recoveryStore.ReadCheckpointRecoveryRegistrationAuthorizationAsync(
                actor,
                result.Session.ProjectId,
                result.Session.RunId,
                member.Session.SessionId,
                result.OperationId,
                cancellationToken).ConfigureAwait(false);
            registration = await registrationOwner.ReplaceAfterCheckpointRecoveryFenceAsync(
                context,
                authorization,
                result.Session.ProjectId,
                result.Session.RunId,
                member.Session.SessionId,
                cancellationToken).ConfigureAwait(false);
        }
        RequireReplacementRegistration(owner, placement, registration);
        links["registration"] = JsonSerializer.SerializeToElement(registration, JsonOptions);
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            bootstrapId,
            placement.EnvironmentId,
            registration.RuntimeInstanceId,
            CancellationToken.None).ConfigureAwait(false);

        return new CheckpointRecoveryMemberContinuation(
            member,
            intent,
            owner,
            previousRegistration,
            registration,
            previousPlacement,
            placement,
            bootstrapId,
            consumeId,
            exchangeId);
    }

    private async Task<RuntimeRunStartIntent?> ContinueCheckpointRecoveryMemberAsync(
        RuntimeActorAuthorization runtimeActor,
        RunExecutionTransitionResult result,
        CheckpointRecoveryStatus status,
        CheckpointRecoveryMemberContinuation member,
        CoordinationOwnerStore recoveryStore,
        Func<CancellationToken, Task> requireBoundary,
        CancellationToken cancellationToken)
    {
        var intent = await store.ReadIntentForStatusAsync(
            result.Session.ProjectId,
            result.Session.RunId,
            member.Member.Session.SessionId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");
        if (intent.IntentId != member.Intent.IntentId ||
            intent.ExecutionFence != status.ExecutionFence ||
            intent.State != "ready" ||
            intent.CleanupObligation)
            throw new RuntimeAuthorizationException("checkpoint_recovery_runtime_stale");

        var links = ReadReceiptLinks(intent);
        using var configuration = JsonDocument.Parse("{}");
        var configurationHash = RuntimeContractValidation.Hash(
            Encoding.UTF8.GetBytes(configuration.RootElement.GetRawText()));
        await requireBoundary(cancellationToken).ConfigureAwait(false);
        var bootstrap = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                RuntimeBootstrapDeliveryReceipt>(
            clients.CreateClient("runtime-run-start-broker"),
            options.BrokerOwnerAddress,
            "/internal/runtime/bootstrap/request",
            runtimeActor,
            new RuntimeBootstrapRequest(
                member.Registration.RuntimeInstanceId, member.BootstrapId, configurationHash)
            {
                CheckpointRecoveryOperationId = result.OperationId
            },
            cancellationToken,
            CheckpointRecoverySpentBootstrapFailureCodes).ConfigureAwait(false);
        SetCheckpointRecoveryBootstrapEvidence(
            links,
            result.OperationId,
            JsonSerializer.SerializeToElement(BootstrapReceipt(bootstrap), JsonOptions));
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            member.BootstrapId,
            member.Placement.EnvironmentId,
            member.Registration.RuntimeInstanceId,
            CancellationToken.None).ConfigureAwait(false);
        RequireBootstrapReceipt(
            intent with { BootstrapOperationId = member.BootstrapId },
            member.Registration,
            bootstrap,
            configurationHash);

        await requireBoundary(cancellationToken).ConfigureAwait(false);
        var environmentContext = await environment.ReadAsync(
            runtimeActor,
            member.Owner.ProjectId,
            member.Owner.RunId,
            member.Owner.SessionId,
            member.Placement.EnvironmentId,
            member.Registration.Binding.ProfileId,
            cancellationToken,
            result.OperationId).ConfigureAwait(false);
        RequirePlacement(member.Placement, member.Registration, environmentContext);
        var activation = await RuntimeOwnerHttpTransport.SendRuntimeRunStartAsync<
                RuntimeHostReadinessReceipt>(
            clients.CreateClient("runtime-run-start-environment"),
            options.EnvironmentOwnerAddress,
            "/internal/runtime/v1/activate",
            runtimeActor,
            new RuntimeHostConfigureRequest(
                1,
                member.Registration,
                member.ConsumeId,
                member.ExchangeId,
                configuration.RootElement.Clone())
            {
                McpServer = environmentContext.McpServer,
                CheckpointRecoveryOperationId = result.OperationId
            },
            cancellationToken,
            CheckpointRecoveryActivationFailureCodes).ConfigureAwait(false);
        CoordinationEndpoints.ValidateHostReadiness(activation, member.Registration, timeProvider);
        var recovery = activation.Recovery?.ValidateFor(member.Registration);
        if (recovery?.RecoveryMode is not (RuntimeHostRecoveryMode.NativeCache or
            RuntimeHostRecoveryMode.JournalRebuild))
            throw new RuntimeAuthorizationException("checkpoint_recovery_material_unavailable");
        var materialMode = recovery.RecoveryMode == RuntimeHostRecoveryMode.NativeCache
            ? "nativeCache"
            : "journalRebuild";
        links["activation"] = JsonSerializer.SerializeToElement(activation, JsonOptions);
        intent = await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            member.BootstrapId,
            member.Placement.EnvironmentId,
            member.Registration.RuntimeInstanceId,
            CancellationToken.None,
            currentStep: "readiness").ConfigureAwait(false);

        await requireBoundary(cancellationToken).ConfigureAwait(false);
        var readiness = await environment.ReadReadinessAsync(
            runtimeActor,
            member.Owner.ProjectId,
            member.Owner.RunId,
            member.Owner.SessionId,
            member.Placement.EnvironmentId,
            member.Registration.Binding.ProfileId,
            cancellationToken,
            result.OperationId).ConfigureAwait(false);
        RequireCurrentReadiness(timeProvider, member.Placement, member.Registration, activation, readiness);
        links["placement"] = JsonSerializer.SerializeToElement(member.Placement, JsonOptions);
        links["registration"] = JsonSerializer.SerializeToElement(member.Registration, JsonOptions);
        links["activation"] = JsonSerializer.SerializeToElement(activation, JsonOptions);
        links["readiness"] = JsonSerializer.SerializeToElement(readiness, JsonOptions);
        links["checkpointRecoveryMaterialMode"] =
            JsonSerializer.SerializeToElement(materialMode, JsonOptions);
        return await recoveryStore.PersistCheckpointRecoveryCleanupEvidenceAsync(
            intent,
            JsonSerializer.Serialize(links, JsonOptions),
            status.ExecutionFence,
            member.BootstrapId,
            member.Placement.EnvironmentId,
            member.Registration.RuntimeInstanceId,
            CancellationToken.None,
            currentStep: "ready",
            ready: true,
            consumeOperationId: member.ConsumeId,
            exchangeOperationId: member.ExchangeId).ConfigureAwait(false);
    }

    private static T? ReadLinked<T>(IReadOnlyDictionary<string, JsonElement> links, string key) =>
        links.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.Deserialize<T>(JsonOptions)
            : default;
}
