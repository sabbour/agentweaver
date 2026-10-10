using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Xunit;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafExecutionDispatchTests
{
    [Fact]
    public void NativePreparationBindsTheCurrentPlatformIntentWithoutPredictingNativeIds()
    {
        var (root, checkpoint, intent, registration, source, message) = CreateNativePreparation();
        RuntimeUsageSourceStore.ValidateNativePreparation(root, checkpoint, intent, registration, source, message);
        Assert.Equal(intent.MessageId, message.Message.MessageId);
        Assert.Equal(intent.PromptHash, RuntimeContractValidation.Hash(
            System.Text.Encoding.UTF8.GetBytes(message.Message.Parts[0].Text)));
    }

    [Theory]
    [InlineData("project")]
    [InlineData("root")]
    [InlineData("checkpoint")]
    [InlineData("missing")]
    [InlineData("result")]
    [InlineData("pending")]
    [InlineData("child")]
    [InlineData("not-running")]
    [InlineData("failed")]
    public void NativePreparationRejectsForeignMissingOrCompletedCheckpointIntent(string fault)
    {
        var (root, checkpoint, intent, registration, source, message) = CreateNativePreparation();
        switch (fault)
        {
            case "project":
                root = new("another-project", root.RunId, root.SessionId);
                break;
            case "root":
                root = new(root.ProjectId, root.RunId, registration.Binding.SessionId);
                break;
            case "checkpoint":
                checkpoint = checkpoint with { Info = new("another-root", checkpoint.Info.CheckpointId) };
                break;
            case "missing":
                checkpoint = checkpoint with
                {
                    State = checkpoint.State with { PendingDispatches = checkpoint.State.PendingDispatches.Clear() }
                };
                break;
            case "result":
                checkpoint = checkpoint with
                {
                    State = checkpoint.State with { Results = checkpoint.State.Results.Add(intent.AssociationId, "answer") }
                };
                break;
            case "pending":
                checkpoint = checkpoint with
                {
                    State = checkpoint.State with
                    {
                        PendingDispatches = checkpoint.State.PendingDispatches.SetItem(
                            intent.AssociationId, intent with { MessageId = Guid.NewGuid() })
                    }
                };
                break;
            case "child":
                intent = intent with { ChildSessionId = "another-child" };
                break;
            case "not-running":
            case "failed":
                checkpoint = checkpoint with
                {
                    State = checkpoint.State with
                    {
                        Progress = checkpoint.State.Progress with
                        {
                            WorkItems = checkpoint.State.Progress.WorkItems.SetItem(intent.AssociationId,
                                fault == "failed" ? MafExecutionTaskStatus.Failed : MafExecutionTaskStatus.Pending)
                        }
                    }
                };
                break;
        }
        if (fault is "not-running" or "failed")
        {
            Assert.Throws<ArgumentException>(() =>
                RuntimeUsageSourceStore.ValidateNativePreparation(
                    root, checkpoint, intent, registration, source, message));
            return;
        }
        Assert.Equal("runtime_native_turn_preparation_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceStore.ValidateNativePreparation(
                    root, checkpoint, intent, registration, source, message)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void NativePreparationRetainsHardLimitForObservedFinancialAdmissionAtBegin(int hardLimit)
    {
        var (root, checkpoint, intent, registration, source, message) = CreateNativePreparation(hardLimit);
        RuntimeUsageSourceStore.ValidateNativePreparation(root, checkpoint, intent, registration, source, message);
        Assert.Equal(hardLimit, registration.Binding.CopilotHardCreditLimit);
    }

    [Theory]
    [InlineData(null, null, 100000, false, false)]
    [InlineData(null, 0, 0, false, true)]
    [InlineData(3, 5, 2999, false, false)]
    [InlineData(3, 5, 3000, true, false)]
    [InlineData(3, 5, 4999, true, false)]
    [InlineData(3, 5, 5000, true, true)]
    [InlineData(3, 5, 5001, true, true)]
    public void ObservedCreditLimitsNotifyAtSoftAndDenyNewWorkAtHard(
        int? soft, int? hard, int milliCredits, bool softReached, bool hardReached)
    {
        var (_, _, _, registration, _, _) = CreateNativePreparation();
        var binding = registration.Binding with { CopilotSoftCreditLimit = soft, CopilotHardCreditLimit = hard };
        Assert.Equal((softReached, hardReached),
            RuntimeUsageSourceStore.EvaluateObservedCreditLimits(binding, milliCredits / 1000m));
    }

    [Theory]
    [InlineData(-1, null, 0)]
    [InlineData(null, -1, 0)]
    [InlineData(5, 3, 0)]
    [InlineData(null, null, -1)]
    public void InvalidCreditLimitsOrNegativeObservedUsageCannotAuthorizeWork(int? soft, int? hard, int observed)
    {
        var (_, _, _, registration, _, _) = CreateNativePreparation();
        var binding = registration.Binding with { CopilotSoftCreditLimit = soft, CopilotHardCreditLimit = hard };
        Assert.Throws<RuntimeAuthorizationException>(() =>
            RuntimeUsageSourceStore.EvaluateObservedCreditLimits(binding, observed));
    }

    [Fact]
    public void NativePreparationRejectsMissingPromptPartsBeforeIndexingThem()
    {
        var (root, checkpoint, intent, registration, source, message) = CreateNativePreparation();
        message = message with { Message = message.Message with { Parts = [] } };
        Assert.Equal("runtime_native_turn_admission_invalid",
            Assert.Throws<RuntimeAuthorizationException>(() =>
                RuntimeUsageSourceStore.ValidateNativePreparation(
                    root, checkpoint, intent, registration, source, message)).Code);
    }

    [Fact]
    public void OpenWorkCheckpointBecomesRunningOnce()
    {
        var started = CoordinationEndpoints.PrepareRunningCheckpoint(
            null, "plan-1", 2, "work-item-1", fixedAssociation: null);

        Assert.NotNull(started);
        Assert.Equal(1, started.Revision);
        Assert.Equal(
            MafExecutionTaskStatus.Running,
            started.Progress.WorkItems["work-item-1"]);

        var snapshot = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo("root", "checkpoint-1"), started);
        Assert.Null(CoordinationEndpoints.PrepareRunningCheckpoint(
            snapshot, "plan-1", 2, "work-item-1", fixedAssociation: null));
    }

    [Fact]
    public void FixedWorkCheckpointStoresAssociationAndRejectsChangedReplay()
    {
        var association = CreateFixedAssociation();
        var started = CoordinationEndpoints.PrepareRunningCheckpoint(
            null, "plan-1", 3, association.AssociationId, association);

        Assert.NotNull(started);
        Assert.Equal(association, started.FixedWorkAssociations[association.AssociationId]);
        Assert.Equal(
            MafExecutionTaskStatus.Running,
            started.Progress.FixedWorkItems[association.AssociationId]);

        var snapshot = new MafExecutionCheckpointSnapshot(
            new CheckpointInfo("root", "checkpoint-1"), started);
        Assert.Null(CoordinationEndpoints.PrepareRunningCheckpoint(
            snapshot, "plan-1", 3, association.AssociationId, association));

        var changed = association with
        {
            Specification = association.Specification with { Task = "changed task" }
        };
        var conflict = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.PrepareRunningCheckpoint(
                snapshot, "plan-1", 3, association.AssociationId, changed));
        Assert.Equal("maf_execution_fixed_association_conflict", conflict.Code);
    }

    [Fact]
    public void CompletedDispatchReplayRequiresExactIntentAndResult()
    {
        var intent = new MafExecutionDispatchIntent(
            "work-item-1", "child-1", Guid.NewGuid(), new string('a', 64));
        var pending = new MafExecutionCheckpoint(
            1,
            "plan-1",
            1,
            MafExecutionProgress.Empty with
            {
                WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                    intent.AssociationId, MafExecutionTaskStatus.Running)
            })
        {
            PendingDispatches = ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(intent.AssociationId, intent)
        };

        Assert.False(CoordinationEndpoints.IsCompletedDispatchReplay(pending, intent, intent, "answer"));

        var completed = pending with
        {
            Revision = 2,
            PendingDispatches = pending.PendingDispatches.Remove(intent.AssociationId),
            Results = pending.Results.Add(intent.AssociationId, "answer")
        };
        Assert.True(CoordinationEndpoints.IsCompletedDispatchReplay(completed, intent, intent, "answer"));

        var changedAnswer = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.IsCompletedDispatchReplay(completed, intent, intent, "changed"));
        Assert.Equal("maf_execution_dispatch_intent_conflict", changedAnswer.Code);

        var changedIntent = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.IsCompletedDispatchReplay(
                pending, intent with { PromptHash = new string('b', 64) }, intent, "answer"));
        Assert.Equal("maf_execution_dispatch_intent_conflict", changedIntent.Code);

        var changedCompletedIntent = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.IsCompletedDispatchReplay(
                completed, intent with { MessageId = Guid.NewGuid() }, intent, "answer"));
        Assert.Equal("maf_execution_dispatch_intent_conflict", changedCompletedIntent.Code);
    }

    [Fact]
    public void DispatchSendRequiresCurrentPendingIntentAndSkipsCompletedReplay()
    {
        var intent = new MafExecutionDispatchIntent(
            "work-item-1", "child-1", Guid.NewGuid(), new string('a', 64));
        var running = new MafExecutionCheckpoint(
            1,
            "plan-1",
            1,
            MafExecutionProgress.Empty with
            {
                WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                    intent.AssociationId, MafExecutionTaskStatus.Running)
            })
        {
            PendingDispatches = ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(intent.AssociationId, intent)
        };

        Assert.True(CoordinationEndpoints.ShouldSendDispatchIntent(running, intent, intent));

        var completed = running with
        {
            Revision = 2,
            Progress = running.Progress with
            {
                WorkItems = running.Progress.WorkItems.SetItem(
                    intent.AssociationId, MafExecutionTaskStatus.Succeeded)
            },
            PendingDispatches = running.PendingDispatches.Remove(intent.AssociationId),
            Results = running.Results.Add(intent.AssociationId, "answer")
        };
        Assert.False(CoordinationEndpoints.ShouldSendDispatchIntent(completed, intent, intent));

        var missing = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ShouldSendDispatchIntent(
                running with { PendingDispatches = running.PendingDispatches.Clear() },
                intent,
                intent));
        Assert.Equal("maf_execution_dispatch_intent_conflict", missing.Code);

        var changedIntent = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ShouldSendDispatchIntent(
                running, intent, intent with { PromptHash = new string('b', 64) }));
        Assert.Equal("maf_execution_dispatch_intent_conflict", changedIntent.Code);

        var conflicting = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ShouldSendDispatchIntent(
                running with { Results = running.Results.Add(intent.AssociationId, "answer") },
                intent,
                intent));
        Assert.Equal("maf_execution_dispatch_intent_conflict", conflicting.Code);
    }

    [Fact]
    public async Task RuntimeHostReadinessUsesActorHeadersAndOnlyTreatsExplicitNotReadyAsUnavailable()
    {
        var token = new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, "tenant-1");
        var endpoint = new Uri("https://runtime.example/runtime/v1/readiness");
        var calls = 0;
        using var client = new HttpClient(new TestHandler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(endpoint, request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal(["tenant-1"], request.Headers.GetValues("X-Agentweaver-Tenant"));
            Assert.True(request.Headers.CacheControl?.NoStore);
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new { code = "runtime_host_not_ready" })
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));

        try
        {
            var unavailable = await CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                client, endpoint, actor, null, allowNotReady: true, CancellationToken.None);

            Assert.Null(unavailable);
            Assert.Equal(1, calls);
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public async Task RuntimeHostTransportRejectsRedirectsAndUnrecognizedForbiddenResponses()
    {
        var endpoint = new Uri("https://runtime.example/runtime/v1/readiness");
        var token = new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, null);
        using var redirectClient = new HttpClient(new TestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = new StringContent(string.Empty)
            };
            response.Headers.Location = new Uri("https://other.example/runtime/v1/readiness");
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));
        using var deniedClient = new HttpClient(new TestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = JsonContent.Create(new { code = "runtime_owner_denied" })
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));
        using var prematureClient = new HttpClient(new TestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new RuntimeA2AResponse(
                    "message", Guid.NewGuid(), "sdk-session", "agent", [new("text", "completed")]))
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));

        try
        {
            var redirect = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    redirectClient, endpoint, actor, null, allowNotReady: true, CancellationToken.None));
            Assert.Equal("maf_execution_host_redirect_rejected", redirect.Code);

            var denied = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    deniedClient, endpoint, actor, null, allowNotReady: true, CancellationToken.None));
            Assert.Equal("maf_execution_host_request_rejected", denied.Code);

            var premature = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeA2AResponse>(
                    prematureClient,
                    new Uri("https://runtime.example/runtime/v1/a2a/message:send"),
                    actor,
                    new { prompt = "run the task" },
                    allowNotReady: false,
                    CancellationToken.None));
            Assert.Equal("maf_execution_host_response_invalid", premature.Code);

            var invalidEndpoint = Assert.Throws<CoordinationException>(() =>
                CoordinationEndpoints.RuntimeHostEndpoint(
                    new Uri("http://runtime.example/runtime/v1/configure"), "/runtime/v1/readiness"));
            Assert.Equal("maf_execution_child_registration_stale", invalidEndpoint.Code);
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public async Task RuntimeHostTransportMapsUnknownSendAndTimeoutWithoutDroppingIntent()
    {
        var endpoint = new Uri("https://runtime.example/runtime/v1/a2a/message:send");
        var token = new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, null);
        using var failedClient = new HttpClient(new TestHandler((_, _) =>
            throw new HttpRequestException("connection reset")));
        using var timeoutClient = new HttpClient(new TestHandler((_, _) =>
            throw new TaskCanceledException("request timed out")));
        using var readinessClient = new HttpClient(new TestHandler((_, _) =>
            throw new HttpRequestException("connection refused")));

        try
        {
            var failed = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeA2AResponse>(
                    failedClient, endpoint, actor, new { prompt = "run the task" },
                    allowNotReady: false, CancellationToken.None));
            Assert.Equal("maf_execution_host_outcome_unknown", failed.Code);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (HttpStatusCode)failed.StatusCode);
            Assert.IsType<HttpRequestException>(failed.InnerException);

            var timedOut = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeA2AResponse>(
                    timeoutClient, endpoint, actor, new { prompt = "run the task" },
                    allowNotReady: false, CancellationToken.None));
            Assert.Equal("maf_execution_host_outcome_unknown", timedOut.Code);
            Assert.IsType<TaskCanceledException>(timedOut.InnerException);

            var unavailable = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    readinessClient, new Uri("https://runtime.example/runtime/v1/readiness"),
                    actor, null, allowNotReady: true, CancellationToken.None));
            Assert.Equal("maf_execution_host_unavailable", unavailable.Code);

            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeA2AResponse>(
                    timeoutClient, endpoint, actor, new { prompt = "run the task" },
                    allowNotReady: false, canceled.Token));
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public void RuntimeHostReadinessMustMatchCurrentRegistrationAndObserveGrant()
    {
        var now = DateTimeOffset.UtcNow;
        var registration = CreateRuntimeRegistration(now);
        var receipt = CreateReadiness(registration, now);

        CoordinationEndpoints.ValidateHostReadiness(receipt, registration, TimeProvider.System);

        var staleGrant = receipt with
        {
            SourceGrant = receipt.SourceGrant with
            {
                RegistrationRevision = registration.Revision - 1
            }
        };
        var conflict = Assert.Throws<CoordinationException>(() =>
            CoordinationEndpoints.ValidateHostReadiness(staleGrant, registration, TimeProvider.System));
        Assert.Equal("maf_execution_host_readiness_invalid", conflict.Code);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SandboxStartupPhaseObservation(
                (SandboxStartupPhase)int.MaxValue, 1, now));
    }

    [Fact]
    public void ConfiguredHostedCreditLimitsRequireRealPricingRatherThanABlanketSendHold()
    {
        var registration = CreateRuntimeRegistration(DateTimeOffset.UtcNow);
        registration = registration with
        {
            Binding = registration.Binding with { ModelSourceMode = ModelSourceMode.HostedCopilot }
        };
        Assert.False(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(registration.Binding));

        foreach (var creditLimit in new decimal?[] { 0m, 1m })
        {
            var capped = registration with
            {
                Binding = registration.Binding with { CopilotHardCreditLimit = creditLimit }
            };
            Assert.True(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(capped.Binding));
            Assert.True(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(registration.Binding with
            {
                CopilotSoftCreditLimit = creditLimit
            }));
            Assert.False(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(capped.Binding with
            {
                ModelSourceMode = ModelSourceMode.Byok
            }));
        }
    }

    [Fact]
    public async Task ReadyHostSendUsesTheNativeOwnerAdmissionPathWithoutAStaticPhaseBHold()
    {
        var now = DateTimeOffset.UtcNow;
        var registration = CreateRuntimeRegistration(now);
        registration = registration with
        {
            Binding = registration.Binding with { ModelSourceMode = ModelSourceMode.HostedCopilot }
        };
        Assert.Null(registration.Binding.CopilotHardCreditLimit);
        Assert.False(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(registration.Binding));

        var intent = new MafExecutionDispatchIntent(
            "work-item-1", "child-1", Guid.NewGuid(), new string('a', 64));
        var pending = new MafExecutionCheckpoint(
            1,
            "plan-1",
            1,
            MafExecutionProgress.Empty with
            {
                WorkItems = MafExecutionProgress.Empty.WorkItems.Add(
                    intent.AssociationId, MafExecutionTaskStatus.Running)
            })
        {
            PendingDispatches = ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(intent.AssociationId, intent)
        };

        Assert.True(CoordinationEndpoints.ShouldSendDispatchIntent(pending, intent, intent));
        Assert.Contains(intent.AssociationId, pending.PendingDispatches.Keys);

        var token = new SecretCredential("actor-token", now.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, "tenant-1");
        var sends = 0;
        using var client = new HttpClient(new TestHandler((request, _) =>
        {
            sends++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
                Content = JsonContent.Create(new RuntimeA2AResponse(
                    "message", Guid.NewGuid(), "sdk-session", "agent", [new("text", "completed")]))
            });
        }));

        async Task AssertHostSendAsync(RuntimeRegistration candidate)
        {
            var readiness = CreateReadiness(candidate, now);
            var contextId = RuntimeContractValidation.NativeSessionId(candidate.Binding);
            var message = new RuntimeA2ASendRequest(new RuntimeA2AMessage(
                "message",
                intent.MessageId,
                contextId,
                "user",
                [new("text", "run the task")],
                new(new(
                    1,
                    candidate,
                    readiness.SourceGrant.GrantId,
                    readiness.SourceGrant.Revision,
                    RuntimeCredentialPurpose.Observe),
                    AddressedMessageDeliveryMode.Enqueue)));
            var response = await CoordinationEndpoints.SendRuntimeHostMessageAsync(
                    client,
                    candidate,
                    readiness,
                    actor,
                    message,
                    TimeProvider.System,
                    CancellationToken.None);
            Assert.Equal("completed", Assert.Single(response.Parts).Text);
        }

        try
        {
            await AssertHostSendAsync(registration);
            foreach (var creditLimit in new decimal?[] { 0m, 1m })
            {
                var capped = registration with
                {
                    Binding = registration.Binding with { CopilotHardCreditLimit = creditLimit }
                };
                Assert.True(RuntimeUsageSourceStore.RequiresCopilotCreditSnapshot(capped.Binding));
                await AssertHostSendAsync(capped);
            }
            Assert.Equal(3, sends);
            Assert.Contains(intent.AssociationId, pending.PendingDispatches.Keys);
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public async Task RuntimeHostReadinessRejectsUndefinedStartupPhaseValues()
    {
        var now = DateTimeOffset.UtcNow;
        var registration = CreateRuntimeRegistration(now);
        var receipt = CreateReadiness(registration, now);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var invalidJson = JsonSerializer.Serialize(receipt, jsonOptions)
            .Replace("\"scheduled\"", "999", StringComparison.Ordinal);
        using var client = new HttpClient(new TestHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    invalidJson, System.Text.Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));
        var token = new SecretCredential("actor-token", now.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, null);

        try
        {
            var invalid = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostAsync<RuntimeHostReadinessReceipt>(
                    client, new Uri("https://runtime.example/runtime/v1/readiness"),
                    actor, null, allowNotReady: true, CancellationToken.None));

            Assert.Equal("maf_execution_host_response_invalid", invalid.Code);
            Assert.Contains("\"phase\":999", invalidJson, StringComparison.Ordinal);
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public async Task RuntimeHostMessageDoesNotSendWhenReadinessGrantExpiresDuringDispatchWaits()
    {
        var now = DateTimeOffset.UtcNow;
        var timeProvider = new ManualTimeProvider(now);
        var registration = CreateRuntimeRegistration(now);
        var readiness = CreateReadiness(registration, now);
        var token = new SecretCredential("actor-token", now.AddHours(2), timeProvider);
        var actor = new RuntimeActorAuthorization(token, "tenant-1");
        var contextId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var message = new RuntimeA2ASendRequest(new RuntimeA2AMessage(
            "message",
            Guid.NewGuid(),
            contextId,
            "user",
            [new("text", "run the task")],
            new(new(
                1,
                registration,
                readiness.SourceGrant.GrantId,
                readiness.SourceGrant.Revision,
                RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Enqueue)));
        var sends = 0;
        using var client = new HttpClient(new TestHandler((_, _) =>
        {
            sends++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RuntimeA2AResponse(
                    "message", Guid.NewGuid(), contextId, "agent", [new("text", "completed")]))
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return Task.FromResult(response);
        }));

        try
        {
            timeProvider.Advance(readiness.SourceGrant.ExpiresAt - now + TimeSpan.FromSeconds(1));
            var expired = await Assert.ThrowsAsync<CoordinationException>(() =>
                CoordinationEndpoints.SendRuntimeHostMessageAsync(
                    client, registration, readiness, actor, message, timeProvider, CancellationToken.None));

            Assert.Equal("maf_execution_host_readiness_invalid", expired.Code);
            Assert.Equal(0, sends);
        }
        finally
        {
            token.Invalidate();
        }
    }

    [Fact]
    public async Task RuntimeHostTransportPostsMessageAndReadsAgentReply()
    {
        var registration = CreateRuntimeRegistration(DateTimeOffset.UtcNow);
        var grant = CreateReadiness(registration, DateTimeOffset.UtcNow).SourceGrant;
        var messageId = Guid.NewGuid();
        var contextId = RuntimeContractValidation.NativeSessionId(registration.Binding);
        var requestBody = new RuntimeA2ASendRequest(new RuntimeA2AMessage(
            "message",
            messageId,
            contextId,
            "user",
            [new("text", "run the task")],
            new(new(
                1,
                registration,
                grant.GrantId,
                grant.Revision,
                RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Enqueue)));
        var responseBody = new RuntimeA2AResponse(
            "message", Guid.NewGuid(), contextId, "agent", [new("text", "completed")]);
        var token = new SecretCredential("actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(token, "tenant-1");
        var endpoint = new Uri("https://runtime.example/runtime/v1/a2a/message:send");
        using var client = new HttpClient(new TestHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(endpoint, request.RequestUri);
            Assert.Equal("actor-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("tenant-1", Assert.Single(request.Headers.GetValues("X-Agentweaver-Tenant")));
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Contains(messageId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"deliveryMode\":\"enqueue\"", json, StringComparison.Ordinal);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(responseBody)
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        }));
        try
        {
            var result = await CoordinationEndpoints.SendRuntimeHostAsync<RuntimeA2AResponse>(
                client, endpoint, actor, requestBody, allowNotReady: false, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(responseBody.MessageId, result.MessageId);
            Assert.Equal(responseBody.ContextId, result.ContextId);
            Assert.Equal(responseBody.Parts.Single().Text, result.Parts.Single().Text);
        }
        finally
        {
            token.Invalidate();
        }
    }

    private static MafExecutionFixedWorkAssociation CreateFixedAssociation() =>
        new(
            "fixed-association",
            1,
            "plan-1",
            "workflow-1",
            "revision-1",
            "catalog-1",
            "fixed-step",
            new FixedWorkSpecification(
                "Fixed task", "Run the fixed task.", "developer", "implementation",
                "isolated-worktree", ImmutableArray.Create("src/fixed.cs")),
            "agent-1",
            "model:provider/alpha",
            "sandbox-provider",
            new string('A', 64),
            1,
            3,
            "maf-child");

    private static (SessionIdentity Root, MafExecutionCheckpointSnapshot Checkpoint,
        MafExecutionDispatchIntent Intent, RuntimeRegistration Registration, SdkSessionFacts Source,
        RuntimeA2ASendRequest Message) CreateNativePreparation(decimal? hardLimit = null)
    {
        var registration = CreateRuntimeRegistration(DateTimeOffset.UtcNow);
        registration = registration with
        {
            Binding = registration.Binding with
            {
                WorkflowStepId = "implement", ModelSelectionReference = "model:selected",
                ModelSourceMode = ModelSourceMode.HostedCopilot, CopilotHardCreditLimit = hardLimit,
                EnvironmentCurrentFencingGeneration = 1, EnvironmentProviderFencingGeneration = 1
            }
        };
        var binding = registration.Binding;
        var root = new SessionIdentity(binding.ProjectId, binding.RunId, "root");
        var prompt = "Run the task.";
        var intent = new MafExecutionDispatchIntent("work-item-1", binding.SessionId, Guid.NewGuid(),
            RuntimeContractValidation.Hash(System.Text.Encoding.UTF8.GetBytes(prompt)));
        var checkpoint = new MafExecutionCheckpointSnapshot(new(root.SessionId, "checkpoint-1"),
            new(1, "plan-1", 2, MafExecutionProgress.Empty with
            {
                WorkItems = MafExecutionProgress.Empty.WorkItems.Add(intent.AssociationId, MafExecutionTaskStatus.Running)
            })
            {
                PendingDispatches = ImmutableDictionary<string, MafExecutionDispatchIntent>.Empty
                    .Add(intent.AssociationId, intent)
            });
        var source = new SdkSessionFacts(registration.RuntimeInstanceId, RuntimeContractValidation.NativeSessionId(binding),
            "1.0.18", "1.0.79", binding.ModelSelectionReference!, "controlled-model", new string('c', 64), 1,
            "hosted-copilot", SdkMeterSources.CopilotNanoAiu, binding.AcceptedSelectionHash, registration.Revision);
        var message = new RuntimeA2ASendRequest(new("message", intent.MessageId, source.SdkSessionId, "user",
            [new("text", prompt)], new(new(1, registration, Guid.NewGuid(), 1, RuntimeCredentialPurpose.Observe),
                AddressedMessageDeliveryMode.Enqueue)));
        return (root, checkpoint, intent, registration, source, message);
    }

    private static RuntimeRegistration CreateRuntimeRegistration(DateTimeOffset now)
    {
        var image = new SandboxImageIdentity("sha256:" + new string('a', 64), "linux/amd64", 1024);
        var configureEndpoint = new Uri("https://runtime.example/runtime/v1/configure");
        return new(
            Guid.NewGuid(),
            4,
            new RuntimeBinding(
                "https://identity.example/",
                Guid.NewGuid().ToString("D"),
                "tenant-1",
                "project-1",
                "run-1",
                "session-1",
                "agent-1",
                "turn-1",
                1,
                1,
                1,
                "context-v1",
                new string('a', 64),
                3,
                "environment-1",
                "placement-1",
                1,
                "profile-1",
                configureEndpoint,
                new Uri("https://runtime.example/observation"))
            {
                Image = image,
                PlacementProviderId = "sandbox-provider",
                EnvironmentLifecycleGeneration = 1,
                EnvironmentLeaseRevision = 1
            },
            RuntimeRegistrationState.Active,
            now.AddHours(1));
    }

    private static RuntimeHostReadinessReceipt CreateReadiness(
        RuntimeRegistration registration, DateTimeOffset now)
    {
        var image = registration.Binding.Image!;
        var phases = Enum.GetValues<SandboxStartupPhase>().Select((phase, index) =>
            new SandboxStartupPhaseObservation(
                phase,
                1,
                now.AddSeconds(index),
                phase == SandboxStartupPhase.ImageReady ? image.Digest : null,
                phase == SandboxStartupPhase.ImageReady ? image.CompressedPullBytes : null))
            .ToImmutableArray();
        return new(
            1,
            registration.RuntimeInstanceId,
            registration.Revision,
            registration.Binding.ExecutionFence,
            image,
            phases,
            new RuntimeGrantReceipt(
                Guid.NewGuid(),
                registration.RuntimeInstanceId,
                registration.Revision,
                1,
                "https://identity.example/",
                RuntimeCredentialPurpose.Observe,
                registration.Binding.ConfigureEndpoint,
                RuntimeCredentialState.Active,
                new string('b', 64),
                now.AddMinutes(30),
                now));
    }

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken).ConfigureAwait(false);
            response.RequestMessage ??= request;
            return response;
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
