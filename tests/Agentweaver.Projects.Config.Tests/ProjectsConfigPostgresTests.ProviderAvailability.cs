using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Microsoft.EntityFrameworkCore;
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

    private sealed class DelegateRateCardHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
