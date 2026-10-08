using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Agentweaver.Providers;
using Agentweaver.SourceControl;
using Microsoft.AspNetCore.Http;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class SourceControlOwnerStorePostgresTests(CoordinationPostgresFixture fixture)
{
    [Fact]
    public async Task PersistsImmutablePinAndPreApprovalIntentIdempotently()
    {
        var schema = "source_control_" + Guid.NewGuid().ToString("N");
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, schema);
        try
        {
            await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, schema);
            var actor = new CoordinationActor("https://identity.example/", Guid.NewGuid().ToString("D"));
            var selection = CreateSelection(actor);
            var identity = new SessionIdentity(
                selection.Selection.ProjectId, selection.Selection.RunId, "root");
            var ownerStore = new CoordinationOwnerStore(fixture.DataSource, schema);
            var acceptedRoot = await ownerStore.AcceptRootAsync(
                actor, selection, identity.SessionId, CancellationToken.None);
            var providerCatalog = CreateSourceControlCatalog();
            var providerResolver = new ProviderResolver(providerCatalog);
            var selectionContexts = new CoordinatorRunSelectionContextStore(
                fixture.DataSource, schema, providerCatalog, providerResolver, []);
            var decisions = new CoordinatorDecisionOwnerStore(
                fixture.DataSource, schema, selectionContexts, TimeProvider.System);
            var current = await decisions.InitializeRootAsync(
                actor, identity, selection, CancellationToken.None);
            Assert.Equal(acceptedRoot.ExecutionFence, current.State.Fence);

            var acceptedRun = CreateAcceptedRun(actor, identity, selection, current.State.Fence);
            var pin = CreatePin(acceptedRun, providerResolver);
            var (mergeReadyState, assembly) = MergeReadyForApproval(current.State.Fence);
            var intentRequest = SourceControlMergeIntentRequest.Create(
                intentId: "source-merge-test",
                approvalRequestId: "source-approval-test",
                acceptedRun,
                pin,
                current.StateVersion,
                mergeReadyState,
                assembly,
                PullRequest(),
                SourceControlMergeMethod.Squash,
                DateTimeOffset.Parse("2026-10-07T08:00:00Z"));
            var sourceStore = new SourceControlOwnerStore(
                fixture.DataSource, schema, providerCatalog, providerResolver);
            var savedPin = await sourceStore.PersistRepositoryPinAsync(
                actor, identity, selection, pin, current.StateVersion, CancellationToken.None);
            Assert.Equal(pin.PinId, savedPin.PinId);
            var casingVariantPin = CreatePin(
                acceptedRun, providerResolver, new SourceControlRepositoryIdentity("Octo", "Repo"));
            var sameRepositoryPin = await sourceStore.PersistRepositoryPinAsync(
                actor, identity, selection, casingVariantPin, current.StateVersion, CancellationToken.None);
            Assert.Equal(pin.Repository, sameRepositoryPin.Repository);

            var saved = await sourceStore.PersistMergeIntentAsync(
                actor, identity, selection, intentRequest, "idempotency-1", CancellationToken.None);
            var replay = await sourceStore.PersistMergeIntentAsync(
                actor, identity, selection, intentRequest, "idempotency-1", CancellationToken.None);
            var read = await sourceStore.ReadMergeIntentAsync(
                actor, identity, selection, saved.IntentId, CancellationToken.None);
            var acceptedPin = await sourceStore.ReadRepositoryPinAsync(
                actor, identity, selection, CancellationToken.None);

            Assert.Equal("approval_pending", saved.State);
            Assert.Equal(saved.IntentId, replay.IntentId);
            Assert.Equal(saved.IntentId, read.IntentId);
            Assert.Equal(pin.PinId, read.Pin.PinId);
            Assert.Equal(pin.ApiCredential.Secret.Id, read.Pin.ApiCredential.Secret.Id);
            Assert.Equal(pin.ApiCredential.Secret.Version, read.Pin.ApiCredential.Secret.Version);
            Assert.Equal(pin.WebhookCredential!.Secret.Id, acceptedPin.WebhookCredential!.Secret.Id);
            Assert.Equal(pin.WebhookCredential.Secret.Version, acceptedPin.WebhookCredential.Secret.Version);
            Assert.Equal(intentRequest.PullRequest.HeadSha, read.HeadSha);
            Assert.Equal(intentRequest.PullRequest.BaseSha, read.BaseSha);
            Assert.Equal(current.StateVersion, read.SourceStateVersion);
            Assert.Null(read.GrantReference);

            var webhookPayload = Encoding.UTF8.GetBytes("{\"repository\":\"octo/repo\"}");
            var webhook = new GitHubWebhookEnvelope(
                Guid.NewGuid().ToString(),
                "pull_request",
                "opened",
                new SourceControlRepositoryIdentity("Octo", "Repo"),
                pin.ProviderRepositoryId,
                intentRequest.PullRequest.Number,
                intentRequest.PullRequest.HeadSha,
                intentRequest.PullRequest.BaseSha);
            var webhookHash = Convert.ToHexString(SHA256.HashData(webhookPayload)).ToLowerInvariant();
            Assert.False(await sourceStore.RecordWebhookDeliveryAsync(
                actor,
                identity,
                selection,
                acceptedPin,
                webhook,
                webhookHash,
                current.StateVersion,
                CancellationToken.None));
            Assert.True(await sourceStore.RecordWebhookDeliveryAsync(
                actor,
                identity,
                selection,
                acceptedPin,
                webhook,
                webhookHash,
                current.StateVersion,
                CancellationToken.None));
            var wrongRepository = webhook with
            {
                DeliveryId = Guid.NewGuid().ToString(),
                Repository = new SourceControlRepositoryIdentity("octo", "other")
            };
            var wrongRepositoryException = await Assert.ThrowsAsync<CoordinationException>(() =>
                sourceStore.RecordWebhookDeliveryAsync(
                    actor,
                    identity,
                    selection,
                    acceptedPin,
                    wrongRepository,
                    webhookHash,
                    current.StateVersion,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, wrongRepositoryException.StatusCode);
            var wrongRepositoryId = webhook with
            {
                DeliveryId = Guid.NewGuid().ToString(),
                ProviderRepositoryId = pin.ProviderRepositoryId + 1
            };
            var wrongRepositoryIdException = await Assert.ThrowsAsync<CoordinationException>(() =>
                sourceStore.RecordWebhookDeliveryAsync(
                    actor,
                    identity,
                    selection,
                    acceptedPin,
                    wrongRepositoryId,
                    webhookHash,
                    current.StateVersion,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, wrongRepositoryIdException.StatusCode);
            var replayConflict = await Assert.ThrowsAsync<CoordinationException>(() =>
                sourceStore.RecordWebhookDeliveryAsync(
                    actor,
                    identity,
                    selection,
                    acceptedPin,
                    webhook,
                    new string('a', 64),
                    current.StateVersion,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, replayConflict.StatusCode);

            var conflict = await Assert.ThrowsAsync<CoordinationException>(() =>
                sourceStore.PersistMergeIntentAsync(
                    actor,
                    identity,
                    selection,
                    SourceControlMergeIntentRequest.Create(
                        intentRequest.IntentId,
                        intentRequest.ApprovalRequestId,
                        acceptedRun,
                        pin,
                        current.StateVersion,
                        mergeReadyState,
                        assembly,
                        PullRequest(),
                        SourceControlMergeMethod.Rebase,
                        DateTimeOffset.Parse("2026-10-07T08:00:00Z")),
                    "idempotency-1",
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"""
                SELECT
                    (SELECT count(*) FROM "{schema}".source_control_repository_pins),
                    (SELECT count(*) FROM "{schema}".source_control_merge_intents),
                    (SELECT count(*) FROM "{schema}".source_control_webhook_deliveries),
                    (SELECT intent_state FROM "{schema}".source_control_merge_intents
                     WHERE intent_id = @intent),
                    (SELECT count(*) FROM "{schema}".executable_action_grants
                     WHERE source_control_intent_id = @intent)
                """, connection);
            command.Parameters.AddWithValue("intent", NpgsqlDbType.Varchar, saved.IntentId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal("approval_pending", reader.GetString(3));
            Assert.Equal(0L, reader.GetInt64(4));
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RepositoryMergeLockSerializesSameRepositoryButAllowsIndependentRepositories()
    {
        var catalog = CreateSourceControlCatalog();
        var store = new SourceControlOwnerStore(
            fixture.DataSource, "source_control_lock_test", catalog, new ProviderResolver(catalog));
        var repository = new SourceControlRepositoryIdentity("octo", "repo");
        var otherRepository = new SourceControlRepositoryIdentity("octo", "other");
        Task<SourceControlRepositoryMergeLock> waitingForSameRepository;

        await using (await store.AcquireRepositoryMergeLockAsync(repository, CancellationToken.None))
        {
            await using var independent =
                await store.AcquireRepositoryMergeLockAsync(otherRepository, CancellationToken.None);
            waitingForSameRepository =
                store.AcquireRepositoryMergeLockAsync(repository, CancellationToken.None);
            Assert.True(
                await WaitForAdvisoryLockWaitAsync(fixture.DataSource),
                "the second acquisition must be blocked inside PostgreSQL on the repository advisory lock");
            Assert.False(waitingForSameRepository.IsCompleted);
        }

        await using var sameRepository =
            await waitingForSameRepository.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<bool> WaitForAdvisoryLockWaitAsync(NpgsqlDataSource dataSource)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE datname = current_database()
                      AND wait_event = 'advisory'
                      AND query LIKE '%pg_advisory_lock%'
                      AND query LIKE '%agentweaver.source-control.merge%')
                """, connection);
            if ((bool)(await command.ExecuteScalarAsync())!)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
        return false;
    }

    private static AuthorizedRunSelection CreateSelection(CoordinationActor actor)
    {
        using var document = JsonDocument.Parse("""{"source":"projects-config"}""");
        var runId = Guid.NewGuid().ToString("D");
        var effective = new EffectiveRunSelection(
            "project-1",
            runId,
            1,
            1,
            1,
            "context-v1",
            document.RootElement.Clone());
        return new AuthorizedRunSelection(
            effective,
            new ProjectsAuthorizationContext(
                1,
                actor.Issuer,
                actor.Subject,
                "tenant-1",
                1,
                effective.ProjectId,
                effective.RunId,
                ImmutableArray.Create(new ProjectsAuthority(
                    "project",
                    effective.ProjectId,
                    ImmutableArray.Create(new ProjectsPermissionGrant("acceptRunSelection", 1))))));
    }

    private static SourceControlAcceptedRunBinding CreateAcceptedRun(
        CoordinationActor actor,
        SessionIdentity identity,
        AuthorizedRunSelection selection,
        long fence) =>
        new(
            actor.Issuer,
            actor.Subject,
            selection.Authorization.TenantId,
            identity.ProjectId,
            identity.RunId,
            identity.SessionId,
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(selection.Selection.Snapshot.GetRawText()))),
            selection.Selection.ProjectRevision,
            selection.Selection.ProjectConfigurationRevision,
            selection.Selection.PlatformRuntimeRevision,
            selection.Selection.ContextRevision,
            fence);

    private static ProviderCatalog CreateSourceControlCatalog()
    {
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge);
        return Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [
                new ProviderRegistration(
                    new ProviderDescriptor(
                        ProviderSeam.SourceControl,
                        SourceControlProviderIds.GitHub,
                        new Version(1, 0, 0),
                        1,
                        ProviderHostingPattern.InProcess,
                        capabilities),
                    Enabled: true,
                    "source-control-v1",
                    1)
            ],
            [new ProviderSelection(ProviderSeam.SourceControl, SourceControlProviderIds.GitHub)],
            []).Value);
    }

    private static SourceControlRepositoryPin CreatePin(
        SourceControlAcceptedRunBinding acceptedRun,
        ProviderResolver resolver,
        SourceControlRepositoryIdentity? repository = null)
    {
        var candidate = Assert.IsType<ProviderCandidate>(resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.SourceControl,
            ProjectOverrideId: null,
            new Version(1, 0, 0),
            1,
            ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal))).Value!.Candidate);
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            SourceControlCapabilities.RepositoryRead,
            SourceControlCapabilities.Merge);
        var resource = new ProviderResourceRef(
            ProviderSeam.SourceControl, SourceControlProviderIds.GitHub, "repository-123", 1);
        var negotiation = new ResourceNegotiation(resource, capabilities);
        var binding = Assert.IsType<PinnedProviderBinding>(
            resolver.Pin(acceptedRun.RunId, candidate, resource.ResourceId, negotiation).Value);
        return new SourceControlRepositoryPin(
            "source-pin-test",
            acceptedRun,
            binding,
            repository ?? new SourceControlRepositoryIdentity("octo", "repo"),
            new SourceControlCredentialReference(
                new SecretRef("github-api-v1", "version-1"),
                SourceControlSecretPurposes.Api),
            checkoutCredential: null,
            webhookCredential: new SourceControlCredentialReference(
                new SecretRef("github-webhook-v1", "version-1"),
                SourceControlSecretPurposes.Webhook),
            123,
            "main",
            isPrivate: false,
            DateTimeOffset.Parse("2026-10-07T08:00:00Z"));
    }

    private static (CoordinatorDecisionState State, CoordinatorAssemblyRequestSnapshot Assembly)
        MergeReadyForApproval(long fence)
    {
        var workflow = WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Fixed("setup", 0, [], ["docs/plan.md"]),
            WorkflowTestData.Open("implement", 1, ["setup"]),
            WorkflowTestData.Platform("merge", 2, ["implement"], WorkflowPlatformGate.Merge));
        var catalog = new AuthorizedWorkflowCatalog(workflow, [workflow]);
        var outcome = new CoordinatorOutcomeSpecification(
            "outcome-1",
            "Implement the requested change.",
            "The requested change is available.",
            "Only requested workflow behavior changes.",
            "Existing provider pins remain authoritative.",
            []);
        var proposedOutcome = CoordinatorDecisionFlow.ProposeOutcomeSpec(
            CoordinatorDecisionState.Create(fence),
            outcome,
            "outcome-approval-1",
            "owner-1");
        var confirmedOutcome = proposedOutcome.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            proposedOutcome.State.PendingGate!.RequestId,
            "owner-1",
            fence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var selected = CoordinatorDecisionFlow.SelectWorkflow(
            confirmedOutcome,
            catalog,
            requestedWorkflowId: null,
            firstUseRequestId: null,
            authorizedActorId: null);
        var plan = CoordinatorDecisionFlow.ProposeWorkPlan(
            selected.State,
            WorkflowTestData.Plan(WorkflowTestData.Item("implementation")),
            WorkflowTestData.SelectionContext(),
            "plan-approval-1",
            "owner-1");
        var confirmedPlan = plan.State!.ApplyGateAnswer(new CoordinatorGateAnswer(
            plan.State.PendingGate!.RequestId,
            "owner-1",
            fence,
            CoordinatorGateChoices.Approve,
            null)).State;
        var assembly = CoordinatorDecisionFlow.RequestAssembly(
            confirmedPlan,
            new CoordinatorAssemblyRequest(
                "assembly-request-1",
                workflow.Id,
                workflow.Revision,
                "plan-1",
                "merge",
                WorkflowPlatformGate.Merge));
        Assert.True(assembly.IsSuccess);
        return (assembly.State!, assembly.Value!);
    }

    private static SourceControlPullRequest PullRequest() =>
        new(
            12,
            new Uri("https://github.com/octo/repo/pull/12"),
            "open",
            "feature",
            new string('a', 40),
            "main",
            new string('b', 40),
            Merged: false,
            SourceControlPullRequestDisposition.Observed);
}
