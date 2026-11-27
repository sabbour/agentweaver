using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private const string AvailabilityCardId = "manual:availability-card";
    private const string AvailabilityCardVersion = "2026-11-22";

    [Fact]
    public async Task PublicationV2_VerifiesExactRateCardAndTransitionsToAvailable_AndSurvivesReplayAndDisable()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "avail-tenant-" + suffix;
            var actorId = "avail-admin-" + suffix;
            var caller = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "avail-prov-" + suffix;
            await draftService.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Avail Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"],
                    RateCardId: AvailabilityCardId, RateCardVersion: AvailabilityCardVersion),
                CancellationToken.None);

            var payload = CanonicalManualRateCard(AvailabilityCardId, AvailabilityCardVersion);
            var eventsCalls = 0;
            using var eventsHttp = new HttpClient(new DelegateRateCardHandler((request, _) =>
            {
                eventsCalls++;
                return Task.FromResult(RateCardOkResponse(payload));
            }));
            var rateCardClient = CreateRateCardOwnerClient(eventsHttp, TimeSpan.FromSeconds(2));
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System, rateCardClient);

            var opId = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var credVersion = new string('a', 32);
            var secretId = $"byok-platform-{providerId}";

            var preflight = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opId, ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null, null, "draft-snapshot"),
                CancellationToken.None);

            var receipt = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opId, preflight.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId),
                    new SecretRef(secretId, credVersion), caller.Issuer, actorId, tenantId,
                    preflight.MembershipId, preflight.MembershipRevision, tenantId, null),
                CancellationToken.None);

            Assert.Equal("Available", receipt.ProviderState);
            Assert.Equal(2, receipt.ProviderRevision);
            Assert.Equal(1, eventsCalls);

            var read = await draftService.ReadAsync(caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
            Assert.Equal(ModelProviderDraftState.Available, read.State);
            Assert.NotNull(read.Binding.RateCardPin);
            Assert.Equal(AvailabilityCardId, read.Binding.RateCardPin!.RateCardId);
            Assert.Equal(AvailabilityCardVersion, read.Binding.RateCardPin.RateCardVersion);
            Assert.Equal(credVersion, read.Binding.CredentialVersion);

            // Replay returns saved receipt without calling Events again.
            var replayPreflight = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opId, ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null, null, "authority-only"),
                CancellationToken.None);
            var replayReceipt = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opId, replayPreflight.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId),
                    new SecretRef(secretId, credVersion), caller.Issuer, actorId, tenantId,
                    replayPreflight.MembershipId, replayPreflight.MembershipRevision, tenantId, null),
                CancellationToken.None);
            Assert.Equal(receipt, replayReceipt);
            Assert.Equal(1, eventsCalls);

            // Disabling credential returns head to Draft and clears RateCardPin.
            var disableReceipt = await pubService.DisableAsync(
                new ModelProviderCredentialDisableRequest(
                    Guid.NewGuid(), 2, 1, slotId, credVersion,
                    ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null),
                CancellationToken.None);
            Assert.Equal(3, disableReceipt.ProviderRevision);

            var afterDisable = await draftService.ReadAsync(caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
            Assert.Equal(ModelProviderDraftState.Draft, afterDisable.State);
            Assert.Null(afterDisable.Binding.CredentialVersion);
            Assert.Null(afterDisable.Binding.RateCardPin);
        });
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("unavailable")]
    [InlineData("weak-etag")]
    [InlineData("wrong-version")]
    [InlineData("owner-timeout")]
    public async Task PublicationV2_LeavesProviderDraftOnPricingFailureOrOwnerTimeout(string mode)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "fail-tenant-" + suffix;
            var actorId = "fail-admin-" + suffix;
            var caller = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "fail-prov-" + suffix;
            await draftService.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Fail Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"],
                    RateCardId: AvailabilityCardId, RateCardVersion: AvailabilityCardVersion),
                CancellationToken.None);

            using var eventsHttp = new HttpClient(new DelegateRateCardHandler(async (_, ct) =>
            {
                if (mode == "owner-timeout")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                if (mode == "not-found")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (mode == "unavailable")
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                if (mode == "weak-etag")
                {
                    var p = CanonicalManualRateCard(AvailabilityCardId, AvailabilityCardVersion);
                    var resp = RateCardOkResponse(p);
                    resp.Headers.ETag = new EntityTagHeaderValue(resp.Headers.ETag!.Tag, isWeak: true);
                    return resp;
                }
                var wrong = CanonicalManualRateCard(AvailabilityCardId, "wrong-version");
                return RateCardOkResponse(wrong);
            }));

            var ownerTimeout = mode == "owner-timeout" ? TimeSpan.FromMilliseconds(30) : TimeSpan.FromSeconds(2);
            var rateCardClient = CreateRateCardOwnerClient(eventsHttp, ownerTimeout);
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System, rateCardClient);

            var opId = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var credVersion = new string('b', 32);
            var secretId = $"byok-platform-{providerId}";

            var preflight = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opId, ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null, null, "draft-snapshot"),
                CancellationToken.None);

            var receipt = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opId, preflight.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId),
                    new SecretRef(secretId, credVersion), caller.Issuer, actorId, tenantId,
                    preflight.MembershipId, preflight.MembershipRevision, tenantId, null),
                CancellationToken.None);

            Assert.Equal("Draft", receipt.ProviderState);
            Assert.Equal(2, receipt.ProviderRevision);
            Assert.Equal(credVersion, receipt.CredentialVersion);

            var read = await draftService.ReadAsync(caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
            Assert.Equal(ModelProviderDraftState.Draft, read.State);
            Assert.Equal(credVersion, read.Binding.CredentialVersion);
            Assert.Null(read.Binding.RateCardPin);
        });
    }

    [Fact]
    public async Task PublicationV2_CallerCancellationCancelsWriteWithoutSavingRevision()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "cancel-tenant-" + suffix;
            var actorId = "cancel-admin-" + suffix;
            var caller = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "cancel-prov-" + suffix;
            await draftService.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Cancel Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"],
                    RateCardId: AvailabilityCardId, RateCardVersion: AvailabilityCardVersion),
                CancellationToken.None);

            var eventsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var eventsHttp = new HttpClient(new DelegateRateCardHandler(async (_, ct) =>
            {
                eventsStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
            var rateCardClient = CreateRateCardOwnerClient(eventsHttp, TimeSpan.FromSeconds(5));
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System, rateCardClient);

            var opId = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var credVersion = new string('c', 32);
            var preflight = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opId, ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null, null, "draft-snapshot"),
                CancellationToken.None);

            using var callerCts = new CancellationTokenSource();
            var publishTask = pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opId, preflight.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId),
                    new SecretRef($"byok-platform-{providerId}", credVersion), caller.Issuer, actorId, tenantId,
                    preflight.MembershipId, preflight.MembershipRevision, tenantId, null),
                callerCts.Token);

            await eventsStarted.Task;
            callerCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishTask);

            var read = await draftService.ReadAsync(caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
            Assert.Equal(1, read.Binding.ProviderRevision);
            Assert.Equal(ModelProviderDraftState.Draft, read.State);
            Assert.Null(read.Binding.CredentialVersion);
            Assert.Null(await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleOrDefaultAsync(op => op.OperationId == opId, CancellationToken.None));
        });
    }

    [Fact]
    public async Task PublicationV2_SameVersionRecheckTransitionsDraftToAvailable_AndPreservesAcceptedRunPin()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var configService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "recheck-tenant-" + suffix;
            var admin = await SeedCallerAsync(db, store, "recheck-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "recheck-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await configService.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            _ = await configService.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await configService.CreateProjectAsync(owner, "Recheck Project", CancellationToken.None);
            var boundOwner = await ResolveCallerAsync(
                db, owner.ActorId, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);

            var orchSubject = "recheck-orch-" + suffix;
            var orchMembership = await SeedMembershipAsync(store, orchSubject, tenantId);
            await store.AssignRoleAsync(orchMembership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var runId = "recheck-run-" + suffix;
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "recheck-prov-" + suffix;
            await draftService.SaveAsync(boundOwner, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Recheck Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"]),
                CancellationToken.None);

            var payload = CanonicalManualRateCard(AvailabilityCardId, AvailabilityCardVersion);
            using var eventsHttp = new HttpClient(new DelegateRateCardHandler((_, _) =>
                Task.FromResult(RateCardOkResponse(payload))));
            var rateCardClient = CreateRateCardOwnerClient(eventsHttp, TimeSpan.FromSeconds(2));
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System, rateCardClient);

            var slotId = Guid.NewGuid();
            var credVersion = new string('d', 32);
            var secretId = $"byok-project-{providerId}";
            var credIdentity = new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Project, project.ProjectId, providerId);
            var credRef = new SecretRef(secretId, credVersion);

            // 1. Initial publish without rate card -> Draft, key saved (rev 2).
            var op1 = Guid.NewGuid();
            var pre1 = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    op1, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    boundOwner.Issuer, boundOwner.ActorId, tenantId, null, project.ProjectId, "draft-snapshot"),
                CancellationToken.None);
            var rec1 = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    op1, pre1.PreflightId, 1, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    credVersion, credIdentity, credRef, boundOwner.Issuer, boundOwner.ActorId, tenantId,
                    pre1.MembershipId, pre1.MembershipRevision, tenantId, null),
                CancellationToken.None);
            Assert.Equal("Draft", rec1.ProviderState);

            // 2. Attach rate card on Draft (rev 3).
            await draftService.SaveAsync(boundOwner, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 2, "Recheck Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"],
                    RateCardId: AvailabilityCardId, RateCardVersion: AvailabilityCardVersion),
                CancellationToken.None);

            // 3. Same-version recheck publish (rev 4) -> Available.
            var op2 = Guid.NewGuid();
            var pre2 = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    op2, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    boundOwner.Issuer, boundOwner.ActorId, tenantId, null, project.ProjectId, "draft-snapshot"),
                CancellationToken.None);
            var rec2 = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    op2, pre2.PreflightId, 3, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    credVersion, credIdentity, credRef, boundOwner.Issuer, boundOwner.ActorId, tenantId,
                    pre2.MembershipId, pre2.MembershipRevision, tenantId, null),
                CancellationToken.None);
            Assert.Equal("Available", rec2.ProviderState);
            Assert.Equal(4, rec2.ProviderRevision);

            // 4. Configure project and accept a run pinning this Available provider.
            var available = await draftService.ReadAsync(boundOwner, ModelProviderBindingScope.Project, project.ProjectId, providerId, CancellationToken.None);
            var pin = RuntimeModelBindingsResolver.Pin(available.Binding, "high");
            var selection = new ModelSelectionSettings(providerId, credRef, ModelSourceMode.Byok)
            {
                CredentialIdentity = credIdentity,
                ModelBindingPin = pin,
                ReasoningEffort = "high",
            };
            var updatedConfig = await configService.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings() with { ModelSelection = selection }, CancellationToken.None);
            var accepted = await configService.AcceptInteractiveRunSelectionAsync(
                orchestrator, project.ProjectId, runId,
                new InteractiveRunSelectionRequest(updatedConfig.Revision),
                CancellationToken.None);
            Assert.Equal(credVersion, accepted.ModelSelection.CredentialReference!.Version);
            Assert.Equal(AvailabilityCardId, accepted.ModelSelection.ProviderBindingRevision!.RateCardPin!.RateCardId);

            // 5. Disable credential -> head returns to Draft, accepted run still retains its original pin.
            await pubService.DisableAsync(
                new ModelProviderCredentialDisableRequest(
                    Guid.NewGuid(), 4, 1, slotId, credVersion,
                    ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    boundOwner.Issuer, boundOwner.ActorId, tenantId, null),
                CancellationToken.None);
            var afterDisable = await draftService.ReadAsync(boundOwner, ModelProviderBindingScope.Project, project.ProjectId, providerId, CancellationToken.None);
            Assert.Equal(ModelProviderDraftState.Draft, afterDisable.State);

            var pinnedRun = await configService.GetRunSelectionAsync(
                orchestrator, project.ProjectId, runId, CancellationToken.None);
            Assert.NotNull(pinnedRun);
            Assert.Equal(credVersion, pinnedRun.ModelSelection.CredentialReference!.Version);
            Assert.Equal(AvailabilityCardId, pinnedRun.ModelSelection.ProviderBindingRevision!.RateCardPin!.RateCardId);
        });
    }

    private static RuntimeRateCardOwnerClient CreateRateCardOwnerClient(HttpClient httpClient, TimeSpan timeout)
    {
        var options = new PlatformComponentCollectorOptions(
            "unused-token-file",
            new Dictionary<string, Uri>(StringComparer.Ordinal)
            {
                [PlatformComponentOwners.EventsAndSessions] = new("https://events.example/"),
            });
        return new RuntimeRateCardOwnerClient(
            httpClient,
            options,
            _ => Task.FromResult<string?>("projects-token"),
            timeout);
    }

    private static string CanonicalManualRateCard(string id, string version) =>
        RuntimeRateCardReadContract.SerializeCanonical(
            new RuntimeRateCardReadResponse(
                RuntimeRateCardReadContract.CurrentSchemaVersion,
                new CostRateCard(
                    id, version, SdkMeterSources.ByokTokens, "USD", 1_000_000_000m,
                    ImmutableDictionary<string, decimal>.Empty.Add("gpt-5|input", 1.25m)),
                new ManualRuntimeRateCardReadProvenance("https://example.com/prices", new DateOnly(2026, 11, 22))));

    private static HttpResponseMessage RateCardOkResponse(string canonicalJson)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        response.Headers.ETag = new EntityTagHeaderValue($"\"{hash}\"", isWeak: false);
        return response;
    }

    [Fact]
    public async Task UpgradeFromV2CredentialOperations_PreservesPreExistingPublishAndDisableReceipts_AndReplaysPublishAsDraft()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        try
        {
            await isolated.InitializeToAsync("20261120231128_AddModelProviderCredentialOperationsV2");

            await using var db = new ProjectsConfigDbContext(
                new DbContextOptionsBuilder<ProjectsConfigDbContext>().UseNpgsql(isolated.DataSource).Options);
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                new DbContextOptionsBuilder<ProjectsConfigDbContext>().UseNpgsql(isolated.DataSource).Options,
                TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "upg-tenant-" + suffix;
            var actorId = "upg-admin-" + suffix;
            var caller = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var membership = await db.TenantMemberships.AsNoTracking()
                .SingleAsync(m => m.Issuer == caller.Issuer && m.Subject == actorId && m.TenantId == tenantId);
            var assignment = await db.RoleAssignments.AsNoTracking()
                .SingleAsync(a => a.MembershipId == membership.MembershipId && a.Role == ProjectAuthorityRole.PlatformAdmin);

            // Seed two providers at rev 1 using raw SQL (model omits new columns on downgraded schema).
            var disabledProviderId = "upg-dis-" + suffix;
            var replayProviderId = "upg-rep-" + suffix;
            var configHash = new string('b', 64);
            var credVersion = new string('a', 32);
            var slot1 = Guid.NewGuid();
            var slot2 = Guid.NewGuid();
            var publishOp1 = Guid.NewGuid();
            var disableOp1 = Guid.NewGuid();
            var publishOp2 = Guid.NewGuid();
            var secret1 = $"byok-platform-{disabledProviderId}";
            var secret2 = $"byok-platform-{replayProviderId}";

            // Exact fingerprint matching ModelProviderCredentialPublicationV2Service.PublishAsync
            var replayFingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "publish-v2",
                OperationId = publishOp2,
                ExpectedProviderRevision = 1L,
                Scope = ModelProviderBindingScope.Platform,
                ProjectId = (string?)null,
                ProviderId = replayProviderId,
                CredentialVersion = credVersion,
                slotId = slot2,
                secretId = secret2,
                caller.Issuer,
                ActorId = actorId,
                OriginalTenant = tenantId,
            }))).ToLowerInvariant();

            await using (var conn = await isolated.DataSource.OpenConnectionAsync())
            {
                async Task SeedProviderAsync(string pid)
                {
                    await using var tx = await conn.BeginTransactionAsync();
                    await using var cmd = new NpgsqlCommand("""
                        INSERT INTO projects_config.model_providers
                          (provider_id, scope, display_name, type, base_url, deployment, reasoning_efforts,
                           state, current_revision, created_by_actor_id, created_at, updated_by_actor_id, updated_at)
                        VALUES (@id, 'Platform', 'Legacy', 'openai', 'https://example.test', 'gpt-5', ARRAY['high'],
                                'Draft', 1, @actor, transaction_timestamp(), @actor, transaction_timestamp());
                        INSERT INTO projects_config.model_provider_revisions
                          (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                           display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                           created_by_actor_id, created_at)
                        SELECT h.provider_id, 1, h.scope, h.project_id, @op, repeat('a', 64),
                               h.display_name, h.type, h.base_url, h.deployment, h.reasoning_efforts, @cfg,
                               h.updated_by_actor_id, h.updated_at
                        FROM projects_config.model_providers h WHERE h.provider_id = @id;
                        """, conn, tx);
                    cmd.Parameters.AddWithValue("id", pid);
                    cmd.Parameters.AddWithValue("actor", actorId);
                    cmd.Parameters.AddWithValue("op", Guid.NewGuid());
                    cmd.Parameters.AddWithValue("cfg", configHash);
                    await cmd.ExecuteNonQueryAsync();
                    await tx.CommitAsync();
                }

                await SeedProviderAsync(disabledProviderId);
                await SeedProviderAsync(replayProviderId);

                // Pre-existing Publish + Disable on disabledProviderId
                await using (var tx = await conn.BeginTransactionAsync())
                {
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE projects_config.model_providers
                        SET current_revision = 2, updated_by_actor_id = @actor, updated_at = transaction_timestamp()
                        WHERE provider_id = @id;
                        INSERT INTO projects_config.model_provider_credential_operations
                          (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                           expected_provider_revision, credential_slot_id, credential_secret_id, credential_version,
                           configuration_hash, original_tenant, original_membership_id, original_membership_revision,
                           current_membership_id, current_membership_revision, current_assignment_id,
                           current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                        VALUES
                          (@op, 'Publish', @id, 'Platform', NULL, 2, 1, @slot, @secret, @cred, @cfg,
                           @tenant, @mid, @mrev, @mid, @mrev, @aid, @arev, @issuer, @actor, repeat('c', 64), transaction_timestamp());
                        INSERT INTO projects_config.model_provider_revisions
                          (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                           display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                           credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
                        SELECT h.provider_id, 2, h.scope, h.project_id, @op, repeat('c', 64),
                               h.display_name, h.type, h.base_url, h.deployment, h.reasoning_efforts, @cfg,
                               @cred, @slot, @secret, h.updated_by_actor_id, h.updated_at
                        FROM projects_config.model_providers h WHERE h.provider_id = @id;
                        """, conn, tx);
                    cmd.Parameters.AddWithValue("id", disabledProviderId);
                    cmd.Parameters.AddWithValue("op", publishOp1);
                    cmd.Parameters.AddWithValue("slot", slot1);
                    cmd.Parameters.AddWithValue("secret", secret1);
                    cmd.Parameters.AddWithValue("cred", credVersion);
                    cmd.Parameters.AddWithValue("cfg", configHash);
                    cmd.Parameters.AddWithValue("tenant", tenantId);
                    cmd.Parameters.AddWithValue("mid", membership.MembershipId);
                    cmd.Parameters.AddWithValue("mrev", membership.Revision);
                    cmd.Parameters.AddWithValue("aid", assignment.AssignmentId);
                    cmd.Parameters.AddWithValue("arev", assignment.Revision);
                    cmd.Parameters.AddWithValue("issuer", caller.Issuer);
                    cmd.Parameters.AddWithValue("actor", actorId);
                    await cmd.ExecuteNonQueryAsync();
                    await tx.CommitAsync();
                }

                await using (var tx = await conn.BeginTransactionAsync())
                {
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE projects_config.model_providers
                        SET current_revision = 3, updated_by_actor_id = @actor, updated_at = transaction_timestamp()
                        WHERE provider_id = @id;
                        INSERT INTO projects_config.model_provider_credential_operations
                          (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                           expected_provider_revision, original_provider_revision, original_credential_revision,
                           credential_slot_id, credential_secret_id, credential_version, configuration_hash,
                           original_tenant, original_membership_id, original_membership_revision,
                           current_membership_id, current_membership_revision, current_assignment_id,
                           current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                        VALUES
                          (@op, 'Disable', @id, 'Platform', NULL, 3, 2, 2, 1, @slot, NULL, NULL, @cfg,
                           @tenant, @mid, @mrev, @mid, @mrev, @aid, @arev, @issuer, @actor, repeat('d', 64), transaction_timestamp());
                        INSERT INTO projects_config.model_provider_revisions
                          (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                           display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                           credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
                        SELECT h.provider_id, 3, h.scope, h.project_id, @op, repeat('d', 64),
                               h.display_name, h.type, h.base_url, h.deployment, h.reasoning_efforts, @cfg,
                               NULL, NULL, NULL, h.updated_by_actor_id, h.updated_at
                        FROM projects_config.model_providers h WHERE h.provider_id = @id;
                        """, conn, tx);
                    cmd.Parameters.AddWithValue("id", disabledProviderId);
                    cmd.Parameters.AddWithValue("op", disableOp1);
                    cmd.Parameters.AddWithValue("slot", slot1);
                    cmd.Parameters.AddWithValue("cfg", configHash);
                    cmd.Parameters.AddWithValue("tenant", tenantId);
                    cmd.Parameters.AddWithValue("mid", membership.MembershipId);
                    cmd.Parameters.AddWithValue("mrev", membership.Revision);
                    cmd.Parameters.AddWithValue("aid", assignment.AssignmentId);
                    cmd.Parameters.AddWithValue("arev", assignment.Revision);
                    cmd.Parameters.AddWithValue("issuer", caller.Issuer);
                    cmd.Parameters.AddWithValue("actor", actorId);
                    await cmd.ExecuteNonQueryAsync();
                    await tx.CommitAsync();
                }

                // Pre-existing Publish on replayProviderId
                await using (var tx = await conn.BeginTransactionAsync())
                {
                    await using var cmd = new NpgsqlCommand("""
                        UPDATE projects_config.model_providers
                        SET current_revision = 2, updated_by_actor_id = @actor, updated_at = transaction_timestamp()
                        WHERE provider_id = @id;
                        INSERT INTO projects_config.model_provider_credential_operations
                          (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                           expected_provider_revision, credential_slot_id, credential_secret_id, credential_version,
                           configuration_hash, original_tenant, original_membership_id, original_membership_revision,
                           current_membership_id, current_membership_revision, current_assignment_id,
                           current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                        VALUES
                          (@op, 'Publish', @id, 'Platform', NULL, 2, 1, @slot, @secret, @cred, @cfg,
                           @tenant, @mid, @mrev, @mid, @mrev, @aid, @arev, @issuer, @actor, @fp, transaction_timestamp());
                        INSERT INTO projects_config.model_provider_revisions
                          (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                           display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                           credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
                        SELECT h.provider_id, 2, h.scope, h.project_id, @op, @fp,
                               h.display_name, h.type, h.base_url, h.deployment, h.reasoning_efforts, @cfg,
                               @cred, @slot, @secret, h.updated_by_actor_id, h.updated_at
                        FROM projects_config.model_providers h WHERE h.provider_id = @id;
                        """, conn, tx);
                    cmd.Parameters.AddWithValue("id", replayProviderId);
                    cmd.Parameters.AddWithValue("op", publishOp2);
                    cmd.Parameters.AddWithValue("slot", slot2);
                    cmd.Parameters.AddWithValue("secret", secret2);
                    cmd.Parameters.AddWithValue("cred", credVersion);
                    cmd.Parameters.AddWithValue("cfg", configHash);
                    cmd.Parameters.AddWithValue("tenant", tenantId);
                    cmd.Parameters.AddWithValue("mid", membership.MembershipId);
                    cmd.Parameters.AddWithValue("mrev", membership.Revision);
                    cmd.Parameters.AddWithValue("aid", assignment.AssignmentId);
                    cmd.Parameters.AddWithValue("arev", assignment.Revision);
                    cmd.Parameters.AddWithValue("issuer", caller.Issuer);
                    cmd.Parameters.AddWithValue("actor", actorId);
                    cmd.Parameters.AddWithValue("fp", replayFingerprint);
                    await cmd.ExecuteNonQueryAsync();
                    await tx.CommitAsync();
                }
            }

            // Upgrade to VerifyProviderAvailabilityOnPublication
            var migrationOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
                .Options;
            await ProjectsConfigMigrator.MigrateAsync(isolated.MigrationDataSource, migrationOptions);
            await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(isolated.DataSource, migrationOptions);
            var runtimeConnectionString = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
            }.ConnectionString;
            await using (var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString))
                await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtimeDataSource);

            // Pre-existing rows remain unchanged (provider_state IS NULL).
            await using (var conn = await isolated.DataSource.OpenConnectionAsync())
            await using (var check = new NpgsqlCommand("""
                SELECT count(*) FROM projects_config.model_provider_credential_operations
                WHERE operation_id = ANY(@ops) AND provider_state IS NULL
                """, conn))
            {
                check.Parameters.AddWithValue("ops", new[] { publishOp1, disableOp1, publishOp2 });
                Assert.Equal(3L, (long)(await check.ExecuteScalarAsync())!);
            }

            // Replay pre-existing Publish receipt -> returns ProviderState = "Draft".
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System);
            var replayPreflight = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    publishOp2, ModelProviderBindingScope.Platform, null, replayProviderId,
                    caller.Issuer, actorId, tenantId, null, null, "authority-only"),
                CancellationToken.None);
            Assert.NotNull(replayPreflight.ExistingReceipt);
            Assert.Equal("Draft", replayPreflight.ExistingReceipt!.ProviderState);

            var replayReceipt = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    publishOp2, replayPreflight.PreflightId, 1, ModelProviderBindingScope.Platform, null, replayProviderId,
                    credVersion, new ModelCredentialIdentity(slot2, ModelProviderBindingScope.Platform, null, replayProviderId),
                    new SecretRef(secret2, credVersion), caller.Issuer, actorId, tenantId,
                    replayPreflight.MembershipId, replayPreflight.MembershipRevision, tenantId, null),
                CancellationToken.None);
            Assert.Equal("Draft", replayReceipt.ProviderState);
            Assert.Equal(2, replayReceipt.ProviderRevision);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task DowngradeFromAvailability_WithoutAvailablePins_RestoresPreAvailabilityGuardAndAllowsPublishAndDisable()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        try
        {
            await isolated.InitializeToAsync(null);

            await using var db = new ProjectsConfigDbContext(
                new DbContextOptionsBuilder<ProjectsConfigDbContext>().UseNpgsql(isolated.DataSource).Options);
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                new DbContextOptionsBuilder<ProjectsConfigDbContext>().UseNpgsql(isolated.DataSource).Options,
                TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "dng-tenant-" + suffix;
            var actorId = "dng-admin-" + suffix;
            var caller = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "dng-prov-" + suffix;
            await draftService.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Downgrade Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"]),
                CancellationToken.None);

            // Publish without pricing -> Draft (no Available pins).
            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System);
            var op1 = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var credVersion = new string('e', 32);
            var pre1 = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    op1, ModelProviderBindingScope.Platform, null, providerId,
                    caller.Issuer, actorId, tenantId, null, null, "draft-snapshot"),
                CancellationToken.None);
            var rec1 = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    op1, pre1.PreflightId, 1, ModelProviderBindingScope.Platform, null, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Platform, null, providerId),
                    new SecretRef($"byok-platform-{providerId}", credVersion), caller.Issuer, actorId, tenantId,
                    pre1.MembershipId, pre1.MembershipRevision, tenantId, null),
                CancellationToken.None);
            Assert.Equal("Draft", rec1.ProviderState);

            // Downgrade to pre-availability migration.
            var migrationOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql =>
                    npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
                .Options;
            await using (var migCtx = new ProjectsConfigDbContext(migrationOptions))
            {
                await migCtx.GetService<IMigrator>()
                    .MigrateAsync("20261120231128_AddModelProviderCredentialOperationsV2");
            }

            // Columns removed and restored guard allows actual Publish and Disable.
            await using var conn = await isolated.DataSource.OpenConnectionAsync();
            await using (var colCheck = new NpgsqlCommand("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'projects_config'
                  AND table_name = 'model_provider_credential_operations'
                  AND column_name IN ('provider_state', 'rate_card_id', 'rate_card_version', 'rate_card_pin_hash')
                """, conn))
            {
                Assert.Equal(0L, (long)(await colCheck.ExecuteScalarAsync())!);
            }

            var membership = await db.TenantMemberships.AsNoTracking()
                .SingleAsync(m => m.Issuer == caller.Issuer && m.Subject == actorId && m.TenantId == tenantId);
            var assignment = await db.RoleAssignments.AsNoTracking()
                .SingleAsync(a => a.MembershipId == membership.MembershipId && a.Role == ProjectAuthorityRole.PlatformAdmin);
            var rotatedVersion = new string('f', 32);
            var rotatedSlot = Guid.NewGuid();
            var configHash = rec1.ConfigurationHash;

            // Actual Publish under restored guard (rev 3)
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using var cmd = new NpgsqlCommand("""
                    SET LOCAL ROLE "agentweaver-id-projects-config";
                    UPDATE projects_config.model_providers
                    SET current_revision = 3, updated_by_actor_id = @actor, updated_at = transaction_timestamp()
                    WHERE provider_id = @id;
                    INSERT INTO projects_config.model_provider_credential_operations
                      (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                       expected_provider_revision, credential_slot_id, credential_secret_id, credential_version,
                       configuration_hash, original_tenant, original_membership_id, original_membership_revision,
                       current_membership_id, current_membership_revision, current_assignment_id,
                       current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                    VALUES
                      (@op, 'Publish', @id, 'Platform', NULL, 3, 2, @slot, @secret, @cred, @cfg,
                       @tenant, @mid, @mrev, @mid, @mrev, @aid, @arev, @issuer, @actor, repeat('1', 64), transaction_timestamp());
                    INSERT INTO projects_config.model_provider_revisions
                      (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                       display_name, type, base_url, deployment, wire_api, reasoning_efforts, configuration_hash,
                       credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
                    SELECT h.provider_id, 3, h.scope, h.project_id, @op, repeat('1', 64),
                           h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.reasoning_efforts, @cfg,
                           @cred, @slot, @secret, h.updated_by_actor_id, h.updated_at
                    FROM projects_config.model_providers h WHERE h.provider_id = @id;
                    """, conn, tx);
                cmd.Parameters.AddWithValue("id", providerId);
                cmd.Parameters.AddWithValue("op", Guid.NewGuid());
                cmd.Parameters.AddWithValue("slot", rotatedSlot);
                cmd.Parameters.AddWithValue("secret", $"byok-platform-{providerId}");
                cmd.Parameters.AddWithValue("cred", rotatedVersion);
                cmd.Parameters.AddWithValue("cfg", configHash);
                cmd.Parameters.AddWithValue("tenant", tenantId);
                cmd.Parameters.AddWithValue("mid", membership.MembershipId);
                cmd.Parameters.AddWithValue("mrev", membership.Revision);
                cmd.Parameters.AddWithValue("aid", assignment.AssignmentId);
                cmd.Parameters.AddWithValue("arev", assignment.Revision);
                cmd.Parameters.AddWithValue("issuer", caller.Issuer);
                cmd.Parameters.AddWithValue("actor", actorId);
                await cmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }

            // Actual Disable under restored guard (rev 4)
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await using var cmd = new NpgsqlCommand("""
                    SET LOCAL ROLE "agentweaver-id-projects-config";
                    UPDATE projects_config.model_providers
                    SET current_revision = 4, updated_by_actor_id = @actor, updated_at = transaction_timestamp()
                    WHERE provider_id = @id;
                    INSERT INTO projects_config.model_provider_credential_operations
                      (operation_id, operation_kind, provider_id, scope, project_id, provider_revision,
                       expected_provider_revision, original_provider_revision, original_credential_revision,
                       credential_slot_id, credential_secret_id, credential_version, configuration_hash,
                       original_tenant, original_membership_id, original_membership_revision,
                       current_membership_id, current_membership_revision, current_assignment_id,
                       current_assignment_revision, issuer, actor_id, request_fingerprint, recorded_at)
                    VALUES
                      (@op, 'Disable', @id, 'Platform', NULL, 4, 3, 3, 2, @slot, NULL, NULL, @cfg,
                       @tenant, @mid, @mrev, @mid, @mrev, @aid, @arev, @issuer, @actor, repeat('2', 64), transaction_timestamp());
                    INSERT INTO projects_config.model_provider_revisions
                      (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                       display_name, type, base_url, deployment, wire_api, reasoning_efforts, configuration_hash,
                       credential_version, credential_slot_id, credential_secret_id, created_by_actor_id, created_at)
                    SELECT h.provider_id, 4, h.scope, h.project_id, @op, repeat('2', 64),
                           h.display_name, h.type, h.base_url, h.deployment, h.wire_api, h.reasoning_efforts, @cfg,
                           NULL, NULL, NULL, h.updated_by_actor_id, h.updated_at
                    FROM projects_config.model_providers h WHERE h.provider_id = @id;
                    """, conn, tx);
                cmd.Parameters.AddWithValue("id", providerId);
                cmd.Parameters.AddWithValue("op", Guid.NewGuid());
                cmd.Parameters.AddWithValue("slot", rotatedSlot);
                cmd.Parameters.AddWithValue("cfg", configHash);
                cmd.Parameters.AddWithValue("tenant", tenantId);
                cmd.Parameters.AddWithValue("mid", membership.MembershipId);
                cmd.Parameters.AddWithValue("mrev", membership.Revision);
                cmd.Parameters.AddWithValue("aid", assignment.AssignmentId);
                cmd.Parameters.AddWithValue("arev", assignment.Revision);
                cmd.Parameters.AddWithValue("issuer", caller.Issuer);
                cmd.Parameters.AddWithValue("actor", actorId);
                await cmd.ExecuteNonQueryAsync();
                await tx.CommitAsync();
            }

            await using var revCheck = new NpgsqlCommand(
                "SELECT current_revision, state FROM projects_config.model_providers WHERE provider_id = @id", conn);
            revCheck.Parameters.AddWithValue("id", providerId);
            await using var reader = await revCheck.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(4L, reader.GetInt64(0));
            Assert.Equal("Draft", reader.GetString(1));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConcurrentDisableAndSettleOnlyProduceExactlyOneTerminalOutcome()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var configService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenantId = "race-tenant-" + suffix;
            var actorId = "race-owner-" + suffix;
            var admin = await SeedCallerAsync(db, store, "race-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, actorId, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await configService.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            _ = await configService.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await configService.CreateProjectAsync(owner, "Race Project " + suffix, CancellationToken.None);
            var caller = await ResolveCallerAsync(
                db, actorId, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);

            var draftService = new ModelProviderDraftService(db, TimeProvider.System);
            var providerId = "race-prov-" + suffix;
            await draftService.SaveAsync(caller, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                new SaveModelProviderDraftRequest(
                    Guid.NewGuid(), 0, "Race Provider", "openai", "https://api.openai.com/v1", "gpt-5",
                    WireApi: "responses", ReasoningEfforts: ["high"]),
                CancellationToken.None);

            var pubService = new ModelProviderCredentialPublicationV2Service(db, TimeProvider.System);
            var opPub = Guid.NewGuid();
            var slotId = Guid.NewGuid();
            var credVersion = new string('a', 32);
            var prePub = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opPub, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    caller.Issuer, actorId, tenantId, null, project.ProjectId, "draft-snapshot"),
                CancellationToken.None);
            _ = await pubService.PublishAsync(
                new ModelProviderCredentialPublicationV2Request(
                    opPub, prePub.PreflightId, 1, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    credVersion, new ModelCredentialIdentity(slotId, ModelProviderBindingScope.Project, project.ProjectId, providerId),
                    new SecretRef($"byok-project-{providerId}", credVersion), caller.Issuer, actorId, tenantId,
                    prePub.MembershipId, prePub.MembershipRevision, tenantId, null),
                CancellationToken.None);

            var opDisable = Guid.NewGuid();
            var preDisable = await pubService.PreflightAsync(
                new ModelProviderCredentialPublicationPreflightRequest(
                    opDisable, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                    caller.Issuer, actorId, tenantId, null, project.ProjectId, "draft-snapshot"),
                CancellationToken.None);

            var disableTask = Task.Run(async () =>
            {
                await using var db1 = tests.CreateDbContext();
                var svc1 = new ModelProviderCredentialPublicationV2Service(db1, TimeProvider.System);
                try
                {
                    var receipt = await svc1.DisableAsync(
                        new ModelProviderCredentialDisableRequest(
                            opDisable, 2, 1, slotId, credVersion,
                            ModelProviderBindingScope.Project, project.ProjectId, providerId,
                            caller.Issuer, actorId, tenantId, null, preDisable.PreflightId),
                        CancellationToken.None);
                    return (Outcome: "disabled", Receipt: (ModelProviderCredentialDisableReceipt?)receipt);
                }
                catch (ModelProviderCredentialPublicationV2Exception ex) when (ex.Code == "credential_operation_cancelled")
                {
                    return (Outcome: "cancelled", Receipt: (ModelProviderCredentialDisableReceipt?)null);
                }
            });

            var settleTask = Task.Run(async () =>
            {
                await using var db2 = tests.CreateDbContext();
                var svc2 = new ModelProviderCredentialPublicationV2Service(db2, TimeProvider.System);
                return await svc2.CancelAsync(
                    new ModelProviderCredentialCancelRequest(
                        opDisable, preDisable.PreflightId, ModelProviderBindingScope.Project, project.ProjectId, providerId,
                        caller.Issuer, actorId, null, null, null, null, null, SettleOnly: true),
                    CancellationToken.None);
            });

            var disableResult = await disableTask;
            var settleResult = await settleTask;

            var opRow = await db.ModelProviderCredentialOperations.AsNoTracking()
                .SingleAsync(op => op.OperationId == opDisable);
            if (opRow.OperationKind == ModelProviderCredentialOperationKind.Disable)
            {
                Assert.Equal("disabled", disableResult.Outcome);
                Assert.Equal("disabled", settleResult.Outcome);
                Assert.NotNull(settleResult.DisableReceipt);
                Assert.Equal(3L, settleResult.DisableReceipt!.ProviderRevision);
            }
            else
            {
                Assert.Equal(ModelProviderCredentialOperationKind.Cancel, opRow.OperationKind);
                Assert.Equal("cancelled", disableResult.Outcome);
                Assert.Equal("cancelled", settleResult.Outcome);
                Assert.Null(settleResult.DisableReceipt);

                var delayedEx = await Assert.ThrowsAsync<ModelProviderCredentialPublicationV2Exception>(() =>
                    pubService.DisableAsync(
                        new ModelProviderCredentialDisableRequest(
                            opDisable, 2, 1, slotId, credVersion,
                            ModelProviderBindingScope.Project, project.ProjectId, providerId,
                            caller.Issuer, actorId, tenantId, null, preDisable.PreflightId),
                        CancellationToken.None));
                Assert.Equal("credential_operation_cancelled", delayedEx.Code);
            }
        });
    }

    private sealed class DelegateRateCardHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
