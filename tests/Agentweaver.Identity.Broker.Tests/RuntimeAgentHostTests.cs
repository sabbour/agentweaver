using System.Collections.Concurrent;
using System.Collections.Immutable;
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
        var proof = new RuntimeHostSessionProof(1, session.Registration, session.SourceGrant.GrantId,
            session.SourceGrant.Revision, RuntimeCredentialPurpose.Observe);
        await session.SendNativeTurnAsync(fixture.Message(proof), material, source, timeout.Token);
        await session.CommitNativeCacheAsync(material, Guid.NewGuid(), timeout.Token);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);

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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), phaseVersion);

        Assert.Equal("runtime_suspend_request_invalid",
            (await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
                fixture.Host.SuspendAsync(request, fixture.Actor, timeout.Token))).Code);
        Assert.Empty(fixture.SuspendChecks);
        Assert.Empty(fixture.Material);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
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
        var request = new RuntimeHostSuspendRequest(proof, Guid.NewGuid(), Guid.NewGuid(), 1);
        const string path = "/runtime/v1/suspend/native-evidence";
        using var anonymous = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token);
        client.DefaultRequestHeaders.Add("X-Agentweaver-Tenant", "tenant");
        using var wrongAudience = await client.PostAsJsonAsync(
            "http://runtime.test" + path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAudience.StatusCode);
        Assert.Single(fixture.Material, material => material.Kind == SessionMaterialKind.SdkCache);
        using var response = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var receipt = await response.Content.ReadFromJsonAsync<RuntimeHostSuspendReceipt>(
            HostFixture.Json, timeout.Token);
        Assert.Equal(request.ManifestId, receipt!.CacheAcknowledgment.EventId);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token + "invalid");
        using var forged = await client.PostAsJsonAsync(path, request, HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(2, fixture.Material.Count(material => material.Kind == SessionMaterialKind.SdkCache));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaterialAcknowledgmentMustRetainTheActualNarrowedPromptCapacity(bool changed)
    {
        await using var fixture = new HostFixture(maxPromptTokens: 2048)
        {
            MaterialPromptCapacityChanged = changed
        };
        fixture.Sdk.MaxPromptTokens = 1024;
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
        Assert.All(fixture.Material, request => Assert.Equal(1024, request.MaxPromptTokens));
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
            Assert.Empty(fixture.Material);
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
        Assert.Empty(fixture.Material);
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
        using var turn = await client.PostAsJsonAsync("/runtime/v1/a2a/message:send",
            fixture.Message(fixture.Proof(ready!)), HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, turn.StatusCode);
        var answer = await turn.Content.ReadFromJsonAsync<RuntimeA2AResponse>(HostFixture.Json, timeout.Token);
        Assert.Equal("agent", answer!.Role);
        Assert.Equal(fixture.Sdk.AssistantResponse, Assert.Single(answer.Parts).Text);
        client.DefaultRequestHeaders.Authorization = new("Bearer", fixture.Token + "invalid");
        using var forged = await client.PostAsJsonAsync("/runtime/v1/refresh",
            new RuntimeHostRefreshRequest(fixture.Proof(ready!), Guid.NewGuid()), HostFixture.Json, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(0, fixture.Rotations);
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(new string('s', 64)));
        private readonly HttpClient _http;
        private readonly Guid _bootstrap = Guid.NewGuid();
        private readonly Guid _source = Guid.NewGuid();
        private readonly string _hash = RuntimeContractValidation.Hash("{}"u8);
        private RuntimeModelCredentialGrantReceipt? _model;
        private SdkSessionFacts? _facts;
        private long _sourceRevision = 1;
        private int _position;
        private readonly ConcurrentDictionary<Guid, SessionMaterialReadResult> _material = new();
        public HostFixture(bool workflowBound = false, int? maxPromptTokens = null)
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
                    ConfigureEndpoint = new("https://runtime.test/runtime/v1/configure"),
                    PlacementProviderId = "sandbox-test", EnvironmentLifecycleGeneration = 1,
                    EnvironmentLeaseRevision = 1, Image = Image, ModelConnectionId = Guid.NewGuid(),
                    ModelConnectionScope = ProjectAuthorityResourceType.Project,
                    WorkflowStepId = workflowBound ? "implement" : null, MaxPromptTokens = maxPromptTokens
                }
            };
            Sdk = new()
            {
                EmitUsageAfterCreate = false, ExpectedAvailableToolsCount = 7, PersistNativeSessionState = true,
                EmitNativeCompletionReceipt = workflowBound
            };
            var factory = RuntimeCopilotSessionTests.Factory(Sdk);
            var b = Registration.Binding;
            var owner = new RuntimeOwnerContext(1, b.ActorIssuer, b.ActorId, b.TenantId, b.ProjectId, b.RunId,
                b.SessionId, b.AgentId, b.ModelSelectionReference!, b.TurnId, b.ProjectRevision,
                b.ProjectConfigurationRevision, b.PlatformRuntimeRevision, b.ContextRevision,
                b.AcceptedSelectionHash, b.ExecutionFence, 1, 1, 1)
            {
                ModelSourceMode = b.ModelSourceMode, ModelConnectionId = b.ModelConnectionId,
                ModelConnectionScope = b.ModelConnectionScope, WorkflowStepId = b.WorkflowStepId,
                MaxPromptTokens = b.MaxPromptTokens
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
                _http, new RuntimeRegistrationHttpClient(_http, new("https://orchestrator.test/")), factory, Time);
            Configure = new(1, Registration, Guid.NewGuid(), Guid.NewGuid(), JsonSerializer.SerializeToElement(new { }));
        }
        public TestClock Time { get; }
        public string Token { get; }
        public RuntimeActorAuthorization Actor { get; }
        public SandboxImageIdentity Image { get; }
        public RuntimeRegistration Registration { get; set; }
        public EnvironmentRuntimeReadinessContext Readiness { get; set; }
        public RuntimeAgentHost Host { get; }
        public RuntimeHostConfigureRequest Configure { get; }
        public ControlledCopilotRuntime Sdk { get; }
        public ConcurrentQueue<SessionMaterialWriteRequest> Material { get; } = new();
        public ConcurrentQueue<RuntimeUsageSourceReceipt> Usage { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnBeginRequest> NativeAdmissions { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnObservationRequest> NativeObservations { get; } = new();
        public ConcurrentQueue<RuntimeNativeTurnAccountingRequest> NativeAccounting { get; } = new();
        public string? NativeAccountingFault { get; set; }
        public string? NativeAdmissionFault { get; set; }
        public string? NativeRecordFault { get; set; }
        public Func<RuntimeHostSuspendRequest, RuntimeHostSuspendRequest>? SuspendEcho { get; set; }
        public HttpStatusCode SuspendStatus { get; set; } = HttpStatusCode.OK;
        public ConcurrentQueue<RuntimeHostSuspendRequest> SuspendChecks { get; } = new();
        public TaskCompletionSource AccountingReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, Task>? BeforeAccounting { get; set; }
        public bool FailAccounting { get; set; }
        public bool MaterialPromptCapacityChanged { get; set; }
        public Action<SessionMaterialWriteRequest>? AfterMaterialWrite { get; set; }
        public Action<SessionMaterialReadResult>? AfterMaterialRead { get; set; }
        public Guid? MissingMaterialId { get; set; }
        public int Accounted { get; private set; }
        public int Rotations { get; private set; }

        public Task<RuntimeBootstrapDeliveryReceipt> DeliverAsync(CancellationToken token) =>
            Host.ReceiveAsync(new(Registration.RuntimeInstanceId, Guid.NewGuid(), _bootstrap, _hash,
                new string('c', 64), Registration.ExpiresAt), Actor, token);
        public async Task<RuntimeHostReadinessReceipt> ConfigureAsync(CancellationToken token)
        {
            await DeliverAsync(token);
            return await Host.ConfigureAsync(Configure, Actor, token);
        }
        public RuntimeHostSessionProof Proof(RuntimeHostReadinessReceipt ready) =>
            new(1, Configure.Registration, ready.SourceGrant.GrantId, ready.SourceGrant.Revision, RuntimeCredentialPurpose.Observe);
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
            RuntimeCredentialState state = RuntimeCredentialState.Active, long revision = 1) =>
            new(id, Configure.Registration.RuntimeInstanceId, Configure.Registration.Revision, revision,
                "https://broker.test/", purpose,
                purpose == RuntimeCredentialPurpose.Configure ? Configure.Registration.Binding.ConfigureEndpoint :
                    Configure.Registration.Binding.ObservationEndpoint, state, _hash,
                Configure.Registration.ExpiresAt, Time.GetUtcNow());

        private async Task<HttpResponseMessage> SendOwnerAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Authorization?.Parameter != Token ||
                request.Headers.GetValues("X-Agentweaver-Tenant").Single() != "tenant")
                return Response(request, new { }, HttpStatusCode.Forbidden);
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/internal/runtime/registrations/", StringComparison.Ordinal))
                return Response(request, Registration);
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
                return Response(request, Grant(_source, RuntimeCredentialPurpose.Observe, RuntimeCredentialState.Revoked, ++_sourceRevision));
            if (path == "/internal/runtime/model-session/grant")
            {
                _model = new("model-grant", 1, Configure.Registration.RuntimeInstanceId, Configure.Registration.Revision,
                    Configure.Registration.Binding.ModelSelectionReference!, new("copilot-user", "v1"),
                    RuntimeSecretPurposes.ModelSession, Configure.Registration.ExpiresAt)
                {
                    SourceMode = ModelSourceMode.HostedCopilot, ConnectionId = Configure.Registration.Binding.ModelConnectionId,
                    ConnectionRevision = 1, CredentialKind = RuntimeModelCredentialKind.GitHubUserAccess
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
                    _admission = new(1, action, action.EventId.ToString("N"), "1", action.EventId,
                        PolicyEvaluationOutcome.Allow, PolicyEvaluationReasonCode.Allowed);
                }
                return Response(request, _admission!);
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
                return Response(request, new SessionEventPage([], null, false));
            if (path.EndsWith("/material", StringComparison.Ordinal))
            {
                var material = await ReadAsync<SessionMaterialWriteRequest>(request, token);
                Material.Enqueue(material);
                var b = Configure.Registration.Binding;
                var receipt = new SessionMaterialAcknowledgment(1, new(b.ProjectId, b.RunId, b.SessionId),
                    material.EventId, Interlocked.Increment(ref _position),
                    new(new ObjectKey(material.EventId.ToString("N")), SessionMaterialValidation.Purpose(material.Kind),
                        material.Bytes.Length)
                    {
                        Material = new(1, material.Kind, b.TenantId, RuntimeContractValidation.Hash(material.Bytes),
                            Configure.Registration.RuntimeInstanceId, Configure.Registration.Revision, b.ExecutionFence,
                            b.AcceptedSelectionHash, _facts!.SdkVersion, _facts.RuntimeVersion,
                            _facts.ModelSelectionReference, _facts.ModelId)
                        {
                            MaxPromptTokens = MaterialPromptCapacityChanged
                                ? _facts.MaxPromptTokens + 1 : _facts.MaxPromptTokens
                        }
                    });
                _material[material.EventId] = new(receipt, material.Bytes);
                AfterMaterialWrite?.Invoke(material);
                return Response(request, receipt);
            }
            if (path.Contains("/material/", StringComparison.Ordinal))
            {
                var id = Guid.Parse(path.Split('/')[5]);
                if (id == MissingMaterialId || !_material.TryGetValue(id, out var recorded))
                    return Response(request, new { }, HttpStatusCode.NotFound);
                AfterMaterialRead?.Invoke(recorded);
                return Response(request, recorded);
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
