using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeAgentHostTests
{
    [Fact]
    public async Task OrdinarySessionCacheWritesCannotBypassTheGuardedSuspendPath()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var material = fixture.MaterialClient();
        var source = fixture.SourceClient();
        await using var session = await fixture.ConfigureSessionAsync(material, timeout.Token);
        await session.RegisterUsageAsync(source, timeout.Token);
        var turnReceipt = await session.CommitTurnContentAsync(
            material, Guid.NewGuid(), "user", "A normal user turn.", timeout.Token);
        Assert.Null(turnReceipt.Reference.Material!.RemoteToolResultProof);
        var proof = new RuntimeHostSessionProof(1, session.Registration, session.SourceGrant.GrantId,
            session.SourceGrant.Revision, RuntimeCredentialPurpose.Observe);
        await session.SendNativeTurnAsync(fixture.Message(proof), material, source, timeout.Token);
        await session.CommitNativeCacheAsync(material, Guid.NewGuid(), timeout.Token);
        Assert.All(fixture.Material.Where(item => item.Kind == SessionMaterialKind.SdkCache),
            item => Assert.Null(item.RemoteToolResultProof));
        Assert.Equal(2, fixture.Material.Count(item => item.Kind == SessionMaterialKind.SdkCache));
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
        var receipt = await session.SuspendAsync(
            material, request, token => fixture.RequireCurrentSuspendAsync(request, token), timeout.Token);
        Assert.Equal(3, fixture.Material.Count(item => item.Kind == SessionMaterialKind.SdkCache));
        var writes = fixture.Material.Count;

        Assert.Equal("runtime_session_suspended",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                session.CommitNativeCacheAsync(material, Guid.NewGuid(), timeout.Token))).Code);
        Assert.Equal(writes, fixture.Material.Count);
        Assert.Equal(receipt, await session.SuspendAsync(
            material, request, token => fixture.RequireCurrentSuspendAsync(request, token), timeout.Token));
        Assert.Equal(writes, fixture.Material.Count);
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
    }

    [Fact]
    public async Task NativeSuspendWaitsForTheAdmittedTurnAndReplaysTheExactDurableCacheReceipt()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sdk.BeforeTurnResponse = token => release.Task.WaitAsync(token);
        var message = fixture.Message(proof);
        var turn = fixture.Host.SendAsync(message, fixture.Actor, timeout.Token);
        await fixture.Sdk.TurnReceived.Task.WaitAsync(timeout.Token);
        var request = fixture.SuspendRequest(proof);
        var suspend = fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token);
        Assert.False(suspend.IsCompleted);
        Assert.DoesNotContain(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);

        release.TrySetResult();
        var receipt = await suspend;
        var answer = await turn;
        Assert.Equal(request.OperationId, receipt.OperationId);
        Assert.Equal(request.ManifestId, receipt.ManifestId);
        Assert.Equal(fixture.Registration, receipt.Registration);
        Assert.Equal(request.ManifestId, receipt.CacheAcknowledgment.EventId);
        Assert.Equal(SessionMaterialKind.SdkCache, receipt.CacheAcknowledgment.Reference.Material!.Kind);
        var configuration = receipt.ConfigurationAcknowledgment
            ?? throw new InvalidOperationException("The suspend receipt must carry its saved configuration.");
        Assert.Equal(fixture.Configure.ConsumeOperationId, configuration.EventId);
        Assert.Equal(SessionMaterialKind.RuntimeConfiguration, configuration.Reference.Material!.Kind);
        Assert.Equal(fixture.ConfigurationHash, configuration.Reference.Material.Sha256);
        Assert.Equal(message.Message.MessageId, receipt.NativeTurn.Admission.MessageId);
        Assert.Equal(fixture.Sdk.NativeCompletionReceiptEventId,
            receipt.NativeTurn.Observation.NativeCompletionReceiptEventId);
        Assert.Equal(receipt, await fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token));
        Assert.Equal(answer, await fixture.Host.SendAsync(message, fixture.Actor, timeout.Token));
        Assert.Equal(2, fixture.Material.Count(material => material.Kind == SessionMaterialKind.SdkCache));
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
        Assert.DoesNotContain(fixture.Sdk.Requests, rpc => rpc.Method is "session.abort" or "session.detach");
        Assert.Equal("runtime_session_suspended",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token))).Code);
        Assert.Equal("runtime_suspend_operation_conflict",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request with { ManifestId = Guid.NewGuid() },
                    fixture.Actor, timeout.Token))).Code);
        Assert.Equal("runtime_suspend_operation_conflict",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request with { PhaseVersion = 2 },
                    fixture.Actor, timeout.Token))).Code);
        fixture.Registration = fixture.Registration with { Revision = 2 };
        Assert.Equal("runtime_registration_stale",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
    }

    [Fact]
    public async Task RuntimeConfigurationUsesDedicatedWriteRouteAndGenericReadsStayDenied()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.ConfigureAsync(timeout.Token);

        var saved = Assert.Single(
            fixture.Material, material => material.Kind == SessionMaterialKind.RuntimeConfiguration);
        Assert.Equal(fixture.Configure.ConsumeOperationId, saved.EventId);
        Assert.Equal(
            $"/internal/sessions/{fixture.Registration.Binding.SessionId}/runtime-configuration",
            Assert.Single(fixture.MaterialWriteRoutes));

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.MaterialClient().ReadRecordedAsync(
                new(fixture.Registration.Binding.ProjectId, fixture.Registration.Binding.RunId,
                    fixture.Registration.Binding.SessionId),
                saved.EventId, SessionMaterialKind.RuntimeConfiguration, timeout.Token));
        Assert.Equal("runtime_configuration_route_unavailable", error.Code);
    }

    [Fact]
    public async Task ResumeRecoveryReadsOnlyTheExactManifestCacheAcknowledgment()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var material = fixture.MaterialClient();
        await using var session = await fixture.ConfigureSessionAsync(material, timeout.Token);
        await session.RegisterUsageAsync(fixture.SourceClient(), timeout.Token);
        var nativeFile = await fixture.Sdk.InvokeNativeFilesAsync(
            "sessionFs.writeFile", "state/events.jsonl", "opaque native events\n", timeout.Token);
        Assert.Equal(JsonValueKind.Null, nativeFile.ValueKind);
        var acknowledgment = await session.CommitNativeCacheAsync(material, Guid.NewGuid(), timeout.Token);

        var recovery = await material.ReadRecoveryAsync(session.Registration, timeout.Token, acknowledgment);

        Assert.Equal(acknowledgment, recovery.Cache?.Material);
        var changed = acknowledgment with { EventId = Guid.NewGuid() };
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            material.ReadRecoveryAsync(session.Registration, timeout.Token, changed));
    }

    [Fact]
    public async Task ResumeWithCompatibleCacheUsesNativeResumeWithoutReadingJournal()
    {
        await using var source = new HostFixture(workflowBound: true);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await PrepareResumeTargetAsync(source, target, timeout.Token);
        target.MissingMaterialId = null;

        var readiness = await target.ConfigureAsync(timeout.Token);

        Assert.Equal("native-cache", readiness.ResumeCache?.RecoveryMode);
        var cache = request.Resume.SourceManifest.CacheAcknowledgment!;
        Assert.Equal(cache.EventId, Assert.Single(target.ResumeCacheReads));
        var resume = Assert.Single(target.Sdk.Requests, item => item.Method == "session.resume");
        Assert.Equal(RuntimeContractValidation.NativeSessionId(request.Registration.Binding),
            resume.Parameters.GetProperty("sessionId").GetString());
        Assert.DoesNotContain(target.Sdk.Requests, item => item.Method == "session.create");
        Assert.Empty(target.ResumeEventRequests);
        Assert.DoesNotContain(target.Material, item => item.Kind == SessionMaterialKind.SdkCache);
    }

    [Fact]
    public async Task AuthenticatedIncompatibleCacheFallsBackToJournalWithoutNativeResume()
    {
        await using var source = new HostFixture(workflowBound: true);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await PrepareResumeTargetAsync(source, target, timeout.Token);
        target.MissingMaterialId = null;
        target.Sdk.RuntimeVersion = "controlled-runtime-v2";

        var readiness = await target.ConfigureAsync(timeout.Token);

        Assert.Equal("journal-rebuild", readiness.ResumeCache?.RecoveryMode);
        Assert.NotNull(readiness.ResumeCache?.JournalRebuild);
        Assert.Equal(request.Resume.SourceManifest.CacheAcknowledgment!.EventId,
            Assert.Single(target.ResumeCacheReads));
        AssertResumeEventsUseSourceSession(target, request);
        Assert.Single(target.Sdk.Requests, item => item.Method == "session.create");
        Assert.DoesNotContain(target.Sdk.Requests, item => item.Method == "session.resume");
        Assert.DoesNotContain(target.Material, item => item.Kind == SessionMaterialKind.SdkCache);
    }

    [Fact]
    public async Task ResumeRebuildsFromTheSavedVisibleJournalAndWaitsForAnAuthorizedTurnToWriteCache()
    {
        await using var source = new HostFixture(workflowBound: true);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await PrepareResumeTargetAsync(source, target, timeout.Token);
        var sourceCache = request.Resume.SourceManifest.CacheAcknowledgment!;

        var readiness = await target.ConfigureAsync(timeout.Token);
        var receipt = readiness.ResumeCache
            ?? throw new InvalidOperationException("Resume readiness must include the recovery receipt.");
        Assert.Equal("journal-rebuild", receipt.RecoveryMode);
        Assert.Equal(sourceCache, receipt.CacheAcknowledgment);
        Assert.Equal(request.Resume.SourceManifest.FlushedJournalPosition, receipt.JournalRebuild!.ThroughPosition);
        Assert.Equal(RuntimeResumeNativeCacheState.PendingUntilAuthorizedTurn,
            receipt.JournalRebuild.NativeCacheState);
        var savedPrefix = source.SessionEvents
            .Where(item => item.Position <= receipt.JournalRebuild.ThroughPosition)
            .ToImmutableArray();
        Assert.Contains(savedPrefix, item => item.Identity.SessionId == "visible-parent");
        Assert.DoesNotContain(source.SessionEvents, item =>
            item.Position <= receipt.JournalRebuild.ThroughPosition &&
            item.Payload is AcceptedEffectSessionPayload { EffectId: "post-boundary-effect" });
        Assert.Equal(
            RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(request, savedPrefix),
            receipt.JournalRebuild.JournalSha256);
        var create = Assert.Single(target.Sdk.Requests, item => item.Method == "session.create");
        Assert.DoesNotContain(target.Sdk.Requests, item => item.Method == "session.resume");
        Assert.Contains("Recorded conversation context", create.Parameters.GetRawText());
        Assert.Contains("A bounded user request.", create.Parameters.GetRawText());
        Assert.DoesNotContain(target.Material, item => item.Kind == SessionMaterialKind.SdkCache);

        await target.Host.SendAsync(
            target.Message(target.Proof(readiness)), target.Actor, timeout.Token);

        var writtenCache = Assert.Single(target.Material, item => item.Kind == SessionMaterialKind.SdkCache);
        Assert.NotEqual(sourceCache.EventId, writtenCache.EventId);
        Assert.NotEmpty(writtenCache.Bytes);
    }

    [Fact]
    public async Task ResumeJournalAcceptsATurnFromItsPriorRuntimeRegistration()
    {
        await using var prior = new HostFixture(workflowBound: true);
        await using var source = new HostFixture(workflowBound: true, executionFence: 2);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var priorEventId = await prior.RecordOneTurnAsync(timeout.Token);
        var request = await PrepareResumeTargetAsync(
            source, target, timeout.Token, priorTurnOwner: prior);
        var priorTurn = prior.ReadMaterial(priorEventId);
        var priorBinding = priorTurn.Material.Reference.Material!;
        var latestCacheBinding = request.Resume.SourceManifest.CacheAcknowledgment!.Reference.Material!;

        Assert.Equal(prior.Registration.RuntimeInstanceId, priorBinding.RuntimeInstanceId);
        Assert.Equal(prior.Registration.Revision, priorBinding.RegistrationRevision);
        Assert.Equal(prior.Registration.Binding.ExecutionFence, priorBinding.ExecutionFence);
        Assert.NotEqual(latestCacheBinding.RuntimeInstanceId, priorBinding.RuntimeInstanceId);
        Assert.NotEqual(latestCacheBinding.ExecutionFence, priorBinding.ExecutionFence);

        var readiness = await target.ConfigureAsync(timeout.Token);

        Assert.Equal("journal-rebuild", readiness.ResumeCache?.RecoveryMode);
        Assert.Contains(
            $"/internal/sessions/{Uri.EscapeDataString(prior.Registration.Binding.SessionId)}/material/{priorEventId:D}/TurnContent",
            target.ResumeMaterialReadRequests);
        AssertResumeEventsUseSourceSession(target, request);
    }

    [Fact]
    public async Task ResumeJournalReadsVisibleAncestorTurnButNotNonVisibleSameRunTurn()
    {
        await using var parent = new HostFixture(workflowBound: true, sessionId: "visible-parent");
        await using var nonVisible = new HostFixture(workflowBound: true, sessionId: "not-visible");
        await using var source = new HostFixture(workflowBound: true, executionFence: 2);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var parentEventId = await parent.RecordOneTurnAsync(timeout.Token);
        var nonVisibleEventId = await nonVisible.RecordOneTurnAsync(timeout.Token);
        var request = await PrepareResumeTargetAsync(
            source, target, timeout.Token, visibleParentOwner: parent, nonVisibleOwner: nonVisible);
        var throughPosition = request.Resume.SourceManifest.FlushedJournalPosition
            ?? throw new InvalidOperationException("Resume must preserve the flushed journal position.");
        var parentEvent = Assert.Single(source.SessionEvents, item => item.EventId == parentEventId);
        var parentBinding = parent.ReadMaterial(parentEventId).Material.Reference.Material!;

        Assert.Equal("visible-parent", parentEvent.Identity.SessionId);
        Assert.IsType<TurnSessionPayload>(parentEvent.Payload);
        Assert.True(parentEvent.Position <= throughPosition);
        Assert.Equal(parent.Registration.RuntimeInstanceId, parentBinding.RuntimeInstanceId);
        Assert.Equal(parent.Registration.Revision, parentBinding.RegistrationRevision);
        Assert.Equal(parent.Registration.Binding.ExecutionFence, parentBinding.ExecutionFence);
        Assert.True(source.HasMaterial(nonVisibleEventId));
        Assert.DoesNotContain(source.SessionEvents, item => item.EventId == nonVisibleEventId);
        Assert.DoesNotContain(source.SessionEvents, item => item.Identity.SessionId == "not-visible");

        var readiness = await target.ConfigureAsync(timeout.Token);

        Assert.Equal("journal-rebuild", readiness.ResumeCache?.RecoveryMode);
        Assert.Contains(
            $"/internal/sessions/visible-parent/material/{parentEventId:D}/TurnContent",
            target.ResumeMaterialReadRequests);
        Assert.DoesNotContain(
            $"/internal/sessions/not-visible/material/{nonVisibleEventId:D}/TurnContent",
            target.ResumeMaterialReadRequests);
        AssertResumeEventsUseSourceSession(target, request);
    }

    [Theory]
    [InlineData("events-unauthorized", "runtime_owner_denied")]
    [InlineData("events-forbidden", "runtime_owner_denied")]
    [InlineData("events-transport", "transport")]
    [InlineData("cursor-repeated", "runtime_journal_cursor_invalid")]
    [InlineData("cursor-no-progress", "runtime_journal_cursor_invalid")]
    [InlineData("material-bytes", "runtime_material_read_invalid")]
    [InlineData("material-reference", "runtime_journal_turn_invalid")]
    [InlineData("material-hash", "runtime_material_read_invalid")]
    [InlineData("material-provenance", "runtime_journal_turn_invalid")]
    public async Task ResumeJournalProducerFailuresPreventReadinessAndNativeSessionCreation(
        string fault, string expectedFailure)
    {
        await using var source = new HostFixture(workflowBound: true);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await PrepareResumeTargetAsync(source, target, timeout.Token);
        target.ResumeFault = fault;
        if (fault == "material-provenance")
            target.ResumeProvenanceRuntimeInstanceId = Guid.NewGuid();

        var failure = await Record.ExceptionAsync(() => target.ConfigureAsync(timeout.Token));

        if (expectedFailure == "transport")
            Assert.IsType<HttpRequestException>(failure);
        else
            Assert.Equal(expectedFailure, Assert.IsType<RuntimeAuthorizationException>(failure).Code);

        var cache = request.Resume.SourceManifest.CacheAcknowledgment!;
        Assert.Equal(cache.EventId, Assert.Single(target.ResumeCacheReads));
        AssertResumeEventsUseSourceSession(target, request);
        Assert.DoesNotContain(target.Material, item => item.Kind == SessionMaterialKind.SdkCache);
        Assert.DoesNotContain(target.Sdk.Requests,
            item => item.Method is "session.create" or "session.resume" or "session.send");
        var readinessFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(
            () => target.Host.ReadinessAsync(timeout.Token));
        Assert.Equal("runtime_host_not_ready", readinessFailure.Code);
    }

    private static async Task<RuntimeResumeBootstrapRequest> PrepareResumeTargetAsync(
        HostFixture source, HostFixture target, CancellationToken token,
        HostFixture? priorTurnOwner = null,
        HostFixture? visibleParentOwner = null,
        HostFixture? nonVisibleOwner = null)
    {
        source.PositionIncrement = 2;
        var request = await CreateResumeBootstrapRequestAsync(source, target, token);
        source.PrepareResumeJournal(request.Resume.SourceRelease.HostReceipt, includeVisibleAncestor: true);
        if (priorTurnOwner is not null)
            source.ReplaceFirstTurnFrom(priorTurnOwner);
        if (visibleParentOwner is not null)
            source.ReplaceVisibleParentEffectWithTurn(visibleParentOwner);
        if (nonVisibleOwner is not null)
            source.ImportUnlistedMaterialFrom(nonVisibleOwner, nonVisibleOwner.FirstTurnEventId);
        var cache = request.Resume.SourceManifest.CacheAcknowledgment!;
        target.UseResumeBootstrap(request);
        var resumedPlacement = request.Resume.RestoredWorkspace!.Placement;
        target.Readiness = source.Readiness with
        {
            Placement = resumedPlacement with
            {
                RuntimeOwnerContext = resumedPlacement.RuntimeOwnerContext! with
                {
                    ExecutionFence = request.Registration.Binding.ExecutionFence
                }
            },
            Observation = source.Readiness.Observation with
            {
                Resource = resumedPlacement.Resource,
                FencingGeneration = resumedPlacement.ProviderFencingGeneration
            },
            WorkspaceMountPath = target.WorkingDirectory
        };
        target.ImportResumeSourceFrom(source, cache.EventId);
        target.SessionEventPageSize = 2;
        return request;
    }

    private static void AssertResumeEventsUseSourceSession(
        HostFixture target, RuntimeResumeBootstrapRequest request)
    {
        var expectedPrefix =
            $"/internal/sessions/{Uri.EscapeDataString(request.Resume.SourceManifest.Identity.SessionId)}/events";
        Assert.NotEmpty(target.ResumeEventRequests);
        Assert.All(target.ResumeEventRequests, path =>
            Assert.True(path.StartsWith(expectedPrefix, StringComparison.Ordinal),
                "Journal fallback must stay bound to the saved source session."));
    }

    [Fact]
    public async Task WorkspaceResumeVerifierChecksCoreBeforeAndAfterHashingTheMount()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var suspend = fixture.SuspendRequest(proof);
        var hostReceipt = await fixture.Host.SuspendAsync(suspend, fixture.Actor, timeout.Token);
        Directory.CreateDirectory(fixture.WorkingDirectory);
        var treeHash = await MountedWorkspaceFlush.ComputeTreeSha256Async(
            fixture.WorkingDirectory, timeout.Token);
        var flush = hostReceipt.WorkspaceFlush! with { TreeSha256 = treeHash };
        hostReceipt = hostReceipt with { WorkspaceFlush = flush };
        var identity = new SessionIdentity(
            proof.Registration.Binding.ProjectId,
            proof.Registration.Binding.RunId,
            proof.Registration.Binding.SessionId);
        var manifest = new SessionSuspendResumeManifest(
            1,
            hostReceipt.ManifestId,
            identity,
            SessionSuspendResumeManifestState.Suspended,
            flush.EnvironmentFence,
            proof.Registration.Binding.ExecutionFence,
            hostReceipt.CacheAcknowledgment,
            new(
                identity,
                SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
                "checkpoint",
                1,
                "plan",
                1),
            hostReceipt.CacheAcknowledgment.Position,
            flush.Volume,
            flush.Resource,
            flush.DataGeneration,
            treeHash,
            null,
            null,
            null,
            1,
            ImmutableArray<string>.Empty).Validate();
        var release = new RuntimeSuspendPlacementReleaseRequest(hostReceipt, 3).Validate();
        var request = new RuntimeWorkspaceResumeRequest(
            1, Guid.NewGuid(), 3, manifest, release)
        {
            RestoredWorkspace = CreateResumeWorkspace(fixture.Readiness.Placement, flush, release)
        };

        var receipt = await fixture.Host.VerifyWorkspaceResumeAsync(request, fixture.Actor, timeout.Token);

        Assert.Equal(treeHash, receipt.ActualTreeSha256);
        Assert.Equal(request.RestoredWorkspace, receipt.Workspace);
        Assert.Equal(2, fixture.ResumeChecks.Count);
        Assert.All(fixture.ResumeChecks, check => Assert.Equal(request, check));
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
        Assert.Single(fixture.NativeAdmissions);
    }

    private static EnvironmentRuntimeWorkspaceContext CreateResumeWorkspace(
        EnvironmentRuntimeBootstrapContext sourcePlacement,
        WorkspaceVolumeFlushReceipt flush,
        RuntimeSuspendPlacementReleaseRequest release)
    {
        var sandbox = new ProviderResourceRef(
            ProviderSeam.Sandbox,
            sourcePlacement.Resource.ProviderId,
            sourcePlacement.Resource.ResourceId + "-resumed",
            checked(sourcePlacement.Resource.Generation + 1));
        var fence = new EnvironmentGenerationFence(
            flush.EnvironmentFence.Owner,
            checked(flush.EnvironmentFence.LifecycleGeneration + 1));
        var fencingGeneration = checked(sourcePlacement.ProviderFencingGeneration + 1);
        var placement = sourcePlacement with
        {
            LifecycleGeneration = fence.LifecycleGeneration,
            CurrentFencingGeneration = fencingGeneration,
            ProviderFencingGeneration = fencingGeneration,
            LeaseRevision = checked(sourcePlacement.LeaseRevision + 1),
            Resource = sandbox
        };
        var workspace = new WorkspaceVolumeAttachmentNegotiation(
            flush.Volume.ProjectId,
            sourcePlacement.EnvironmentId,
            sourcePlacement.RunId,
            flush.Volume,
            sandbox,
            flush.Resource,
            fence,
            flush.DataGeneration,
            "/workspace/agentweaver/workspace",
            false,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        return new(1, placement, workspace, checked(release.FlushTransitionRevision + 2));
    }

    [Fact]
    public async Task ResumeStateProbeRequiresSignedAuthorityAndNeverReportsPresentStateAsMissing()
    {
        await using var source = new HostFixture(workflowBound: true);
        await using var target = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = await CreateResumeBootstrapRequestAsync(source, target, timeout.Token);
        target.UseResumeBootstrap(request);

        using var server = await target.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = request.Registration.Binding.ConfigureEndpoint;
        var path = request.Registration.Binding.ConfigureEndpoint.AbsolutePath + "/resume-state";
        using var anonymous = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Authorization = new("Bearer", target.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        using var wrongAudience = await client.PostAsJsonAsync(
            "http://runtime.test" + path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAudience.StatusCode);

        using var missing = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        var missingReceipt = await missing.Content.ReadFromJsonAsync<RuntimeResumeHostStateReceipt>(
            HostFixture.Json, timeout.Token);
        Assert.Equal(RuntimeResumeHostState.Missing, missingReceipt!.State);

        await target.Host.ReceiveAsync(new RuntimeBootstrapDeliveryRequest(
            request.Registration.RuntimeInstanceId,
            request.BootstrapOperationId,
            target.BootstrapGrantId,
            RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(
                request.Resume.SourceRelease.HostReceipt).Reference.Material!.Sha256,
            new string('c', 64),
            request.Registration.ExpiresAt), target.Actor, timeout.Token);
        using var present = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, present.StatusCode);
        var presentReceipt = await present.Content.ReadFromJsonAsync<RuntimeResumeHostStateReceipt>(
            HostFixture.Json, timeout.Token);
        Assert.Equal(RuntimeResumeHostState.BootstrapPresent, presentReceipt!.State);

        using var changedAttempt = await client.PostAsJsonAsync(
            path, request with { ResumeAttemptId = Guid.NewGuid() }, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, changedAttempt.StatusCode);
        Assert.Equal(4, target.ResumeBootstrapChecks.Count);
    }

    private static async Task<RuntimeResumeBootstrapRequest> CreateResumeBootstrapRequestAsync(
        HostFixture source, HostFixture target, CancellationToken token)
    {
        var ready = await source.ConfigureAsync(token);
        var proof = source.Proof(ready);
        await source.Host.SendAsync(source.Message(proof), source.Actor, token);
        var hostReceipt = await source.Host.SuspendAsync(
            source.SuspendRequest(proof), source.Actor, token);
        var flush = hostReceipt.WorkspaceFlush!;
        var identity = new SessionIdentity(
            proof.Registration.Binding.ProjectId,
            proof.Registration.Binding.RunId,
            proof.Registration.Binding.SessionId);
        var manifest = new SessionSuspendResumeManifest(
            1,
            hostReceipt.ManifestId,
            identity,
            SessionSuspendResumeManifestState.Suspended,
            flush.EnvironmentFence,
            proof.Registration.Binding.ExecutionFence,
            hostReceipt.CacheAcknowledgment,
            new(
                identity,
                SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
                "checkpoint",
                1,
                "plan",
                1),
            hostReceipt.CacheAcknowledgment.Position,
            flush.Volume,
            flush.Resource,
            flush.DataGeneration,
            flush.TreeSha256,
            null,
            null,
            null,
            1,
            ImmutableArray<string>.Empty).Validate();
        var release = new RuntimeSuspendPlacementReleaseRequest(hostReceipt, 3).Validate();
        var operationId = Guid.NewGuid();
        var resume = new RuntimeWorkspaceResumeRequest(
            1, operationId, 4, manifest, release)
        {
            RestoredWorkspace = CreateResumeWorkspace(source.Readiness.Placement, flush, release)
        }.Validate();
        var workspace = resume.RestoredWorkspace!;
        var attemptId = RuntimeResumeAttemptIdentity.Initial(operationId);
        var targetBinding = proof.Registration.Binding with
        {
            ExecutionFence = checked(proof.Registration.Binding.ExecutionFence + 1),
            EnvironmentId = workspace.Placement.EnvironmentId,
            PlacementUid = workspace.Placement.Resource.ResourceId,
            PlacementGeneration = workspace.Placement.Resource.Generation,
            ProfileId = workspace.Placement.ProfileId,
            ConfigureEndpoint = workspace.Placement.ConfigureEndpoint,
            ObservationEndpoint = workspace.Placement.ObservationEndpoint,
            PlacementProviderId = workspace.Placement.Resource.ProviderId,
            EnvironmentLifecycleGeneration = workspace.Placement.LifecycleGeneration,
            EnvironmentLeaseRevision = workspace.Placement.LeaseRevision,
            EnvironmentCurrentFencingGeneration = workspace.Placement.CurrentFencingGeneration,
            EnvironmentProviderFencingGeneration = workspace.Placement.ProviderFencingGeneration,
            Image = workspace.Placement.Image,
            ResumeOperationId = operationId,
            ResumeAttemptId = attemptId
        };
        var registration = new RuntimeRegistration(
            Guid.NewGuid(),
            1,
            targetBinding,
            RuntimeRegistrationState.Active,
            workspace.Placement.LeaseExpiresAt);
        target.Registration = registration;
        var requestIds = (
            RuntimeResumeAttemptIdentity.Bootstrap(operationId, attemptId),
            RuntimeResumeAttemptIdentity.Consume(operationId, attemptId),
            RuntimeResumeAttemptIdentity.Exchange(operationId, attemptId));
        return new RuntimeResumeBootstrapRequest(
            1, resume, registration, requestIds.Item1, requestIds.Item2, requestIds.Item3)
        {
            ResumeAttemptId = attemptId
        }.Validate();
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("manifest")]
    [InlineData("phase-version")]
    [InlineData("project")]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("fence")]
    [InlineData("selection")]
    [InlineData("source-grant")]
    [InlineData("source-revision")]
    [InlineData("proof-version")]
    public async Task NativeSuspendRejectsChangedCoreEchoBeforeStoppingTurnsOrWritingCache(string changed)
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);
        fixture.SuspendEcho = current => changed switch
        {
            "operation" => current with { OperationId = Guid.NewGuid() },
            "manifest" => current with { ManifestId = Guid.NewGuid() },
            "phase-version" => current with { PhaseVersion = current.PhaseVersion + 1 },
            "source-grant" => current with { Proof = current.Proof with { SourceGrantId = Guid.NewGuid() } },
            "source-revision" => current with
            {
                Proof = current.Proof with { SourceGrantRevision = current.Proof.SourceGrantRevision + 1 }
            },
            "proof-version" => current with { Proof = current.Proof with { ContractVersion = 2 } },
            _ => current with
            {
                Proof = current.Proof with
                {
                    Registration = current.Proof.Registration with
                    {
                        Binding = current.Proof.Registration.Binding with
                        {
                            ProjectId = changed == "project" ? "foreign" : proof.Registration.Binding.ProjectId,
                            RunId = changed == "run" ? "foreign" : proof.Registration.Binding.RunId,
                            SessionId = changed == "session" ? "foreign" : proof.Registration.Binding.SessionId,
                            ExecutionFence = changed == "fence" ? proof.Registration.Binding.ExecutionFence + 1 :
                                proof.Registration.Binding.ExecutionFence,
                            AcceptedSelectionHash = changed == "selection" ? new string('f', 64) :
                                proof.Registration.Binding.AcceptedSelectionHash
                        }
                    }
                }
            }
        };

        Assert.Equal("runtime_suspend_authority_changed",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(request, Assert.Single(fixture.SuspendChecks));
        Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        Assert.Equal(2, fixture.Sdk.Requests.Count(rpc => rpc.Method == "session.send"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "runtime_owner_denied")]
    [InlineData(HttpStatusCode.Conflict, "runtime_owner_unavailable")]
    [InlineData(HttpStatusCode.NotFound, "runtime_owner_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "runtime_owner_unavailable")]
    public async Task NativeSuspendNeedsCoreAuthorityBeforeStoppingTurns(
        HttpStatusCode status, string code)
    {
        await using var fixture = new HostFixture(workflowBound: true) { SuspendStatus = status };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);

        Assert.Equal(code, (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        Assert.Equal(2, fixture.Sdk.Requests.Count(rpc => rpc.Method == "session.send"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NativeSuspendRequiresAPositivePhaseVersion(long phaseVersion)
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        var request = fixture.SuspendRequest(proof, phaseVersion);

        Assert.Equal("runtime_suspend_request_invalid",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Empty(fixture.SuspendChecks);
        Assert.DoesNotContain(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task NativeSuspendRechecksCoreBeforeCacheCaptureAndPersistence(int deniedCheck)
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);
        fixture.SuspendEcho = current => fixture.SuspendChecks.Count == deniedCheck
            ? current with { PhaseVersion = 2 } : current;

        Assert.Equal("runtime_suspend_authority_changed",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(deniedCheck, fixture.SuspendChecks.Count);
        Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        Assert.DoesNotContain(fixture.Material, material => material.EventId == request.ManifestId);
    }

    [Fact]
    public async Task NativeSuspendRejectsLostCoreOperationAuthorityAfterActualCachePersistence()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);
        fixture.AfterMaterialWrite = material =>
        {
            if (material.EventId == request.ManifestId)
                fixture.SuspendStatus = HttpStatusCode.Conflict;
        };

        Assert.Equal("runtime_owner_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Single(fixture.Material, material => material.EventId == request.ManifestId);
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeSuspendReplayNeedsStoredCacheAndCurrentCoreOperation(bool missingCache)
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);
        await fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token);
        fixture.MissingMaterialId = missingCache ? request.ManifestId : null;
        fixture.AfterMaterialRead = read =>
        {
            if (read.Material.EventId == request.ManifestId)
                fixture.SuspendStatus = HttpStatusCode.Conflict;
        };

        Assert.Equal("runtime_owner_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(2, fixture.Material.Count(material => material.Kind == SessionMaterialKind.SdkCache));
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
    }

    [Fact]
    public async Task NativeSuspendCannotTurnAnAbortOrIdleAcknowledgmentIntoACompletionReceipt()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Sdk.BeforeTurnResponse = token => release.Task.WaitAsync(token);
        fixture.Sdk.BeforeAbortIdle = token => idle.Task.WaitAsync(token);
        var turn = fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, cancellation.Token);
        await fixture.Sdk.TurnReceived.Task.WaitAsync(timeout.Token);
        var request = fixture.SuspendRequest(proof);
        var suspend = fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token);
        await cancellation.CancelAsync();
        await fixture.Sdk.AbortAcknowledged.Task.WaitAsync(timeout.Token);
        Assert.False(suspend.IsCompleted);
        Assert.DoesNotContain(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        idle.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => suspend);
        Assert.Equal("runtime_native_suspend_receipt_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Empty(fixture.NativeObservations);
        Assert.DoesNotContain(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
    }

    [Fact]
    public async Task NativeSuspendRechecksAuthorityAfterTheActualCacheWriteBeforeReturningEvidence()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        var request = fixture.SuspendRequest(proof);
        fixture.AfterMaterialWrite = material =>
        {
            if (material.EventId == request.ManifestId)
                fixture.Registration = fixture.Registration with { Revision = 2 };
        };
        Assert.Equal("runtime_registration_stale",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Single(fixture.Material, material => material.EventId == request.ManifestId);
        Assert.Single(fixture.Sdk.Requests, rpc => rpc.Method == "session.send");
    }

    [Fact]
    public async Task NativeSuspendHttpRequiresSignedAuthorityAndExactAudienceBeforeCacheDisclosure()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);
        using var server = await fixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = fixture.Registration.Binding.ConfigureEndpoint;
        var request = fixture.SuspendRequest(proof);
        const string path = "/runtime/v1/suspend";
        using var anonymous = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        using var wrongAudience = await client.PostAsJsonAsync(
            "http://runtime.test" + path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAudience.StatusCode);
        Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        using var missingFlush = await client.PostAsJsonAsync(
            path, new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1),
            HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, missingFlush.StatusCode);
        Assert.Contains("runtime_workspace_flush_request_missing",
            await missingFlush.Content.ReadAsStringAsync());
        using var response = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var receipt = await response.Content.ReadFromJsonAsync<RuntimeHostSuspendReceipt>(
            HostFixture.Json, timeout.Token);
        Assert.Equal(request.ManifestId, receipt!.CacheAcknowledgment.EventId);
        Assert.NotNull(receipt.WorkspaceFlush);
        Assert.Equal(request.WorkspaceFlush!.EnvironmentFence, receipt.WorkspaceFlush!.EnvironmentFence);
        Assert.Equal(request.WorkspaceFlush.Resource, receipt.WorkspaceFlush.Resource);
        Assert.Equal(request.WorkspaceFlush.ExpectedDataGeneration + 1, receipt.WorkspaceFlush.DataGeneration);
        Assert.Equal(request.WorkspaceFlush.IdempotencyKey, receipt.WorkspaceFlush.IdempotencyKey);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token + "invalid");
        using var forged = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(2, fixture.Material.Count(material => material.Kind == SessionMaterialKind.SdkCache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterialAcknowledgmentMustMatchTheSessionPromptCapacity(bool changed)
    {
        await using var fixture = new HostFixture()
        {
            MaterialPromptCapacityChanged = changed
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        var send = fixture.Host.SendAsync(fixture.Message(fixture.Proof(ready)), fixture.Actor, timeout.Token);
        if (changed)
        {
            Assert.Equal("runtime_material_receipt_invalid",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => send)).Code);
            Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
        }
        else
        {
            Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single((await send).Parts).Text);
            Assert.Contains(fixture.Material, request => request.Kind == SessionMaterialKind.SdkCache);
        }
        Assert.NotEmpty(fixture.Material);
        Assert.All(fixture.Material, request => Assert.Null(request.MaxPromptTokens));
    }

    [Fact]
    public async Task ConfigureAndRefreshReplaysKeepOneNativeSessionAndRejectChangedAuthority()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        Assert.Equal(Enum.GetValues<SandboxStartupPhase>(), ready.StartupPhases.Select(phase => phase.Phase));
        Assert.Equal(fixture.Image.CompressedPullBytes,
            ready.StartupPhases.Single(phase => phase.Phase == SandboxStartupPhase.ImageReady).CompressedPullBytes);
        Assert.Equal(JsonSerializer.Serialize(ready), JsonSerializer.Serialize(
            await fixture.Host.ConfigureAsync(fixture.Configure, fixture.Actor, timeout.Token)));
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.create");
        Assert.Equal("runtime_configuration_conflict",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ConfigureAsync(fixture.Configure with { ConsumeOperationId = Guid.NewGuid() },
                    fixture.Actor, timeout.Token))).Code);
        Assert.Equal(JsonSerializer.Serialize(ready), JsonSerializer.Serialize(
            await fixture.Host.ReadinessAsync(timeout.Token)));
        var proof = fixture.Proof(ready);
        var refresh = new RuntimeHostRefreshRequest(proof, Guid.NewGuid());
        var rotated = await fixture.Host.RefreshAsync(refresh, fixture.Actor, timeout.Token);
        Assert.Equal(2, rotated.SourceGrant.Revision);
        Assert.Equal(rotated, await fixture.Host.RefreshAsync(refresh, fixture.Actor, timeout.Token));
        Assert.Equal(1, fixture.Rotations);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token));
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(fixture.Message(proof with
            {
                SourceGrantRevision = 2, Purpose = RuntimeCredentialPurpose.Configure
            }), fixture.Actor, timeout.Token));
        fixture.Registration = fixture.Registration with { Revision = 2 };
        Assert.Equal("runtime_registration_stale",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ReadinessAsync(timeout.Token))).Code);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
    }

    [Fact]
    public async Task ConfigureCannotSubstituteEnvironmentBoundMcpServerBeforeNativeSessionCreation()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var endpoint = new RuntimeMcpServerEndpoint("reviewed-tools", new("https://tools.example.test/mcp"));
        fixture.Readiness = fixture.Readiness with
        {
            Placement = fixture.Readiness.Placement with { McpServer = endpoint }
        };
        await fixture.DeliverAsync(timeout.Token);

        var substituted = endpoint with { HttpsUrl = new("https://attacker.example.test/mcp") };
        var mismatch = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.ConfigureAsync(fixture.Configure with { McpServer = substituted },
                fixture.Actor, timeout.Token));
        Assert.Equal("runtime_mcp_server_binding_invalid", mismatch.Code);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.create");

        var unavailable = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.ConfigureAsync(fixture.Configure with { McpServer = endpoint },
                fixture.Actor, timeout.Token));
        Assert.Equal("runtime_mcp_tool_interception_unavailable", unavailable.Code);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.create");
    }

    [Fact]
    public async Task DedicatedRemoteToolResultClientDoesNotSendWithoutApprovedTlsProfile()
    {
        await using var fixture = new HostFixture();
        var requests = 0;
        using var httpClient = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromException<HttpResponseMessage>(
                new InvalidOperationException("The unavailable route must not send a request."));
        }));
        var material = new RuntimeSessionMaterialHttpClient(
            httpClient, new Uri("https://events.test/"), fixture.Actor);

        var writeFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            material.WriteRemoteToolResultAsync(
                null!, "private result"u8.ToArray(), CancellationToken.None));
        var readFailure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            material.ReadRemoteToolResultAsync(null!, CancellationToken.None));

        Assert.Equal("runtime_remote_tool_result_route_unavailable", writeFailure.Code);
        Assert.Equal("runtime_remote_tool_result_route_unavailable", readFailure.Code);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ImmediateRunsAtIdleAheadOfEnqueuedWorkAndExactMessageReplayDoesNotRunAgain()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = 0;
        fixture.Sdk.BeforeTurnResponse = token => Interlocked.Increment(ref turns) == 1
            ? release.Task.WaitAsync(token) : Task.CompletedTask;
        var proof = fixture.Proof(ready);
        var first = fixture.Message(proof);
        var queued = fixture.Message(proof);
        var immediate = fixture.Message(proof, AddressedMessageDeliveryMode.Immediate);
        var active = fixture.Host.SendAsync(first, fixture.Actor, timeout.Token);
        await fixture.Sdk.TurnReceived.Task.WaitAsync(timeout.Token);
        var enqueue = fixture.Host.SendAsync(queued, fixture.Actor, timeout.Token);
        var priority = fixture.Host.SendAsync(immediate, fixture.Actor, timeout.Token);
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
        release.TrySetResult();
        var response = await active;
        await Task.WhenAll(enqueue, priority).WaitAsync(timeout.Token);
        Assert.Equal(new[] { first.Message.MessageId, immediate.Message.MessageId, queued.Message.MessageId },
            fixture.Material.Where(request => request.Role == "user").Select(request => request.EventId));
        Assert.Equal(response, await fixture.Host.SendAsync(first, fixture.Actor, timeout.Token));
        Assert.Equal(3, fixture.Sdk.Requests.Count(request => request.Method == "session.send"));
        Assert.Equal("runtime_a2a_message_conflict",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.Host.SendAsync(
                first with { Message = first.Message with { Parts = [new("text", "changed")] } },
                fixture.Actor, timeout.Token))).Code);
        Assert.Equal(3, fixture.Material.Count(request => request.Kind == SessionMaterialKind.SdkCache));
    }

    [Theory]
    [InlineData(" Explain this:\n```csharp\nvar value = 1;\n\tConsole.WriteLine(value);\n```\n ", true)]
    [InlineData("\tFirst line\r\n\tSecond line\r\n ", true)]
    [InlineData("A prompt\0with NUL", false)]
    [InlineData("A prompt\u001bwith escape", false)]
    public async Task A2APreservesMultilinePromptBytesAndRejectsUnsupportedControlsWithoutDispatch(
        string prompt, bool supported)
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        fixture.Sdk.ExpectedPrompt = prompt;
        var ready = await fixture.ConfigureAsync(timeout.Token);
        var input = fixture.Message(fixture.Proof(ready));
        input = input with { Message = input.Message with { Parts = [new("text", prompt)] } };
        using var server = await fixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = fixture.Registration.Binding.ConfigureEndpoint;
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");

        using var response = await client.PostAsJsonAsync(
            "/runtime/v1/a2a/message:send", input, HostFixture.Json, timeout.Token);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (supported)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = Encoding.UTF8.GetBytes(prompt);
            var send = Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
            Assert.Equal(bytes, Encoding.UTF8.GetBytes(send.Parameters.GetProperty("prompt").GetString()!));
            Assert.Equal(bytes, Assert.Single(fixture.Material, material => material.Role == "user").Bytes);
            Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("runtime_a2a_message_invalid", error.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
            Assert.DoesNotContain(fixture.Material,
                material => material.Kind is SessionMaterialKind.TurnContent or SessionMaterialKind.SdkCache);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NativeUsageMustReachTheAccountingAcknowledgmentBeforeTheTurnSucceeds(
        bool failAccounting, bool workflowBound = false)
    {
        await using var fixture = new HostFixture(workflowBound: workflowBound);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeAccounting = token => release.Task.WaitAsync(token);
        fixture.FailAccounting = failAccounting;
        fixture.Sdk.BeforeTurnResponse = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        var response = fixture.Host.SendAsync(fixture.Message(fixture.Proof(ready)), fixture.Actor, timeout.Token);
        await fixture.AccountingReceived.Task.WaitAsync(timeout.Token);
        Assert.False(response.IsCompleted);
        release.TrySetResult();
        if (failAccounting)
        {
            Assert.Equal("runtime_usage_persistence_failed",
                (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => response)).Code);
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ReadinessAsync(timeout.Token));
            await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.Host.DisposeAsync().AsTask());
        }
        else
        {
            Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single((await response).Parts).Text);
            Assert.Single(fixture.Usage);
            Assert.Equal(1234567.25m, fixture.Usage.Single().Usage.Measurement.ProviderUnits);
            Assert.Equal(1, fixture.Accounted);
        }
    }

    [Theory]
    [InlineData("denied", "runtime_owner_denied")]
    [InlineData("message", "runtime_native_turn_admission_invalid")]
    [InlineData("prompt", "runtime_native_turn_admission_invalid")]
    [InlineData("revision", "runtime_native_turn_admission_invalid")]
    [InlineData("stale", "runtime_registration_stale")]
    public async Task WorkflowTurnRequiresExactDurableAdmissionAndCurrentAuthorityBeforeNativeSend(
        string fault, string expectedCode)
    {
        await using var fixture = new HostFixture(workflowBound: true) { NativeAdmissionFault = fault };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var message = fixture.Message(fixture.Proof(await fixture.ConfigureAsync(timeout.Token)));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(message, fixture.Actor, timeout.Token));

        Assert.Equal(expectedCode, failure.Code);
        Assert.Single(fixture.NativeAdmissions);
        Assert.DoesNotContain(fixture.Material,
            material => material.Kind is SessionMaterialKind.TurnContent or SessionMaterialKind.SdkCache);
        Assert.Empty(fixture.NativeObservations);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowNativeOutputReturnsAfterObservedAccountingAndReplaysWithoutResending(
        bool emitsUsage)
    {
        await using var fixture = new HostFixture(workflowBound: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        if (emitsUsage)
            fixture.Sdk.BeforeTurnResponse = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        var message = fixture.Message(fixture.Proof(await fixture.ConfigureAsync(timeout.Token)));

        var response = await fixture.Host.SendAsync(message, fixture.Actor, timeout.Token);
        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(response.Parts).Text);
        Assert.Equal(response, await fixture.Host.SendAsync(message, fixture.Actor, timeout.Token));
        var admission = Assert.Single(fixture.NativeAdmissions);
        Assert.Equal(RuntimeNativeTurnContract.RequestHash(message),
            RuntimeNativeTurnContract.RequestHash(admission.Message));
        var observation = Assert.Single(fixture.NativeObservations);
        Assert.Equal(message.Message.MessageId, observation.Admission.MessageId);
        Assert.Equal(fixture.Sdk.NativeMessageId.ToString("D"), observation.Observation.NativeMessageId);
        Assert.Equal(fixture.Sdk.NativeCompletionReceiptEventId,
            observation.Observation.NativeCompletionReceiptEventId);
        Assert.NotEqual(message.Message.MessageId, fixture.Sdk.NativeMessageId);
        Assert.Null(observation.Observation.AccountingCheckpoint);
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.Single(fixture.Material, request => request.Role == "user");
        Assert.Single(fixture.Material, request => request.Role == "assistant");
        Assert.Single(fixture.Material, request => request.Kind == SessionMaterialKind.SdkCache);
        Assert.Equal(emitsUsage ? 1 : 0, fixture.Accounted);
        Assert.Equal(emitsUsage ? 1 : 0, fixture.Usage.Count);
        var accounted = Assert.Single(fixture.NativeAccounting);
        Assert.Equal(observation.Admission.MessageId, accounted.Recorded.Admission.MessageId);
        Assert.Equal(emitsUsage ? 1 : 0, accounted.RequiredReceipts.Length);
        Assert.All(accounted.RequiredReceipts, reference =>
            Assert.Equal(CostDisposition.Unpriced, reference.Accounting.Disposition));
    }

    [Theory]
    [InlineData("unavailable", "runtime_owner_unavailable")]
    [InlineData("missing", "runtime_usage_cost_snapshot_invalid")]
    [InlineData("changed", "runtime_usage_cost_snapshot_invalid")]
    [InlineData("revision", "runtime_native_turn_accounting_invalid")]
    public async Task FailedDurableObservedJoinCannotReturnAnAnswerOrResend(string fault, string expectedCode)
    {
        await using var fixture = new HostFixture(workflowBound: true) { NativeAccountingFault = fault };
        fixture.Sdk.BeforeTurnResponse = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var message = fixture.Message(fixture.Proof(await fixture.ConfigureAsync(timeout.Token)));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(message, fixture.Actor, timeout.Token));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Same(failure, await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(message, fixture.Actor, timeout.Token)));
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.Single(fixture.NativeAccounting);
        Assert.Equal(1, fixture.Accounted);
    }

    [Fact]
    public async Task LaterUsageIsAccountedWithoutInventingMembershipInsideTheNativeCompletionRange()
    {
        await using var fixture = new HostFixture(workflowBound: true);
        fixture.Sdk.AfterNativeCompletionReceipt = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var message = fixture.Message(fixture.Proof(await fixture.ConfigureAsync(timeout.Token)));
        var response = await fixture.Host.SendAsync(message, fixture.Actor, timeout.Token);
        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(response.Parts).Text);
        Assert.Equal(1, fixture.Accounted);
        Assert.Single(fixture.Usage);
        Assert.Empty(Assert.Single(fixture.NativeObservations).Observation.UsageEventIds);
        Assert.Empty(Assert.Single(fixture.NativeAccounting).RequiredReceipts);
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
    }

    [Theory]
    [InlineData("missing", "runtime_native_turn_receipt_unavailable")]
    [InlineData("unavailable", "runtime_owner_unavailable")]
    [InlineData("mismatch", "runtime_native_turn_receipt_invalid")]
    public async Task WorkflowMissingNativeProofOrFailedRecordCannotReturnAnAnswerOrResend(
        string fault, string expectedCode)
    {
        await using var fixture = new HostFixture(workflowBound: true) { NativeRecordFault = fault };
        fixture.Sdk.EmitNativeCompletionReceipt = fault != "missing";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var message = fixture.Message(fixture.Proof(await fixture.ConfigureAsync(timeout.Token)));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(message, fixture.Actor, timeout.Token));
        Assert.Equal(expectedCode, failure.Code);
        Assert.Same(failure, await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            fixture.Host.SendAsync(message, fixture.Actor, timeout.Token)));
        Assert.Single(fixture.NativeAdmissions);
        Assert.Single(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.DoesNotContain(fixture.Material, request =>
            request.Role == "assistant" || request.Kind == SessionMaterialKind.SdkCache);
        Assert.Equal(fault == "missing" ? 0 : 1, fixture.NativeObservations.Count);
    }

    [Fact]
    public async Task UnpricedCostPreflightCleansPreparedSessionBeforeReadinessOrModelSend()
    {
        await using var fixture = new HostFixture(hardCreditLimit: 100m) { CostPreflightPriced = false };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.DeliverAsync(timeout.Token);

        Assert.Equal("runtime_cost_preflight_unpriced",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ConfigureAsync(fixture.Configure, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(1, fixture.CostPreflightRequests);
        Assert.Equal(1, fixture.SourceRevocations);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.Equal("runtime_host_not_ready",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ReadinessAsync(timeout.Token))).Code);
    }

    [Fact]
    public async Task CostPreflightMustPinTheExactRegisteredUsageSource()
    {
        await using var fixture = new HostFixture(hardCreditLimit: 100m) { CostPreflightSourceHashMismatch = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.DeliverAsync(timeout.Token);

        Assert.Equal("runtime_cost_preflight_source_mismatch",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ConfigureAsync(fixture.Configure, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(1, fixture.CostPreflightRequests);
        Assert.Equal(1, fixture.SourceRevocations);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
    }

    [Fact]
    public async Task PositiveHardCreditLimitCannotBecomeReadyWithoutUsageAffinity()
    {
        await using var fixture = new HostFixture(hardCreditLimit: 100m);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.DeliverAsync(timeout.Token);

        Assert.Equal("runtime_usage_affinity_unavailable",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ConfigureAsync(fixture.Configure, fixture.Actor, timeout.Token))).Code);
        Assert.Equal(1, fixture.CostPreflightRequests);
        Assert.Equal(1, fixture.SourceRevocations);
        Assert.Empty(fixture.CostTurnRequests);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.Empty(fixture.Material);
        Assert.Equal("runtime_host_not_ready",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ReadinessAsync(timeout.Token))).Code);
    }

    [Theory]
    [InlineData(1, null, null)]
    [InlineData(null, 1, null)]
    [InlineData(null, null, 1024)]
    public async Task ConfiguredNumericRunPolicyFailsBeforeReadyOrNativeSessionCreation(
        int? maxModelTurns,
        int? maxToolCalls,
        int? maxPromptTokens)
    {
        await using var fixture = new HostFixture(
            maxModelTurns: maxModelTurns,
            maxToolCalls: maxToolCalls,
            maxPromptTokens: maxPromptTokens);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.DeliverAsync(timeout.Token);

        Assert.Equal("runtime_numeric_limits_unsupported",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ConfigureAsync(fixture.Configure, fixture.Actor, timeout.Token))).Code);
        Assert.Empty(fixture.Sdk.Requests);
        Assert.Equal(0, fixture.CostPreflightRequests);
        Assert.Empty(fixture.CostTurnRequests);
        Assert.Empty(fixture.Material);
        Assert.Equal("runtime_host_not_ready",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.ReadinessAsync(timeout.Token))).Code);
    }

    [Fact]
    public async Task UnbudgetedHostedCopilotKeepsLegacyStartupAndSelectionHash()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        Assert.Equal(0, fixture.CostPreflightRequests);
        var bindingWire = JsonSerializer.SerializeToElement(fixture.Registration.Binding, HostFixture.Json);
        Assert.False(bindingWire.TryGetProperty("copilotHardCreditLimit", out _));

        fixture.Sdk.BeforeTurnResponse = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        var message = fixture.Message(fixture.Proof(ready));
        var response = await fixture.Host.SendAsync(message, fixture.Actor, timeout.Token);

        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(response.Parts).Text);
        var receipt = Assert.Single(fixture.Usage);
        Assert.Null(receipt.Usage.A2AMessageId);
        Assert.Equal(fixture.Registration.Binding.AcceptedSelectionHash,
            receipt.Usage.SdkSource!.AcceptedSelectionHash);
        Assert.Equal(fixture.Registration.Binding.AcceptedSelectionHash,
            bindingWire.GetProperty("acceptedSelectionHash").GetString());
        Assert.Null(fixture.Registration.Binding.CopilotHardCreditLimit);
    }

    [Fact]
    public async Task LateUsageCallbacksRemainUncorrelatedInsteadOfBeingBoundToTheNextA2AMessage()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        var firstUsageCreatedAt = default(DateTimeOffset);
        var send = 0;
        fixture.Sdk.BeforeTurnResponse = async cancellationToken =>
        {
            if (Interlocked.Increment(ref send) == 1)
            {
                firstUsageCreatedAt = DateTimeOffset.UtcNow;
                return;
            }
            await fixture.Sdk.EmitUsageAsync(
                fixture.Sdk.UsageData(), eventId: Guid.NewGuid(), timestamp: firstUsageCreatedAt);
            await fixture.Sdk.EmitUsageAsync(
                fixture.Sdk.UsageData(), eventId: Guid.NewGuid(), timestamp: DateTimeOffset.UtcNow);
        };
        var first = fixture.Message(proof);
        var second = fixture.Message(proof);

        await fixture.Host.SendAsync(first, fixture.Actor, timeout.Token);
        await fixture.Host.SendAsync(second, fixture.Actor, timeout.Token);

        Assert.NotEqual(first.Message.MessageId, second.Message.MessageId);
        var receipts = fixture.Usage.ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Null(receipt.Usage.A2AMessageId));
    }

    [Fact]
    public async Task ZeroHardCreditLimitAllowsStartupButBlocksCostTurnDispatch()
    {
        await using var fixture = new HostFixture(hardCreditLimit: 0m);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        Assert.Equal(0, fixture.CostPreflightRequests);

        Assert.Equal("runtime_cost_budget_exhausted",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SendAsync(fixture.Message(fixture.Proof(ready)), fixture.Actor, timeout.Token))).Code);
        Assert.DoesNotContain(fixture.Sdk.Requests, request => request.Method == "session.send");
        Assert.DoesNotContain(fixture.Material,
            material => material.Kind is SessionMaterialKind.TurnContent or SessionMaterialKind.SdkCache);
        Assert.Empty(fixture.Usage);
    }

    [Fact]
    public async Task ByokStartsAndAccountsUsageWithoutCopilotCostPreflight()
    {
        await using var fixture = new HostFixture(ModelSourceMode.Byok);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready = await fixture.ConfigureAsync(timeout.Token);
        Assert.Equal(0, fixture.CostPreflightRequests);

        fixture.Sdk.BeforeTurnResponse = _ => fixture.Sdk.EmitUsageAsync(fixture.Sdk.UsageData());
        var response = await fixture.Host.SendAsync(fixture.Message(fixture.Proof(ready)), fixture.Actor, timeout.Token);

        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(response.Parts).Text);
        var usage = Assert.Single(fixture.Usage).Usage;
        Assert.Equal(SdkMeterSources.ByokTokens, usage.ModelBinding!.MeterSource);
        Assert.Equal("tokens", usage.Measurement!.ProviderUnit);
        Assert.Null(usage.Measurement.ProviderUnits);
        Assert.Equal(1, fixture.Accounted);
    }

    [Fact]
    public async Task CompletedMessageReplaySurvivesMoreTurnsThanThePendingCapacity()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var proof = fixture.Proof(await fixture.ConfigureAsync(timeout.Token));
        var first = fixture.Message(proof);
        var response = await fixture.Host.SendAsync(first, fixture.Actor, timeout.Token);
        for (var turn = 0; turn < 9; turn++)
            await fixture.Host.SendAsync(fixture.Message(proof), fixture.Actor, timeout.Token);

        Assert.Equal(response, await fixture.Host.SendAsync(first, fixture.Actor, timeout.Token));
        Assert.Equal(10, fixture.Sdk.Requests.Count(request => request.Method == "session.send"));
        Assert.Equal(10, fixture.Material.Count(request => request.Role == "user"));
        Assert.Equal("runtime_a2a_message_conflict",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => fixture.Host.SendAsync(
                first with { Message = first.Message with { Parts = [new("text", "changed")] } },
                fixture.Actor, timeout.Token))).Code);
        fixture.Registration = fixture.Registration with { Revision = 2 };
        Assert.Equal("runtime_registration_stale",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SendAsync(first, fixture.Actor, timeout.Token))).Code);
    }

    [Theory]
    [InlineData("isolation")]
    [InlineData("workspace")]
    [InlineData("egress")]
    [InlineData("resource")]
    [InlineData("lease")]
    [InlineData("owner")]
    [InlineData("image")]
    [InlineData("bytes")]
    [InlineData("phase")]
    [InlineData("future")]
    [InlineData("state")]
    [InlineData("budget")]
    public async Task ReadinessRequiresFreshLeaseOwnerIsolationWorkspaceEgressAndMeasuredStartup(string loss)
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.ConfigureAsync(timeout.Token);
        var context = fixture.Readiness;
        fixture.Readiness = loss switch
        {
            "isolation" => context with { Observation = context.Observation with { VmIsolationVerified = false } },
            "workspace" => context with { WorkspaceMountPath = Path.GetFullPath("wrong-workspace") },
            "egress" => context with { Observation = context.Observation with { VerifiedNetworkGeneration = null } },
            "resource" => context with
            {
                Observation = context.Observation with
                { Resource = context.Observation.Resource with { ResourceId = "replacement-placement" } }
            },
            "lease" => context with { Placement = context.Placement with { LeaseRevision = 2 } },
            "owner" => context with
            {
                Placement = context.Placement with
                { RuntimeOwnerContext = context.Placement.RuntimeOwnerContext! with { ExecutionFence = 2 } }
            },
            "image" => context with
            {
                Observation = context.Observation with
                { StartupPhases = context.Observation.StartupPhases.SetItem(1,
                    new(SandboxStartupPhase.ImageReady, 1, context.Observation.StartupPhases[1].ObservedAt,
                        "sha256:" + new string('b', 64), fixture.Image.CompressedPullBytes)) }
            },
            "bytes" => context with
            {
                Observation = context.Observation with
                { StartupPhases = context.Observation.StartupPhases.SetItem(1,
                    new(SandboxStartupPhase.ImageReady, 1, context.Observation.StartupPhases[1].ObservedAt,
                        fixture.Image.Digest, fixture.Image.CompressedPullBytes + 1)) }
            },
            "phase" => context with
            { Observation = context.Observation with { StartupPhases = context.Observation.StartupPhases.RemoveAt(0) } },
            "future" => context with
            {
                Observation = context.Observation with
                { StartupPhases = context.Observation.StartupPhases.SetItem(2,
                    new(SandboxStartupPhase.Started, 1, fixture.Time.GetUtcNow().AddSeconds(1))) }
            },
            "state" => context with { Observation = context.Observation with { State = (SandboxObservedState)99 } },
            "budget" => context with { StartupBudgets = context.StartupBudgets with { TotalSeconds = 1 } },
            _ => throw new InvalidOperationException(loss)
        };
        var failure = await Record.ExceptionAsync(() => fixture.Host.ReadinessAsync(timeout.Token));
        Assert.True(failure is RuntimeAuthorizationException or RuntimeStartupException, failure?.ToString());
        using var httpHost = await fixture.StartHttpAsync();
        using var client = httpHost.GetTestClient();
        using var health = await client.GetAsync("/health/ready", timeout.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public async Task ConfiguredPhaseAcceptsItsExactTimeCeilingButNotOneSecondOver(int seconds, bool accepted)
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        fixture.Sdk.BeforeStatusResponse = _ =>
        {
            fixture.Time.Now = fixture.Readiness.Observation.StartupPhases[2].ObservedAt.AddSeconds(seconds);
            return Task.CompletedTask;
        };
        if (accepted)
        {
            var ready = await fixture.ConfigureAsync(timeout.Token);
            Assert.Equal(fixture.Time.GetUtcNow(), ready.StartupPhases.Last().ObservedAt);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<RuntimeStartupException>(() => fixture.ConfigureAsync(timeout.Token));
            Assert.Equal("runtime_startup_time_budget_exceeded", failure.Code);
            Assert.Equal(SandboxStartupPhase.Configured, failure.Failure?.Phase);
        }
    }

    [Fact]
    public async Task HttpBoundaryRequiresSignedUnexpiredBearerAndExactHttpsAudienceBeforeEffects()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = await fixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = fixture.Registration.Binding.ConfigureEndpoint;
        var request = fixture.Configure;
        using var anonymous = await client.PostAsJsonAsync("/runtime/v1/configure/activate", request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousReadiness = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousReadiness.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        using var wrongAudience = await client.PostAsJsonAsync("http://runtime.test/runtime/v1/configure/activate",
            request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAudience.StatusCode);
        Assert.Empty(fixture.Sdk.Requests);
        await fixture.DeliverAsync(timeout.Token);
        using var configured = await client.PostAsJsonAsync("/runtime/v1/configure/activate", request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        Assert.True(configured.Headers.CacheControl?.NoStore);
        var ready = await configured.Content.ReadFromJsonAsync<RuntimeHostReadinessReceipt>(HostFixture.Json, timeout.Token);
        using var readiness = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.True(readiness.Headers.CacheControl?.NoStore);
        var currentReadiness = await readiness.Content.ReadFromJsonAsync<RuntimeHostReadinessReceipt>(
            HostFixture.Json, timeout.Token);
        Assert.NotNull(currentReadiness);
        Assert.Equal(JsonSerializer.Serialize(ready), JsonSerializer.Serialize(currentReadiness));
        Assert.DoesNotContain(fixture.Sdk.Requests, sdkRequest => sdkRequest.Method == "session.send");
        var message = fixture.Message(fixture.Proof(ready!));
        using var turn = await client.PostAsJsonAsync(
            "/runtime/v1/a2a/message:send", message, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        var answer = await turn.Content.ReadFromJsonAsync<RuntimeA2AResponse>(HostFixture.Json, timeout.Token);
        Assert.Equal("message", answer!.Kind);
        Assert.NotEqual(message.Message.MessageId, answer.MessageId);
        Assert.Equal(message.Message.ContextId, answer.ContextId);
        Assert.Equal("agent", answer!.Role);
        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(answer.Parts).Text);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token + "invalid");
        using var forged = await client.PostAsJsonAsync("/runtime/v1/refresh",
            new RuntimeHostRefreshRequest(fixture.Proof(ready!), Guid.NewGuid()), HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(0, fixture.Rotations);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        fixture.Registration = fixture.Registration with { Revision = 2 };
        using var staleReadiness = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, staleReadiness.StatusCode);
        Assert.Contains("runtime_registration_stale", await staleReadiness.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CurrentReadinessRequiresAuthenticatedCallerAndExactHttpsAudience()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var expected = await fixture.ConfigureAsync(timeout.Token);
        using var server = await fixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = fixture.Registration.Binding.ConfigureEndpoint;

        using var anonymous = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        using var wrongScheme = await client.GetAsync("http://runtime.test/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongScheme.StatusCode);
        using var wrongHost = await client.GetAsync("https://another.test/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongHost.StatusCode);
        using var current = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.True(current.Headers.CacheControl?.NoStore);
        var receipt = await current.Content.ReadFromJsonAsync<RuntimeHostReadinessReceipt>(HostFixture.Json, timeout.Token);
        Assert.Equal(expected.RuntimeInstanceId, receipt!.RuntimeInstanceId);
        Assert.Equal(expected.RegistrationRevision, receipt.RegistrationRevision);
        Assert.Equal(expected.ExecutionFence, receipt.ExecutionFence);
        Assert.Equal(expected.Image, receipt.Image);
        Assert.Equal(expected.SourceGrant, receipt.SourceGrant);
        Assert.Equal(0, fixture.Rotations);
        Assert.Empty(fixture.NativeAdmissions);
    }

    [Fact]
    public async Task CurrentReadinessRejectsForeignTenantAndChangedRegistrationWithoutEffects()
    {
        await using var fixture = new HostFixture();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await fixture.ConfigureAsync(timeout.Token);
        using var server = await fixture.StartHttpAsync();
        using var client = server.GetTestClient();
        client.BaseAddress = fixture.Registration.Binding.ConfigureEndpoint;
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "another-tenant");

        using var foreign = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Agentweaver-Tenant");
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        fixture.Registration = fixture.Registration with { Revision = fixture.Registration.Revision + 1 };
        using var stale = await client.GetAsync("/runtime/v1/readiness", timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        var error = await stale.Content.ReadFromJsonAsync<JsonElement>(HostFixture.Json, timeout.Token);
        Assert.Equal("runtime_registration_stale", error.GetProperty("code").GetString());
        Assert.Equal(0, fixture.Rotations);
        Assert.Empty(fixture.NativeAdmissions);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(new string('s', 64)));
        private readonly HttpClient _http;
        private Guid _bootstrap = Guid.NewGuid();
        private readonly Guid _source = Guid.NewGuid();
        private readonly string _hash = RuntimeContractValidation.Hash("{}"u8);
        private RuntimeModelCredentialGrantReceipt? _model;
        private SdkSessionFacts? _facts;
        private long _sourceRevision = 1;
        private int _position;
        private readonly ConcurrentDictionary<Guid, SessionMaterialReadResult> _material = new();
        private readonly ConcurrentDictionary<Guid, SessionMaterialWriteRequest> _materialWrites = new();
        public HostFixture(
            ModelSourceMode sourceMode = ModelSourceMode.HostedCopilot,
            decimal? hardCreditLimit = null,
            int? maxModelTurns = null,
            int? maxToolCalls = null,
            int? maxPromptTokens = null,
            bool workflowBound = false,
            string sessionId = "session",
            long executionFence = 1)
        {
            Time = new() { Now = DateTimeOffset.UtcNow };
            Token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: "https://broker.test/", audience: "runtime-host", expires: Time.Now.AddMinutes(6).UtcDateTime,
                signingCredentials: new(_key, SecurityAlgorithms.HmacSha256)));
            Actor = new(new SecretCredential(Token, Time.Now.AddMinutes(5), Time), "tenant");
            Image = new("sha256:" + new string('a', 64), "linux/amd64", 123456789);
            Registration = RuntimeCopilotSessionTests.Registration();
            Registration = Registration with
            {
                ExpiresAt = Time.Now.AddMinutes(5),
                Binding = Registration.Binding with
                {
                    SessionId = sessionId,
                    ExecutionFence = executionFence,
                    ConfigureEndpoint = new("https://runtime.test/runtime/v1/configure"),
                    PlacementProviderId = "sandbox-test", EnvironmentLifecycleGeneration = 1,
                    EnvironmentLeaseRevision = 1, Image = Image,
                    ModelSourceMode = sourceMode,
                    ModelCredentialReference = sourceMode == ModelSourceMode.Byok
                        ? new SecretRef("accepted-model-key", "v1") : null,
                    ModelConnectionId = sourceMode == ModelSourceMode.HostedCopilot
                        ? Guid.NewGuid() : null,
                    ModelConnectionScope = sourceMode == ModelSourceMode.HostedCopilot
                        ? ProjectAuthorityResourceType.Project : null,
                    WorkflowStepId = workflowBound ? "implement" : null,
                    MaxModelTurns = maxModelTurns,
                    MaxToolCalls = maxToolCalls,
                    MaxPromptTokens = maxPromptTokens,
                    CopilotHardCreditLimit = hardCreditLimit
                }
            };
            Sdk = new()
            {
                EmitUsageAfterCreate = false, ExpectedAvailableToolsCount = 7, PersistNativeSessionState = true,
                EmitNativeCompletionReceipt = workflowBound,
                Byok = sourceMode == ModelSourceMode.Byok
            };
            var modelBindings = new RuntimeModelBindingsResolver("legacy-unversioned",
                new Dictionary<string, RuntimeModelBinding>
                {
                    ["accepted-model-reference"] = new("controlled-model", sourceMode,
                        sourceMode == ModelSourceMode.Byok
                            ? new RuntimeByokProvider("openai", new Uri("https://byok.test/v1"), "responses")
                            : null)
                });
            if (sourceMode == ModelSourceMode.Byok)
                Registration = Registration with
                {
                    Binding = Registration.Binding with
                    {
                        ModelBindingPin = modelBindings.Pin("accepted-model-reference", sourceMode)
                    }
                };
            var factory = new RuntimeCopilotSessionFactory(Sdk.Connection,
                Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))), modelBindings);
            WorkingDirectory = factory.WorkingDirectory;
            var b = Registration.Binding;
            var owner = new RuntimeOwnerContext(1, b.ActorIssuer, b.ActorId, b.TenantId, b.ProjectId, b.RunId,
                b.SessionId, b.AgentId, b.ModelSelectionReference!, b.TurnId, b.ProjectRevision,
                b.ProjectConfigurationRevision, b.PlatformRuntimeRevision, b.ContextRevision,
                b.AcceptedSelectionHash, b.ExecutionFence, 1, 1, 1)
            {
                ModelCredentialReference = b.ModelCredentialReference,
                ModelBindingPin = b.ModelBindingPin,
                ModelSourceMode = b.ModelSourceMode, ModelConnectionId = b.ModelConnectionId,
                ModelConnectionScope = b.ModelConnectionScope, WorkflowStepId = b.WorkflowStepId,
                MaxModelTurns = b.MaxModelTurns, MaxToolCalls = b.MaxToolCalls,
                MaxPromptTokens = b.MaxPromptTokens, CopilotHardCreditLimit = b.CopilotHardCreditLimit
            };
            var placement = new EnvironmentRuntimeBootstrapContext(1, b.TenantId, b.ProjectId, b.RunId, b.EnvironmentId,
                1, b.EnvironmentCurrentFencingGeneration, b.EnvironmentProviderFencingGeneration, 1, Registration.ExpiresAt,
                new(ProviderSeam.Sandbox, b.PlacementProviderId!, b.PlacementUid, b.PlacementGeneration),
                new(Guid.NewGuid()), new("placement"), b.ProfileId, b.ConfigureEndpoint, b.ObservationEndpoint)
            { RuntimeOwnerContext = owner, Image = Image };
            Readiness = new(1, placement, Time.Now.AddSeconds(-4),
                new(placement.Resource, SandboxObservedState.Pending, placement.ProviderFencingGeneration, true, true, 1,
                    [new(SandboxStartupPhase.Scheduled, 1, Time.Now.AddSeconds(-3)),
                     new(SandboxStartupPhase.ImageReady, 1, Time.Now.AddSeconds(-2), Image.Digest, Image.CompressedPullBytes),
                     new(SandboxStartupPhase.Started, 1, Time.Now.AddSeconds(-1))]),
                new(120, 120, 120, 120, 120, 600), factory.WorkingDirectory);
            _http = new(new Handler(SendOwnerAsync));
            Host = new(new(b.ConfigureEndpoint, new("https://broker.test/"), new("https://orchestrator.test/"),
                    new("https://environment.test/"), new("https://events.test/"), Image, 8),
                _http, new RuntimeRegistrationHttpClient(_http, new("https://orchestrator.test/")), factory, Time,
                static (_, request, _) => Task.FromResult(CreateWorkspaceFlushReceipt(request)));
            Configure = new(1, Registration, Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(new { }));
        }
        public TestClock Time { get; }
        public string Token { get; }
        public Guid BootstrapGrantId => _bootstrap;
        public RuntimeActorAuthorization Actor { get; }
        public SandboxImageIdentity Image { get; }
        public RuntimeRegistration Registration { get; set; }
        public EnvironmentRuntimeReadinessContext Readiness { get; set; }
        public RuntimeAgentHost Host { get; }
        public RuntimeHostConfigureRequest Configure { get; private set; }
        public ControlledCopilotRuntime Sdk { get; }
        public string WorkingDirectory { get; }
        public string ConfigurationHash => _hash;
        public ConcurrentQueue<SessionMaterialWriteRequest> Material { get; } = new();
        public ImmutableArray<SessionEventEnvelope> SessionEvents { get; private set; } = [];
        public int PositionIncrement { get; set; } = 1;
        public int SessionEventPageSize { get; set; } = 100;
        public ConcurrentQueue<string> MaterialWriteRoutes { get; } = new();
        public ConcurrentQueue<Guid> ResumeCacheReads { get; } = new();
        public ConcurrentQueue<string> ResumeEventRequests { get; } = new();
        public ConcurrentQueue<string> ResumeMaterialReadRequests { get; } = new();
        public ConcurrentQueue<RuntimeUsageSourceReceipt> Usage { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnBeginRequest> NativeAdmissions { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnObservationRequest> NativeObservations { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnAccountingRequest> NativeAccounting { get; } = new();
        public ConcurrentQueue<Guid> CostTurnRequests { get; } = new();
        public string? NativeAccountingFault { get; set; }
        public string? NativeAdmissionFault { get; set; }
        public string? NativeRecordFault { get; set; }
        public Func<RuntimeHostSuspendRequest, RuntimeHostSuspendRequest>? SuspendEcho { get; set; }
        public HttpStatusCode SuspendStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<RuntimeHostSuspendRequest> SuspendChecks { get; } = new();
        public Func<RuntimeWorkspaceResumeRequest, RuntimeWorkspaceResumeRequest>? ResumeEcho { get; set; }
        public HttpStatusCode ResumeStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<RuntimeWorkspaceResumeRequest> ResumeChecks { get; } = new();
        public ConcurrentQueue<RuntimeResumeBootstrapRequest> ResumeBootstrapChecks { get; } = new();
        public TaskCompletionSource AccountingReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? BeforeAccounting { get; set; }
        public bool FailAccounting { get; set; }
        public bool MaterialPromptCapacityChanged { get; set; }
        public string? ResumeFault { get; set; }
        public Guid? ResumeProvenanceRuntimeInstanceId { get; set; }
        public Action<SessionMaterialWriteRequest>? AfterMaterialWrite { get; set; }
        public Action<SessionMaterialReadResult>? AfterMaterialRead { get; set; }
        public Guid? MissingMaterialId { get; set; }
        public int Accounted { get; private set; }
        public int Rotations { get; private set; }
        public int CostPreflightRequests { get; private set; }
        public bool CostPreflightPriced { get; set; } = true;
        public bool CostPreflightSourceHashMismatch { get; set; }
        public int SourceRevocations { get; private set; }

        public Guid FirstTurnEventId => _materialWrites.Values
            .Where(item => item.Kind == SessionMaterialKind.TurnContent)
            .OrderBy(item => _material[item.EventId].Material.Position)
            .Select(item => item.EventId)
            .First();
        public SessionMaterialReadResult ReadMaterial(Guid eventId) => _material[eventId];
        public bool HasMaterial(Guid eventId) => _material.ContainsKey(eventId);
        public async Task<Guid> RecordOneTurnAsync(CancellationToken token)
        {
            var readiness = await ConfigureAsync(token);
            await Host.SendAsync(Message(Proof(readiness)), Actor, token);
            return FirstTurnEventId;
        }
        public Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(CancellationToken token) =>
            Host.ReceiveAsync(new(Registration.RuntimeInstanceId,
                Configure.ResumeBootstrap?.BootstrapOperationId ?? Guid.NewGuid(), _bootstrap, _hash,
                new string('c', 64), Registration.ExpiresAt), Actor, token);
        public void UseResumeBootstrap(RuntimeResumeBootstrapRequest request)
        {
            request.Validate();
            using var emptyConfiguration = JsonDocument.Parse("null");
            Configure = new RuntimeHostConfigureRequest(
                1,
                request.Registration,
                request.ConsumeOperationId,
                request.ExchangeOperationId,
                emptyConfiguration.RootElement.Clone())
            {
                ResumeBootstrap = request
            };
        }
        public void ImportResumeSourceFrom(HostFixture source, Guid missingCacheId)
        {
            foreach (var (eventId, material) in source._material)
                _material[eventId] = material;
            _position = source._position;
            SessionEvents = source.SessionEvents;
            MissingMaterialId = missingCacheId;
        }
        public void ReplaceFirstTurnFrom(HostFixture owner)
        {
            var ownerWrite = owner._materialWrites.Values
                .Where(item => item.Kind == SessionMaterialKind.TurnContent)
                .OrderBy(item => owner._material[item.EventId].Material.Position)
                .First();
            var ownerMaterial = owner._material[ownerWrite.EventId];
            var currentTurn = SessionEvents
                .Where(item => item.Payload is TurnSessionPayload)
                .OrderBy(item => item.Position)
                .First();
            var relocated = ownerMaterial.Material with { Position = currentTurn.Position };
            _material[relocated.EventId] = ownerMaterial with { Material = relocated };
            var turn = new TurnSessionPayload(ownerWrite.Role!, relocated.Reference);
            SessionEvents = SessionEvents
                .Where(item => item.EventId != currentTurn.EventId)
                .Append(CreateEnvelope(relocated.Identity, currentTurn.Position, relocated.EventId, turn))
                .OrderBy(item => item.Position)
                .ToImmutableArray();
        }
        public void ReplaceVisibleParentEffectWithTurn(HostFixture owner)
        {
            var ownerWrite = owner._materialWrites.Values
                .Where(item => item.Kind == SessionMaterialKind.TurnContent)
                .OrderBy(item => owner._material[item.EventId].Material.Position)
                .First();
            var ownerMaterial = owner._material[ownerWrite.EventId];
            var placeholder = SessionEvents.Single(item =>
                item.Payload is AcceptedEffectSessionPayload { EffectId: "parent-effect" });
            var relocated = ownerMaterial.Material with { Position = placeholder.Position };
            _material[relocated.EventId] = ownerMaterial with { Material = relocated };
            var turn = new TurnSessionPayload(ownerWrite.Role!, relocated.Reference);
            SessionEvents = SessionEvents
                .Where(item => item.EventId != placeholder.EventId)
                .Append(CreateEnvelope(relocated.Identity, placeholder.Position, relocated.EventId, turn))
                .OrderBy(item => item.Position)
                .ToImmutableArray();
        }
        public void ImportUnlistedMaterialFrom(HostFixture owner, Guid eventId) =>
            _material[eventId] = owner._material[eventId];
        public void PrepareResumeJournal(RuntimeHostSuspendReceipt receipt, bool includeVisibleAncestor)
        {
            var identity = receipt.ConfigurationAcknowledgment!.Identity;
            var events = new List<SessionEventEnvelope>();
            foreach (var write in _materialWrites.Values)
            {
                var recorded = _material[write.EventId];
                if (recorded.Material.Position > receipt.CacheAcknowledgment.Position)
                    continue;
                SessionEventPayload? payload = write.Kind switch
                {
                    SessionMaterialKind.TurnContent => new TurnSessionPayload(
                        write.Role ?? throw new InvalidOperationException("Turn role is required."),
                        recorded.Material.Reference),
                    SessionMaterialKind.SdkCache => new CacheReferenceSessionPayload(
                        recorded.Material.Reference,
                        recorded.Material.Reference.Material!.RuntimeVersion,
                        "fixture-binding"),
                    SessionMaterialKind.RuntimeConfiguration => new RuntimeConfigurationSessionPayload(
                        recorded.Material.Reference),
                    _ => null
                };
                if (payload is not null)
                    events.Add(CreateEnvelope(
                        recorded.Material.Identity,
                        recorded.Material.Position,
                        recorded.Material.EventId,
                        payload));
            }

            if (includeVisibleAncestor)
            {
                var configuration = events.Single(item => item.Payload is RuntimeConfigurationSessionPayload);
                events.Add(CreateEnvelope(
                    new SessionIdentity(identity.ProjectId, identity.RunId, "visible-parent"),
                    configuration.Position + 1,
                    Guid.NewGuid(),
                    new AcceptedEffectSessionPayload("parent-effect", "recorded", null)));
                events.Add(CreateEnvelope(
                    identity,
                    receipt.CacheAcknowledgment.Position + 1,
                    Guid.NewGuid(),
                    new AcceptedEffectSessionPayload("post-boundary-effect", "recorded", null)));
            }
            SessionEvents = events.OrderBy(item => item.Position).ToImmutableArray();
        }
        public async Task<RuntimeHostReadinessReceipt> ConfigureAsync(CancellationToken token)
        {
            await DeliverAsync(token);
            return await Host.ConfigureAsync(Configure, Actor, token);
        }
        public RuntimeHostSessionProof Proof(RuntimeHostReadinessReceipt ready) =>
            new(1, Configure.Registration, ready.SourceGrant.GrantId, ready.SourceGrant.Revision, RuntimeCredentialPurpose.Observe);
        public RuntimeHostSuspendRequest SuspendRequest(RuntimeHostSessionProof proof, long phaseVersion = 1)
        {
            var binding = proof.Registration.Binding;
            var request = new RuntimeHostSuspendRequest(
                proof, Guid.NewGuid(), Guid.NewGuid(), phaseVersion);
            var spec = new WorkspaceVolumeSpec(
                "workspace",
                binding.ProjectId,
                new(WorkspaceVolumeOwnerKind.Run, binding.RunId),
                binding.EnvironmentId,
                WorkspaceVolumeBindingMode.Environment,
                WorkspaceVolumeAccessMode.ReadWriteOnce,
                8,
                "test-storage",
                WorkspaceVolumeConsistency.Strict,
                WorkspaceVolumeReclaimPolicy.Delete,
                WorkspaceVolumeOwnerDeletionPolicy.Delete,
                []);
            var flushRequest = new WorkspaceVolumeFlushRequest(
                spec,
                1,
                new(new(binding.TenantId, binding.ProjectId, binding.RunId, binding.EnvironmentId),
                    binding.EnvironmentLifecycleGeneration),
                new(ProviderSeam.Storage, "test-storage", "workspace-resource", 1),
                0,
                Guid.NewGuid().ToString("N"));
            return request with { WorkspaceFlush = flushRequest };
        }

        private static WorkspaceVolumeFlushReceipt CreateWorkspaceFlushReceipt(
            WorkspaceVolumeFlushRequest request) =>
            new WorkspaceVolumeFlushReceipt(
                1,
                request.EnvironmentFence,
                new(request.Spec.ProjectId, request.Spec.VolumeId, request.ResourceGeneration),
                request.Resource,
                request.ExpectedDataGeneration,
                checked(request.ExpectedDataGeneration + 1),
                request.IdempotencyKey,
                new string('a', 64),
                ProviderCheckpointId: null).ValidateFor(request);

        public RuntimeSessionMaterialHttpClient MaterialClient() => new(_http, new("https://events.test/"), Actor);
        public RuntimeUsageSourceHttpClient SourceClient() => new(_http, new("https://orchestrator.test/"), Actor);
        public async Task<AuthorizedRuntimeSession> ConfigureSessionAsync(
            RuntimeSessionMaterialHttpClient material, CancellationToken token)
        {
            var credential = new SecretCredential(new string('c', 64), Configure.Registration.ExpiresAt, Time);
            try
            {
                var owner = new RuntimeRegistrationHttpClient(_http, new("https://orchestrator.test/"));
                var broker = new RuntimeBrokerCredentialClient(
                    _http, new("https://broker.test/"), Configure.Registration.Binding.ActorIssuer, Actor, Time);
                var bootstrap = new RuntimeSessionBootstrap(owner, broker, RuntimeCopilotSessionTests.Factory(Sdk),
                    RuntimeSkillContentTests.Client(Configure.Registration, Actor),
                    Actor, Time, Image, material, new(_http, new("https://orchestrator.test/"), Actor));
                return await bootstrap.ConfigureAsync(Encoding.UTF8.GetBytes(Configure.Configuration.GetRawText()),
                    new(_bootstrap, Configure.Registration.RuntimeInstanceId, 1, RuntimeCredentialPurpose.Configure,
                        Configure.Registration.Binding.ConfigureEndpoint, _hash, credential),
                    Configure.ConsumeOperationId, Configure.ExchangeOperationId, token);
            }
            finally
            {
                credential.Invalidate();
            }
        }
        public async Task RequireCurrentSuspendAsync(RuntimeHostSuspendRequest request, CancellationToken token) =>
            Assert.Equal(request, await RuntimeOwnerHttpTransport.SendAsync<RuntimeHostSuspendRequest>(
                _http, new("https://orchestrator.test/"), "/internal/runtime/suspend/require-current",
                Actor, request, token));
        public RuntimeA2ASendRequest Message(RuntimeHostSessionProof proof,
            AddressedMessageDeliveryMode mode = AddressedMessageDeliveryMode.Enqueue) =>
            new(new("message", Guid.NewGuid(), RuntimeContractValidation.NativeSessionId(proof.Registration.Binding),
                "user", [new("text", "A bounded user request.")], new(proof, mode)));

        public Task<IHost> StartHttpAsync() => new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new()
                    {
                        ValidIssuer = "https://broker.test/", ValidAudience = "runtime-host",
                        IssuerSigningKey = _key, ClockSkew = TimeSpan.Zero,
                        ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                    };
                });
                services.AddAuthorization();
                services.ConfigureHttpJsonOptions(options =>
                    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapRuntimeAgentHost(Host, Time));
            });
        }).StartAsync();

        private RuntimeGrantReceipt Grant(Guid id, RuntimeCredentialPurpose purpose,
            RuntimeCredentialState state = RuntimeCredentialState.Active, long revision = 1)
        {
            var registration = Registration;
            return new(id, registration.RuntimeInstanceId, registration.Revision, revision,
                "https://broker.test/", purpose,
                purpose == RuntimeCredentialPurpose.Configure ? registration.Binding.ConfigureEndpoint :
                    registration.Binding.ObservationEndpoint, state, _hash,
                registration.ExpiresAt, Time.GetUtcNow());
        }

        private async Task<HttpResponseMessage> SendOwnerAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization?.Parameter != Token ||
                request.Headers.GetValues("X-Agentweaver-Tenant").Single() != "tenant")
                return Response(request, new { }, HttpStatusCode.Forbidden);
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/internal/runtime/registrations/", StringComparison.Ordinal))
            {
                if (path.EndsWith("/skills", StringComparison.Ordinal))
                    return Response(request, RuntimeSkillContentTests.EmptyProjection(Registration));
                return Response(request, Registration);
            }
            if (path.EndsWith("/readiness", StringComparison.Ordinal))
                return Response(request, Readiness);
            if (path == "/internal/runtime/suspend/require-current")
            {
                Assert.Equal("orchestrator.test", request.RequestUri.Host);
                Assert.Equal(HttpMethod.Post, request.Method);
                var suspend = await ReadAsync<RuntimeHostSuspendRequest>(request, token);
                SuspendChecks.Enqueue(suspend);
                return Response(request, SuspendEcho?.Invoke(suspend) ?? suspend, SuspendStatus);
            }
            if (path == "/internal/runtime/resume/require-current")
            {
                Assert.Equal("orchestrator.test", request.RequestUri.Host);
                Assert.Equal(HttpMethod.Post, request.Method);
                var resume = await ReadAsync<RuntimeWorkspaceResumeRequest>(request, token);
                ResumeChecks.Enqueue(resume);
                return Response(request, ResumeEcho?.Invoke(resume) ?? resume, ResumeStatus);
            }
            if (path == "/internal/runtime/resume/require-bootstrap-current")
            {
                Assert.Equal("orchestrator.test", request.RequestUri.Host);
                Assert.Equal(HttpMethod.Post, request.Method);
                var resume = await ReadAsync<RuntimeResumeBootstrapRequest>(request, token);
                ResumeBootstrapChecks.Enqueue(resume);
                return Response(request, resume);
            }
            if (path == "/internal/runtime/bootstrap/verify-pending")
                return Response(request, Grant(_bootstrap, RuntimeCredentialPurpose.Configure));
            if (path == "/internal/runtime/bootstrap/consume")
                return Response(request, Grant(_bootstrap, RuntimeCredentialPurpose.Configure, RuntimeCredentialState.Consumed, 2));
            if (path == "/internal/runtime/bootstrap/exchange")
                return Response(request, new RuntimeCredentialExchangeResponse(
                    Grant(_source, RuntimeCredentialPurpose.Observe), new string('d', 64), false));
            if (path == "/internal/runtime/source/verify")
                return Response(request, Grant(_source, RuntimeCredentialPurpose.Observe, revision: _sourceRevision));
            if (path == "/internal/runtime/source/rotate")
            {
                Rotations++;
                return Response(request, new RuntimeCredentialExchangeResponse(
                    Grant(_source, RuntimeCredentialPurpose.Observe, revision: ++_sourceRevision), new string('e', 64), false));
            }
            if (path == "/internal/runtime/source/revoke")
            {
                SourceRevocations++;
                return Response(request, Grant(_source, RuntimeCredentialPurpose.Observe, RuntimeCredentialState.Revoked, ++_sourceRevision));
            }
            if (path == "/internal/runtime/model-session/grant")
            {
                var binding = Configure.Registration.Binding;
                var byok = binding.ModelSourceMode == ModelSourceMode.Byok;
                _model = new("model-grant", 1, Configure.Registration.RuntimeInstanceId, Configure.Registration.Revision,
                    binding.ModelSelectionReference!, binding.ModelCredentialReference ?? new("copilot-user", "v1"),
                    RuntimeSecretPurposes.ModelSession, Configure.Registration.ExpiresAt)
                {
                    SourceMode = binding.ModelSourceMode, ConnectionId = binding.ModelConnectionId,
                    ConnectionRevision = byok ? null : 1,
                    CredentialKind = byok ? RuntimeModelCredentialKind.ByokKey : RuntimeModelCredentialKind.GitHubUserAccess
                };
                return Response(request, _model);
            }
            if (path == "/internal/runtime/model-session/redeem")
                return Response(request, new RuntimeModelCredentialResponse(_model!, _model!.ExpiresAt, Sdk.SdkCredential));
            if (path == "/internal/runtime/model-session/verify")
                return Response(request, _model!);
            if (path.Contains("/actions/", StringComparison.Ordinal))
            {
                if (path.EndsWith("/authorize", StringComparison.Ordinal))
                {
                    var action = await ReadAsync<RuntimeActionRequest>(request, token);
                    if (action.CostTurnId is { } costTurnId)
                        CostTurnRequests.Enqueue(costTurnId);
                    _admission = new(1, action, action.EventId.ToString("N"), "1", action.EventId,
                        PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed);
                }
                return Response(request, _admission!);
            }
            if (path.EndsWith("/cost-preflight", StringComparison.Ordinal))
            {
                var preflight = await ReadAsync<RuntimeUsageCostPreflightRequest>(request, token);
                if (preflight.ContractVersion != 1 || _facts is null)
                    throw new InvalidOperationException("The exact registered runtime source is required.");
                CostPreflightRequests++;
                var rateCard = new CostRateCard(
                    "fixture-rate-card", "1", _facts.MeterSource, "AIC", 1m,
                    ImmutableDictionary<string, decimal>.Empty.WithComparers(StringComparer.Ordinal)
                        .Add(_facts.ModelId, 1m));
                var costBinding = new CostBinding(
                    _facts.MeterSource, "fixture.cost", "1.0.0", 1, "fixture-revision",
                    "fixture-resource", 1,
                    ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal), rateCard);
                return Response(request, new RuntimeUsageCostPreflightReceipt(
                    1, Registration.RuntimeInstanceId, Registration.Revision,
                    Registration.Binding.ExecutionFence, _facts.ModelSelectionReference, _facts.ModelId,
                    _facts.MeterSource, _facts.AcceptedSelectionHash,
                    CostPreflightSourceHashMismatch
                        ? new string('f', 64)
                        : RuntimeUsageSourceReceiptContract.HashSource(Registration, _facts),
                    costBinding, CostPreflightPriced, CostPreflightPriced ? null : "fixture-unpriced"));
            }
            if (path.StartsWith("/internal/runtime/sources/", StringComparison.Ordinal))
            {
                var source = await ReadAsync<RuntimeSdkSourceRequest>(request, token);
                _facts = source.Source;
                return Response(request, new RuntimeSdkSourceReceipt(Configure.Registration.RuntimeInstanceId,
                    Configure.Registration.Revision, _source, _facts,
                    RuntimeUsageSourceReceiptContract.HashSource(Configure.Registration, _facts), Time.GetUtcNow()));
            }
            if (path == "/internal/runtime/turns/begin")
            {
                Assert.Equal(NativeAdmissions.Count, Sdk.Requests.Count(request => request.Method == "session.send"));
                var begin = await ReadAsync<RuntimeNativeTurnBeginRequest>(request, token);
                NativeAdmissions.Enqueue(begin);
                if (NativeAdmissionFault == "denied")
                    return Response(request, new { }, HttpStatusCode.Forbidden);
                var admission = new RuntimeNativeTurnAdmissionReceipt(Configure.Registration, _facts!,
                    begin.Message.Message.MessageId,
                    RuntimeContractValidation.Hash(Encoding.UTF8.GetBytes(begin.Message.Message.Parts[0].Text)),
                    RuntimeNativeTurnContract.RequestHash(begin.Message), 2);
                admission = NativeAdmissionFault switch
                {
                    "message" => admission with { MessageId = Guid.NewGuid() },
                    "prompt" => admission with { PromptHash = new string('e', 64) },
                    "revision" => admission with { OwnerRevision = 1 },
                    _ => admission
                };
                if (NativeAdmissionFault == "stale")
                    Registration = Registration with { Revision = 2 };
                return Response(request, admission);
            }
            if (path == "/internal/runtime/turns/observations")
            {
                var observed = await ReadAsync<RuntimeNativeTurnObservationRequest>(request, token);
                NativeObservations.Enqueue(observed);
                RuntimeNativeTurnContract.ValidateObservation(observed.Admission, observed.Observation);
                if (NativeRecordFault == "unavailable")
                    return Response(request, new { }, HttpStatusCode.ServiceUnavailable);
                return Response(request, new RuntimeNativeTurnRecordedReceipt(
                    observed.Admission, observed.Observation,
                    NativeRecordFault == "mismatch" ? new string('e', 64) :
                        RuntimeNativeTurnContract.ObservationHash(observed.Observation), 3));
            }
            if (path == "/internal/runtime/observations")
            {
                var observation = await ReadAsync<RuntimeUsageObservationRequest>(request, token);
                var usage = RuntimeUsageSourceReceiptContract.CreateUsage(Configure.Registration, _facts!, observation.Observation);
                var receipt = new RuntimeUsageSourceReceipt(1, Guid.NewGuid(), Configure.Registration, usage,
                    RuntimeUsageSourceReceiptContract.Hash(Configure.Registration, usage), Time.GetUtcNow());
                Usage.Enqueue(receipt);
                return Response(request, receipt);
            }
            if (path == "/internal/runtime/turns/accounting")
            {
                var accounted = await ReadAsync<RuntimeNativeTurnAccountingRequest>(request, token);
                NativeAccounting.Enqueue(accounted);
                RuntimeNativeTurnContract.AccountingSnapshotRequest(accounted.Recorded, accounted.RequiredReceipts);
                Assert.Equal(accounted.Recorded.Observation.UsageEventIds.Length, accounted.RequiredReceipts.Length);
                if (NativeAccountingFault == "unavailable")
                    return Response(request, new { }, HttpStatusCode.ServiceUnavailable);
                var binding = Configure.Registration.Binding;
                var snapshot = new RuntimeUsageCostSnapshotReceipt(1,
                    RuntimeUsageSourceReceiptContract.HashSource(Configure.Registration, _facts!), null,
                    new(null, null, CostDisposition.Unpriced, null, "controlled fixture"),
                    new(binding.TenantId, binding.ProjectId, binding.RunId, Usage.Count, false, [], []))
                {
                    DispatchId = accounted.Recorded.Admission.MessageId,
                    RepresentedReceipts = NativeAccountingFault == "missing" ? [] : accounted.RequiredReceipts
                };
                if (NativeAccountingFault == "changed")
                    snapshot = snapshot with
                    {
                        RepresentedReceipts = [accounted.RequiredReceipts[0] with
                        {
                            Accounting = accounted.RequiredReceipts[0].Accounting with { UnpricedReason = "changed" }
                        }]
                    };
                return Response(request, new RuntimeNativeTurnAccountingReceipt(accounted.Recorded, snapshot,
                    NativeAccountingFault == "revision" ? 3 : 4));
            }
            if (path.EndsWith("/usage-receipts", StringComparison.Ordinal))
            {
                AccountingReceived.TrySetResult();
                if (BeforeAccounting is not null)
                    await BeforeAccounting(token);
                if (FailAccounting)
                    return Response(request, new { }, HttpStatusCode.ServiceUnavailable);
                var id = await ReadAsync<RuntimeUsageReceiptReferenceRequest>(request, token);
                var usage = Usage.Single(receipt => receipt.ReceiptId == id.ReceiptId).Usage;
                Accounted++;
                return Response(request, new RuntimeUsageAccountingAcknowledgment(id.ReceiptId,
                    new(usage.EventId, usage.Attribution, new string('f', 64), CostDisposition.Unpriced,
                        null, null, "controlled fixture", null, null, Time.GetUtcNow()), false));
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                ResumeEventRequests.Enqueue(path);
                if (ResumeFault == "events-unauthorized")
                    return Response(request, new { }, HttpStatusCode.Unauthorized);
                if (ResumeFault == "events-forbidden")
                    return Response(request, new { }, HttpStatusCode.Forbidden);
                if (ResumeFault == "events-transport")
                    throw new HttpRequestException("Controlled Events transport failure.");
                if (ResumeFault == "cursor-no-progress")
                    return Response(request, new SessionEventPage([], "1", true));
                var after = 0L;
                foreach (var part in request.RequestUri.Query.TrimStart('?')
                             .Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var pair = part.Split('=', 2);
                    if (pair[0] == "cursor" && pair.Length == 2)
                        after = long.Parse(
                            Uri.UnescapeDataString(pair[1]), NumberStyles.None, CultureInfo.InvariantCulture);
                }
                var page = SessionEvents.Where(item => item.Position > after)
                    .Take(SessionEventPageSize + 1).ToArray();
                var hasMore = page.Length > SessionEventPageSize;
                if (hasMore)
                    page = page[..SessionEventPageSize];
                var cursor = page.Length == 0
                    ? null
                    : page[^1].Position.ToString(CultureInfo.InvariantCulture);
                if (ResumeFault == "cursor-repeated" && after > 0 && SessionEvents.Length > SessionEventPageSize)
                    cursor = SessionEvents.Take(SessionEventPageSize).Last().Position
                        .ToString(CultureInfo.InvariantCulture);
                return Response(request, new SessionEventPage(page.ToImmutableArray(), cursor, hasMore));
            }
            if (path == "/internal/runtime/resume/configuration")
            {
                var resume = await ReadAsync<RuntimeResumeBootstrapRequest>(request, token);
                var acknowledgment = RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(
                    resume.Resume.SourceRelease.HostReceipt);
                if (acknowledgment.EventId == MissingMaterialId ||
                    !_material.TryGetValue(acknowledgment.EventId, out var configuration))
                    return Response(request, new { }, HttpStatusCode.NotFound);
                return Response(request, configuration);
            }
            if (path.EndsWith("/material", StringComparison.Ordinal) ||
                path.EndsWith("/runtime-configuration", StringComparison.Ordinal))
            {
                var material = await ReadAsync<SessionMaterialWriteRequest>(request, token);
                var dedicatedConfigurationRoute = path.EndsWith("/runtime-configuration", StringComparison.Ordinal);
                if (dedicatedConfigurationRoute !=
                    (material.Kind == SessionMaterialKind.RuntimeConfiguration))
                    return Response(request, new { }, HttpStatusCode.Forbidden);
                if (_material.TryGetValue(material.EventId, out var existing))
                {
                    if (!_materialWrites.TryGetValue(material.EventId, out var previous) ||
                        !RuntimeWorkspaceResumeHttpClient.SameJson(previous, material))
                        return Response(request, new { code = "session_event_conflict" }, HttpStatusCode.Conflict);
                    return Response(request, existing.Material);
                }
                Material.Enqueue(material);
                MaterialWriteRoutes.Enqueue(path);
                var b = Configure.Registration.Binding;
                var receipt = new SessionMaterialAcknowledgment(1, new(b.ProjectId, b.RunId, b.SessionId),
                    material.EventId, Interlocked.Add(ref _position, PositionIncrement),
                    new(new ObjectKey(material.EventId.ToString("N")), SessionMaterialValidation.Purpose(material.Kind),
                        material.Bytes.Length)
                    {
                        Material = new(1, material.Kind, b.TenantId, RuntimeContractValidation.Hash(material.Bytes),
                            Configure.Registration.RuntimeInstanceId, Configure.Registration.Revision, b.ExecutionFence,
                            b.AcceptedSelectionHash, _facts!.SdkVersion, _facts.RuntimeVersion,
                            _facts.ModelSelectionReference, _facts.ModelId)
                        {
                            MaxPromptTokens = MaterialPromptCapacityChanged &&
                                material.Kind != SessionMaterialKind.RuntimeConfiguration
                                ? (_facts.MaxPromptTokens ?? 1024) + 1 : _facts.MaxPromptTokens
                        }
                    });
                _material[material.EventId] = new(receipt, material.Bytes);
                _materialWrites[material.EventId] = material;
                AfterMaterialWrite?.Invoke(material);
                return Response(request, receipt);
            }
            if (path.Contains("/material/", StringComparison.Ordinal))
            {
                ResumeMaterialReadRequests.Enqueue(path);
                var id = Guid.Parse(path.Split('/')[5]);
                if (path.EndsWith("/SdkCache", StringComparison.Ordinal))
                    ResumeCacheReads.Enqueue(id);
                if (id == MissingMaterialId || !_material.TryGetValue(id, out var recorded))
                    return Response(request, new { }, HttpStatusCode.NotFound);
                AfterMaterialRead?.Invoke(recorded);
                var result = recorded;
                if (path.EndsWith("/TurnContent", StringComparison.Ordinal))
                {
                    var material = recorded.Material;
                    var reference = material.Reference;
                    var binding = reference.Material!;
                    result = ResumeFault switch
                    {
                        "material-bytes" => recorded with
                        {
                            Bytes = CorruptFirstByte(recorded.Bytes)
                        },
                        "material-reference" => recorded with
                        {
                            Material = material with
                            {
                                Reference = reference with
                                {
                                    Key = new ObjectKey(Guid.NewGuid().ToString("N"))
                                }
                            }
                        },
                        "material-hash" => recorded with
                        {
                            Material = material with
                            {
                                Reference = reference with
                                {
                                    Material = binding with
                                    {
                                        Sha256 = binding.Sha256[0] == 'a'
                                            ? 'b' + binding.Sha256[1..]
                                            : 'a' + binding.Sha256[1..]
                                    }
                                }
                            }
                        },
                        "material-provenance" => recorded with
                        {
                            Material = material with
                            {
                                Reference = reference with
                                {
                                    Material = binding with
                                    {
                                        RuntimeInstanceId = ResumeProvenanceRuntimeInstanceId
                                            ?? throw new InvalidOperationException(
                                                "A forged resume material binding is required.")
                                    }
                                }
                            }
                        },
                        _ => recorded
                    };
                }
                return Response(request, result);
            }
            throw new InvalidOperationException("Unexpected fixture owner route: " + path);
        }
        private RuntimeActionAdmission? _admission;
        private static async Task<T> ReadAsync<T>(HttpRequestMessage request, CancellationToken token) =>
            (await request.Content!.ReadFromJsonAsync<T>(Json, token))!;
        private static HttpResponseMessage Response<T>(HttpRequestMessage request, T body,
            HttpStatusCode status = HttpStatusCode.OK) =>
            new(status)
            {
                RequestMessage = request, Content = JsonContent.Create(body, options: Json),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
            };
        private static byte[] CorruptFirstByte(byte[] bytes)
        {
            var corrupted = bytes.ToArray();
            corrupted[0] ^= 1;
            return corrupted;
        }
        private static SessionEventEnvelope CreateEnvelope(
            SessionIdentity identity,
            long position,
            Guid eventId,
            SessionEventPayload payload)
        {
            var references = SessionEventPayloadValidation.ValidateAndGetReferences(payload)
                .Select(reference => new StoredSessionObjectReference(
                    reference, new SessionObjectRetention("fixture", DateTimeOffset.MaxValue)))
                .ToImmutableArray();
            var json = JsonSerializer.Serialize(new
            {
                schemaVersion = SessionsContractVersions.CurrentSchemaVersion,
                eventVersion = SessionsContractVersions.CurrentEventVersion,
                eventId,
                identity,
                position,
                occurredAt = DateTimeOffset.UnixEpoch,
                payload,
                objectReferences = references
            }, Json);
            return JsonSerializer.Deserialize<SessionEventEnvelope>(json, Json)
                ?? throw new InvalidOperationException("The fixture event envelope could not be created.");
        }
        public async ValueTask DisposeAsync()
        {
            try { await Host.DisposeAsync(); }
            finally
            {
                Actor.Bearer.Invalidate();
                _http.Dispose();
                await Sdk.DisposeAsync();
            }
        }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
