using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed partial class ProjectsConfigBrokerAuthorizationTests
{
    private sealed class DropCommittedMaterialResponseHandler(HttpMessageHandler inner, Guid eventId)
        : DelegatingHandler(inner)
    {
        private bool _dropped;
        public SessionMaterialAcknowledgment? CommittedAcknowledgment { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!_dropped && request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/material", StringComparison.Ordinal) == true &&
                (await request.Content!.ReadAsStringAsync(cancellationToken)).Contains(
                    eventId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                response.IsSuccessStatusCode)
            {
                // The owner committed the original event. Drop only its acknowledgment;
                // the fresh-authority retry must use that exact event identity and bytes.
                _dropped = true;
                CommittedAcknowledgment =
                    await response.Content.ReadFromJsonAsync<SessionMaterialAcknowledgment>(
                        CoordinationJsonOptions, cancellationToken)
                    ?? throw new HttpRequestException("The owner returned no committed acknowledgment.");
                response.Dispose();
                throw new HttpRequestException("Committed material acknowledgment was lost.");
            }
            return response;
        }
    }

    private async Task VerifyReplacementNativeRecoveryAsync(
        AuthorizedRuntimeSession original, RuntimeActorAuthorization actor,
        ProjectsConfigResourceServer projects, ConcurrentQueue<string> failures,
        ControlledCopilotRuntime originalSdk, string eventsSchema, SecurityKey signingKey,
        Dictionary<string, Func<HttpMessageHandler>> routes, RuntimeModelBindingsResolver modelBindings,
        RuntimeMaterialObjects objects,         RuntimeSessionRecovery previous, bool missingCache,
        CancellationToken cancellationToken)
    {
        // Recreate the Events HTTP owner over retained PostgreSQL and object data so recovery cannot use process memory.
        await using var owner = new EventsIntegrationFactory(
            _connectionString, eventsSchema, signingKey, projects.CreateHandler,
            () => new RuntimeServiceRouter(routes, failures),
            nativeUsageHandler: () => new RuntimeServiceRouter(routes, failures),
            sessionMaterialObjects: objects);
        using var client = owner.CreateDefaultClient(new Uri("https://events.test/"));
        var material = new RuntimeSessionMaterialHttpClient(client, client.BaseAddress!, actor);
        var recovery = await material.ReadRecoveryAsync(original.Registration, cancellationToken);
        Assert.Equal(previous.Identity, recovery.Identity);
        Assert.Equal(previous.TenantId, recovery.TenantId);
        Assert.Equal(previous.Turns, recovery.Turns);
        Assert.Equal(previous.JournalEvidence, recovery.JournalEvidence);
        Assert.NotNull(recovery.JournalEvidence);
        Assert.True(recovery.JournalEvidence.ThroughPosition > 0);
        if (!missingCache)
            Assert.True(recovery.JournalEvidence.ThroughPosition >= previous.Cache!.Material.Position);
        Assert.Equal(previous.Cache?.Material, recovery.Cache?.Material);
        Assert.Equal(previous.Cache?.Bytes, recovery.Cache?.Bytes);

        // Recreate the SDK transport and consume only bytes returned by the authenticated owner.
        await using var replacement = new ControlledCopilotRuntime
        {
            Byok = originalSdk.Byok,
            ModelId = originalSdk.ModelId,
            SdkCredential = originalSdk.SdkCredential,
            ExpectedAvailableToolsCount = 7
        };
        var factory = new RuntimeCopilotSessionFactory(replacement.Connection,
            Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
            modelBindings);
        var credential = new SecretCredential(originalSdk.SdkCredential, DateTimeOffset.UtcNow.AddMinutes(2));
        await using var session = await factory.CreateAsync(
            original.Registration, original.Registration.Binding.ModelSelectionReference!, credential,
            _ => Task.CompletedTask, cancellationToken, recovery,
            (_, _) => Task.CompletedTask);
        Assert.Equal(original.Facts.SdkSessionId, session.Facts.SdkSessionId);
        Assert.Equal(original.Registration.Revision, session.Facts.RegistrationRevision);
        Assert.Equal(original.Registration.Binding.AcceptedSelectionHash, session.Facts.AcceptedSelectionHash);
        Assert.Equal(original.Facts.ModelId, session.Facts.ModelId);
        Assert.Equal(original.Facts.SdkVersion, session.Facts.SdkVersion);
        Assert.Equal(original.Facts.RuntimeVersion, session.Facts.RuntimeVersion);
        Assert.Equal(missingCache ? RuntimeSessionRecoveryMode.JournalRebuild :
            RuntimeSessionRecoveryMode.NativeCache, session.RecoveryMode);
        Assert.Equal(missingCache ? "runtime_sdk_cache_missing" : null, session.RecoveryReason);
        if (missingCache)
        {
            // An absent optional cache rebuilds only the existing bounded journal context, without resuming native state.
            var create = Assert.Single(replacement.Requests, request => request.Method == "session.create");
            var context = create.Parameters.GetProperty("systemMessage").GetProperty("content").GetString();
            Assert.Contains("A bounded user request.", context);
            Assert.Contains(originalSdk.AssistantResponse, context);
            Assert.DoesNotContain(replacement.Requests, request => request.Method == "session.resume");
            Assert.Equal(recovery.JournalEvidence, session.JournalEvidence);
        }

        else
        {
            // A compatible cache must be consumed through the SDK filesystem callback, not inferred from its ID.
            Assert.Contains(replacement.Requests, request => request.Method == "session.resume");
            Assert.DoesNotContain(replacement.Requests, request => request.Method == "session.create");
            var restored = await replacement.InvokeNativeFilesAsync(
                "sessionFs.readFile", "state/events.jsonl", null, cancellationToken);
            Assert.Contains(originalSdk.AssistantResponse, restored.GetProperty("content").GetString());
            Assert.Equal(recovery.Cache!.Bytes, await session.CaptureNativeCacheAsync(cancellationToken));
        }
        Assert.DoesNotContain(replacement.Requests, request => request.Method == "session.send");
    }
}

public sealed partial class RuntimeSessionRecoveryTests
{
    private static readonly JsonSerializerOptions ResumeJournalJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Theory]
    [InlineData("terminal")]
    [InlineData("beyond-boundary")]
    public async Task ResumeJournalRebuildRejectsAnUnobservedCheckpointBoundary(string ending)
    {
        // Keep the durable cache at position three so the fixtures differ only in how the page scan ends.
        var request = CreateResumeJournalRequest();
        var configuration = ConfigurationEvent(request);
        var handler = ending == "terminal"
            ? new ResumeJournalPageHandler(new SessionEventPage([configuration], null, false))
            : new ResumeJournalPageHandler(
                new SessionEventPage([configuration], "after-configuration", true),
                new SessionEventPage([JournalEvent(request, Guid.NewGuid(), 4,
                    new AcceptedEffectSessionPayload("after-boundary", "recorded", null))], null, false));
        using var client = new HttpClient(handler);
        var actor = ResumeJournalActor(request.Registration);
        var material = new RuntimeSessionMaterialHttpClient(client, new("https://events.test/"), actor);

        // A terminal page or a later event cannot prove that the exact checkpoint position was read.
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            material.ReadJournalRebuildAsync(request.Registration, request, CancellationToken.None));
        Assert.Equal("runtime_resume_journal_incomplete", failure.Code);
        Assert.Equal(ending == "terminal" ? 1 : 2, handler.Reads);
    }

    [Fact]
    public async Task ResumeJournalRebuildHashesTheCompletePaginatedPrefixOnly()
    {
        // Split the exact checkpoint prefix across pages and place a later event beside its final entry.
        var request = CreateResumeJournalRequest();
        var configuration = ConfigurationEvent(request);
        var cache = CacheEvent(request);
        var afterBoundary = JournalEvent(request, Guid.NewGuid(), 4,
            new AcceptedEffectSessionPayload("after-boundary", "recorded", null));
        var handler = new ResumeJournalPageHandler(
            new SessionEventPage([configuration], "after-configuration", true),
            new SessionEventPage([cache, afterBoundary], null, false));
        using var client = new HttpClient(handler);
        var material = new RuntimeSessionMaterialHttpClient(
            client, new("https://events.test/"), ResumeJournalActor(request.Registration));

        var recovery = await material.ReadJournalRebuildAsync(
            request.Registration, request, CancellationToken.None);

        // Matching only the required prefix proves the post-boundary event did not enter the digest.
        Assert.Equal(request.Resume.SourceManifest.FlushedJournalPosition,
            recovery.JournalRebuild?.ThroughPosition);
        Assert.Equal(RuntimeResumeJournalRebuildReceipt.ComputeJournalSha256(
            request, [configuration, cache]), recovery.JournalRebuild?.JournalSha256);
        Assert.Equal(2, handler.Reads);
    }

    [Theory]
    [InlineData("valid", null)]
    [InlineData("absent", "runtime_owner_unavailable")]
    [InlineData("corrupt", "runtime_material_read_invalid")]
    [InlineData("denied", "runtime_owner_denied")]
    [InlineData("binding", "runtime_owner_binding_changed")]
    [InlineData("excessive", "runtime_journal_turn_invalid")]
    public async Task OwnerJournalTurnMustRemainCompleteAuthorizedAndBounded(
        string condition, string? expectedFailure)
    {
        // Exercise the owner-read boundary with ordered events and real material acknowledgments,
        // then vary one missing, damaged, revoked, rebound, or oversized condition.
        var registration = RuntimeCopilotSessionTests.Registration();
        var identity = new SessionIdentity(registration.Binding.ProjectId,
            registration.Binding.RunId, registration.Binding.SessionId);
        Guid[] eventIds = condition == "excessive" ? [Guid.NewGuid(), Guid.NewGuid()] : [Guid.NewGuid()];
        var content = condition == "excessive" ? new string('x', 600_000) : "recorded turn";
        var bytes = Encoding.UTF8.GetBytes(content);
        var facts = new SdkSessionFacts(registration.RuntimeInstanceId, "native-session",
            "sdk", "runtime", registration.Binding.ModelSelectionReference!, "controlled-model",
            new string('a', 64), null, "hosted-copilot", SdkMeterSources.CopilotNanoAiu,
            registration.Binding.AcceptedSelectionHash, registration.Revision);
        var material = Cache(registration, facts, bytes);
        var turnReference = material.Material.Reference with
        {
            Purpose = SessionMaterialValidation.Purpose(SessionMaterialKind.TurnContent),
            Material = material.Material.Reference.Material! with
            {
                Kind = SessionMaterialKind.TurnContent,
                Sha256 = RuntimeContractValidation.Hash(bytes)
            }
        };
        var recorded = eventIds.Select((id, index) =>
            material with
            {
                Material = material.Material with
                {
                    EventId = id,
                    Position = index + 1,
                    Reference = turnReference
                },
                Bytes = condition == "corrupt" ? "altered"u8.ToArray() : bytes
            }).ToArray();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var page = JsonSerializer.SerializeToUtf8Bytes(new
        {
            events = eventIds.Select((id, index) => new
            {
                schemaVersion = 1, eventVersion = 2, eventId = id, identity,
                position = index + 1, occurredAt = DateTimeOffset.UtcNow,
                kind = SessionEventKind.Turn,
                payload = (SessionEventPayload)new TurnSessionPayload("user", turnReference),
                objectReferences = ImmutableArray<StoredSessionObjectReference>.Empty
            }).ToArray(),
            nextCursor = (string?)null, hasMore = false
        }, options);
        var reads = 0;
        using var client = new HttpClient(new MaterialOwnerHandler(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(registration.Binding.TenantId,
                Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            if (request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal))
                return MaterialOwnerHandler.Json(page);
            reads++;
            // Missing, revoked, and rebound required material must remain explicit owner failures.
            if (condition is "absent" or "denied" or "binding")
                return MaterialOwnerHandler.Status(condition switch
                {
                    "absent" => HttpStatusCode.NotFound,
                    "denied" => HttpStatusCode.Forbidden,
                    _ => HttpStatusCode.Conflict
                });
            var id = Guid.Parse(request.RequestUri.Segments[^2].TrimEnd('/'));
            return MaterialOwnerHandler.Json(JsonSerializer.SerializeToUtf8Bytes(
                Assert.Single(recorded, item => item.Material.EventId == id), options));
        }));
        var actor = new RuntimeActorAuthorization(
            new SecretCredential("owner-test-token", DateTimeOffset.UtcNow.AddMinutes(2)),
            registration.Binding.TenantId);
        var owner = new RuntimeSessionMaterialHttpClient(client, new("https://events.test/"), actor);
        if (expectedFailure is null)
        {
            var recovery = await owner.ReadRecoveryAsync(registration, CancellationToken.None);
            Assert.Equal(("user", content), Assert.Single(recovery.Turns));
            Assert.Equal(1, recovery.JournalEvidence?.ThroughPosition);
            Assert.NotNull(recovery.PreviousModelBinding);
            Assert.Null(recovery.Cache);
        }
        else
        {
            // Never turn incomplete history or an owner denial into an apparently successful rebuild.
            var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                owner.ReadRecoveryAsync(registration, CancellationToken.None));
            Assert.Equal(expectedFailure, failure.Code);
        }
        Assert.Equal(condition == "excessive" ? 2 : 1, reads);
    }

    private static RuntimeResumeBootstrapRequest CreateResumeJournalRequest()
    {
        // Construct source-owned cache, configuration, turn, and flush receipts accepted by the resume contracts.
        var now = DateTimeOffset.UtcNow;
        var sourceRegistration = RuntimeCopilotSessionTests.Registration();
        sourceRegistration = sourceRegistration with
        {
            Binding = sourceRegistration.Binding with
            {
                EnvironmentLifecycleGeneration = 1,
                EnvironmentCurrentFencingGeneration = 1,
                EnvironmentProviderFencingGeneration = 1,
                EnvironmentLeaseRevision = 1,
                PlacementProviderId = "agent-sandbox"
            }
        };
        var identity = new SessionIdentity(
            sourceRegistration.Binding.ProjectId, sourceRegistration.Binding.RunId,
            sourceRegistration.Binding.SessionId);
        var facts = new SdkSessionFacts(
            sourceRegistration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(sourceRegistration.Binding),
            "1.0.18", "1.0.97", sourceRegistration.Binding.ModelSelectionReference!,
            "controlled-model", new string('a', 64), null, "hosted-copilot",
            SdkMeterSources.CopilotNanoAiu, sourceRegistration.Binding.AcceptedSelectionHash,
            sourceRegistration.Revision);
        var cacheAcknowledgment = Cache(sourceRegistration, facts, "native cache"u8.ToArray()).Material;
        var configurationBytes = "{}"u8.ToArray();
        var configurationHash = RuntimeContractValidation.Hash(configurationBytes);
        var configurationReference = cacheAcknowledgment.Reference with
        {
            Key = new ObjectKey("recorded-runtime-configuration"),
            Purpose = SessionMaterialValidation.Purpose(SessionMaterialKind.RuntimeConfiguration),
            ByteLength = configurationBytes.Length,
            Material = cacheAcknowledgment.Reference.Material! with
            {
                Kind = SessionMaterialKind.RuntimeConfiguration,
                Sha256 = configurationHash
            }
        };
        var configurationAcknowledgment = new SessionMaterialAcknowledgment(
            1, identity, Guid.NewGuid(), cacheAcknowledgment.Position - 1, configurationReference);
        var admission = new RuntimeNativeTurnAdmissionReceipt(
            sourceRegistration, facts, Guid.NewGuid(), new string('a', 64), new string('b', 64), 2);
        var observation = new RuntimeNativeTurnObservation(
            facts, Guid.NewGuid().ToString("D"), "native-turn", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            now, "native-cursor", new string('c', 64), null);
        var nativeTurn = new RuntimeNativeTurnRecordedReceipt(
            admission, observation, RuntimeNativeTurnContract.ObservationHash(observation), 3);
        var environmentFence = new EnvironmentGenerationFence(
            new("tenant", identity.ProjectId, identity.RunId, sourceRegistration.Binding.EnvironmentId), 1);
        var workspaceVolume = new WorkspaceVolumeReference(identity.ProjectId, "workspace", 1);
        var workspaceResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "claim-1", 1);
        var flush = new WorkspaceVolumeFlushReceipt(
            1, environmentFence, workspaceVolume, workspaceResource, 7, 8,
            "flush-once", new string('e', 64), null);
        var host = new RuntimeHostSuspendReceipt(
            1, Guid.NewGuid(), cacheAcknowledgment.EventId, sourceRegistration,
            new(Guid.NewGuid(), sourceRegistration.RuntimeInstanceId, sourceRegistration.Revision, 1,
                sourceRegistration.Binding.ActorIssuer, RuntimeCredentialPurpose.Observe,
                sourceRegistration.Binding.ObservationEndpoint, RuntimeCredentialState.Active,
                configurationHash, sourceRegistration.ExpiresAt, now),
            nativeTurn, cacheAcknowledgment)
        {
            WorkspaceFlush = flush,
            ConfigurationAcknowledgment = configurationAcknowledgment
        };
        var sourceRelease = new RuntimeSuspendPlacementReleaseRequest(host, 5).Validate();
        var checkpoint = new SessionSuspendResumeCheckpointReference(
            identity, SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
            "checkpoint", 1, "plan", 1);
        var manifest = new SessionSuspendResumeManifest(
            1, host.ManifestId, identity, SessionSuspendResumeManifestState.Suspended,
            environmentFence, sourceRegistration.Binding.ExecutionFence, cacheAcknowledgment, checkpoint,
            cacheAcknowledgment.Position, workspaceVolume, workspaceResource, flush.DataGeneration,
            flush.TreeSha256, null, null, null, 1, ImmutableArray<string>.Empty).Validate();
        var operationId = Guid.NewGuid();
        var restoredWorkspace = CreateRestoredWorkspace(
            sourceRegistration, manifest, sourceRelease, operationId, now.AddMinutes(5));
        var resume = new RuntimeWorkspaceResumeRequest(
            1, operationId, 4, manifest, sourceRelease) { RestoredWorkspace = restoredWorkspace }.Validate();
        var attemptId = RuntimeResumeAttemptIdentity.Initial(operationId);
        var placement = restoredWorkspace.Placement;
        var image = new SandboxImageIdentity("sha256:" + new string('b', 64), "linux/amd64", 1);
        placement = placement with { Image = image };
        resume = resume with
        {
            RestoredWorkspace = restoredWorkspace with { Placement = placement }
        };
        var targetBinding = sourceRegistration.Binding with
        {
            ExecutionFence = sourceRegistration.Binding.ExecutionFence + 1,
            EnvironmentId = placement.EnvironmentId,
            PlacementUid = placement.Resource.ResourceId,
            PlacementGeneration = placement.Resource.Generation,
            ProfileId = placement.ProfileId,
            ConfigureEndpoint = placement.ConfigureEndpoint,
            ObservationEndpoint = placement.ObservationEndpoint,
            PlacementProviderId = placement.Resource.ProviderId,
            EnvironmentLifecycleGeneration = placement.LifecycleGeneration,
            EnvironmentLeaseRevision = placement.LeaseRevision,
            EnvironmentCurrentFencingGeneration = placement.CurrentFencingGeneration,
            EnvironmentProviderFencingGeneration = placement.ProviderFencingGeneration,
            Image = image,
            ResumeOperationId = operationId,
            ResumeAttemptId = attemptId
        };
        var targetRegistration = new RuntimeRegistration(
            Guid.NewGuid(), 1, targetBinding, RuntimeRegistrationState.Active, placement.LeaseExpiresAt);
        return new RuntimeResumeBootstrapRequest(
            1, resume, targetRegistration,
            RuntimeResumeAttemptIdentity.Bootstrap(operationId, attemptId),
            RuntimeResumeAttemptIdentity.Consume(operationId, attemptId),
            RuntimeResumeAttemptIdentity.Exchange(operationId, attemptId))
        {
            ResumeAttemptId = attemptId
        }.Validate();
    }

    private static EnvironmentRuntimeWorkspaceContext CreateRestoredWorkspace(
        RuntimeRegistration sourceRegistration, SessionSuspendResumeManifest manifest,
        RuntimeSuspendPlacementReleaseRequest sourceRelease, Guid operationId, DateTimeOffset leaseExpiresAt)
    {
        // Give the replacement a new Environment generation and storage attachment to the flushed source data.
        var sourceFence = manifest.EnvironmentFence!;
        var targetFence = new EnvironmentGenerationFence(
            sourceFence.Owner, checked(sourceFence.LifecycleGeneration + 1));
        var resource = new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", "claim-2", 2);
        var configureEndpoint =
            new Uri("https://runtime-receiver.agentweaver.svc.cluster.local:8443/runtime/v1/configure");
        var placement = new EnvironmentRuntimeBootstrapContext(
            1, targetFence.Owner.TenantId, targetFence.Owner.ProjectId, targetFence.Owner.RunId,
            targetFence.Owner.EnvironmentId, targetFence.LifecycleGeneration, 3, 3, 1, leaseExpiresAt,
            resource, new(Guid.NewGuid()), new("cluster"), sourceRegistration.Binding.ProfileId!,
            configureEndpoint, sourceRegistration.Binding.ObservationEndpoint)
        {
            RuntimeEndpoint = new SandboxRuntimeHttpsEndpoint(
                new("https://sandbox-2.agentweaver.svc.cluster.local:8443/"), "10.2.3.4", "pod-2", "service-2")
            {
                ReceiverConfigureEndpoint = configureEndpoint
            }
        };
        return new EnvironmentRuntimeWorkspaceContext(
            1, placement,
            new("project", "environment", "run", manifest.WorkspaceVolume!, resource,
                manifest.WorkspaceResource!, targetFence, manifest.WorkspaceDataGeneration!.Value,
                "/workspace/agentweaver/workspace", false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            checked(sourceRelease.FlushTransitionRevision + 2));
    }

    private static RuntimeActorAuthorization ResumeJournalActor(RuntimeRegistration registration) =>
        new(new SecretCredential("owner-test-token", DateTimeOffset.UtcNow.AddMinutes(2)),
            registration.Binding.TenantId);

    private static SessionEventEnvelope ConfigurationEvent(RuntimeResumeBootstrapRequest request)
    {
        var acknowledgment =
            RuntimeResumeBootstrapRequest.RequireConfigurationAcknowledgment(request.Resume.SourceRelease.HostReceipt);
        return JournalEvent(request, acknowledgment.EventId, acknowledgment.Position,
            new RuntimeConfigurationSessionPayload(acknowledgment.Reference));
    }

    private static SessionEventEnvelope CacheEvent(RuntimeResumeBootstrapRequest request)
    {
        var acknowledgment = request.Resume.SourceManifest.CacheAcknowledgment!;
        return JournalEvent(request, acknowledgment.EventId, acknowledgment.Position,
            new CacheReferenceSessionPayload(
                acknowledgment.Reference, acknowledgment.Reference.Material!.RuntimeVersion, "binding"));
    }

    private static SessionEventEnvelope JournalEvent(
        RuntimeResumeBootstrapRequest request, Guid eventId, long position, SessionEventPayload payload)
    {
        // Use the same polymorphic JSON contract as the Events owner so production deserialization is exercised.
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            eventVersion = 2,
            eventId,
            identity = request.Resume.SourceManifest.Identity,
            position,
            occurredAt = DateTimeOffset.UnixEpoch,
            payload,
            objectReferences = Array.Empty<StoredSessionObjectReference>()
        }, ResumeJournalJsonOptions);
        return JsonSerializer.Deserialize<SessionEventEnvelope>(json, ResumeJournalJsonOptions)
            ?? throw new InvalidOperationException("The fixture journal event did not deserialize.");
    }

    private sealed class MaterialOwnerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        internal static HttpResponseMessage Json(byte[] bytes) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers = { ContentType = new("application/json") }
            },
            Headers = { CacheControl = new() { NoStore = true } }
        };

        internal static HttpResponseMessage Status(HttpStatusCode status) => new(status)
        {
            Headers = { CacheControl = new() { NoStore = true } }
        };
    }

    private sealed class ResumeJournalPageHandler(params SessionEventPage[] pages) : HttpMessageHandler
    {
        private readonly Queue<SessionEventPage> _pages = new(pages);
        public int Reads { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Only the authenticated Events owner route may supply recovery pages.
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/internal/sessions/session/events", request.RequestUri!.AbsolutePath);
            Assert.Equal("owner-test-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            if (!_pages.TryDequeue(out var page))
                throw new InvalidOperationException("The runtime requested more journal pages than the fixture provides.");
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(page, options: ResumeJournalJsonOptions),
                Headers = { CacheControl = new() { NoStore = true } }
            });
        }
    }
}
