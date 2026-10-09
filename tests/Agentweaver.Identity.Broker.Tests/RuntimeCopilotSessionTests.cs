using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using System.Text.Json;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeCopilotSessionTests
{
    [Fact]
    public async Task ActualSdkTransportCallbackPreservesNativeMeasurementsAndEffectiveModelPins()
    {
        await using var external = new ControlledCopilotRuntime();
        var factory = Factory(external);
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await factory.CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        await using var usage = session.ReadUsageAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await usage.MoveNextAsync());
        var observation = usage.Current;
        Assert.Equal(external.UsageEventId.ToString("D"), observation.SdkEventId);
        Assert.Equal(external.UsageTimestamp, observation.OccurredAt);
        Assert.Equal(session.Facts.SdkSessionId, observation.SdkSessionId);
        Assert.Equal("controlled-model", session.Facts.ModelId);
        Assert.Equal(17, observation.InputTokens);
        Assert.Equal(11, observation.OutputTokens);
        Assert.Equal(7, observation.CacheReadTokens);
        Assert.Equal(5, observation.CacheWriteTokens);
        Assert.Equal(3, observation.ReasoningTokens);
        Assert.Equal(1234567.25m, observation.TotalNanoAiu);
        Assert.Equal(12.5m, observation.DurationMilliseconds);
        Assert.Equal(2.5m, session.Facts.ModelMultiplier);
        Assert.Equal(SdkMeterSources.CopilotNanoAiu, session.Facts.MeterSource);
        Assert.Equal(registration.Binding.AcceptedSelectionHash, session.Facts.AcceptedSelectionHash);
        Assert.Equal(registration.Revision, session.Facts.RegistrationRevision);
        Assert.Contains(external.Requests, request => request.Method == "session.model.getCurrent");
    }

    [Fact]
    public async Task SendTurnUsesNativeSdkAndReturnsOnlyAfterTheAssistantIdleEvent()
    {
        await using var external = new ControlledCopilotRuntime
        {
            AssistantResponse = "A durable answer."
        };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            SdkCredential(), _ => Task.CompletedTask, timeout.Token);

        var response = await session.SendTurnAsync("A bounded user request.", timeout.Token);

        Assert.Equal(external.AssistantResponse, response);
        var send = Assert.Single(external.Requests, request => request.Method == "session.send");
        Assert.Equal("A bounded user request.", send.Parameters.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task CancellationDrainsActualNativeAbortBeforeAdmittingTheQueuedTurn()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldTurn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = 0;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var firstCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var external = new ControlledCopilotRuntime
        {
            BeforeTurnResponse = token => Interlocked.Increment(ref turns) == 1
                ? heldTurn.Task.WaitAsync(token) : Task.CompletedTask,
            BeforeAbortIdle = token => idle.Task.WaitAsync(token),
            LateAbortAssistantContent = "An abandoned turn must not reach the next turn."
        };
        var registration = Registration();
        await using var session = await Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        var first = session.SendTurnAsync("A bounded user request.", firstCancellation.Token);
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        var queued = session.SendTurnAsync("A bounded user request.", timeout.Token);
        await firstCancellation.CancelAsync();
        await external.AbortAcknowledged.Task.WaitAsync(timeout.Token);
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);
        Assert.Single(external.Requests, request => request.Method == "session.send");

        idle.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(external.AssistantResponse, await queued);
        Assert.Equal(new[] { "session.send", "session.abort", "session.send" },
            external.Requests.Where(request => request.Method is "session.send" or "session.abort")
                .Select(request => request.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAbortOrMissingIdlePoisonsTheSessionRatherThanReleasingAnotherTurn(bool missingIdle)
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var external = new ControlledCopilotRuntime
        {
            BeforeTurnResponse = token => held.Task.WaitAsync(token),
            AbortSucceeds = missingIdle,
            BeforeAbortIdle = token => held.Task.WaitAsync(token)
        };
        var registration = Registration();
        await using var session = await Factory(external, abortDrainTimeout: TimeSpan.FromMilliseconds(100))
            .CreateAsync(registration, registration.Binding.ModelSelectionReference!,
                SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        var turn = session.SendTurnAsync("A bounded user request.", turnCancellation.Token);
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        await turnCancellation.CancelAsync();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => turn);
        Assert.Contains("runtime_native_turn_indeterminate", failure.Message);
        var rejected = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            session.SendTurnAsync("A bounded user request.", timeout.Token));
        Assert.Equal("runtime_native_turn_indeterminate", rejected.Code);
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => session.CaptureNativeCacheAsync(timeout.Token));
        Assert.Single(external.Requests, request => request.Method == "session.send");
    }

    [Fact]
    public async Task DisposalCancelsAndDrainsTheActiveNativeTurnBeforeCompleting()
    {
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var external = new ControlledCopilotRuntime
        {
            BeforeTurnResponse = token => held.Task.WaitAsync(token),
            BeforeAbortIdle = token => idle.Task.WaitAsync(token),
            EmitUsageAfterCreate = false
        };
        var registration = Registration();
        await using var session = await Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!,
            SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        var turn = session.SendTurnAsync("A bounded user request.", timeout.Token);
        await external.TurnReceived.Task.WaitAsync(timeout.Token);
        var disposal = session.DisposeAsync().AsTask();
        var repeatedDisposal = session.DisposeAsync().AsTask();
        await external.AbortAcknowledged.Task.WaitAsync(timeout.Token);
        Assert.False(disposal.IsCompleted);
        Assert.False(repeatedDisposal.IsCompleted);
        idle.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
        await Task.WhenAll(disposal, repeatedDisposal).WaitAsync(timeout.Token);
        Assert.True(session.UsageCompletion.IsCompletedSuccessfully);
        Assert.Single(external.Requests, request => request.Method == "session.detach");
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.SendTurnAsync("A bounded user request.", timeout.Token));
    }

    [Fact]
    public async Task EffectiveModelMismatchRejectsAndDisposesTheActualNativeSession()
    {
        await using var external = new ControlledCopilotRuntime { EffectiveModelId = "other-model" };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration,
                registration.Binding.ModelSelectionReference!, SdkCredential(), _ => Task.CompletedTask, timeout.Token));
        Assert.Equal("runtime_sdk_effective_model_mismatch", error.Code);
        Assert.Contains(external.Requests, request => request.Method == "session.detach");
    }

    [Fact]
    public async Task NativeMissingMeasurementsRemainNullAndDuplicateEventsKeepTheirStableIdentity()
    {
        await using var external = new ControlledCopilotRuntime { EmitUsageAfterCreate = false };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!, SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        var data = new { model = external.ModelId };
        await external.EmitUsageAsync(data);
        await external.EmitUsageAsync(data);
        await using var usage = session.ReadUsageAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await usage.MoveNextAsync());
        var first = usage.Current;
        Assert.Null(first.InputTokens);
        Assert.Null(first.OutputTokens);
        Assert.Null(first.CacheReadTokens);
        Assert.Null(first.CacheWriteTokens);
        Assert.Null(first.ReasoningTokens);
        Assert.Null(first.TotalNanoAiu);
        Assert.Null(first.DurationMilliseconds);
        Assert.True(await usage.MoveNextAsync());
        Assert.Equal(first, usage.Current);
    }

    [Fact]
    public async Task SdkCredentialInvalidationDuringTheEffectiveModelWaitCannotPublishSessionFacts()
    {
        var credential = SdkCredential();
        await using var external = new ControlledCopilotRuntime
        {
            BeforeEffectiveModelResponse = credential.Invalidate
        };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(
                registration, registration.Binding.ModelSelectionReference!, credential, _ => Task.CompletedTask, timeout.Token));
        Assert.Equal("runtime_sdk_credential_unavailable", failure.Code);
        Assert.Contains(external.Requests, request => request.Method == "session.detach");
        Assert.False(credential.IsUsable());
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("initiator")]
    [InlineData("model")]
    public async Task ForeignNativeAttributionFaultsTheUsageReader(string foreign)
    {
        await using var external = new ControlledCopilotRuntime { EmitUsageAfterCreate = false };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await Factory(external).CreateAsync(
            registration, registration.Binding.ModelSelectionReference!, SdkCredential(), _ => Task.CompletedTask, timeout.Token);
        await external.EmitUsageAsync(external.UsageData(
            model: foreign == "model" ? "other-model" : null,
            initiator: foreign == "initiator" ? "subagent" : null),
            agentId: foreign == "agent" ? "foreign-agent" : null);
        await using var usage = session.ReadUsageAsync(timeout.Token).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(async () =>
            await usage.MoveNextAsync());
        Assert.Equal("runtime_sdk_usage_binding_invalid", error.Code);
    }

    [Fact]
    public async Task SdkCredentialInvalidationDuringPreparationCannotCreateANativeSession()
    {
        var credential = SdkCredential();
        await using var external = new ControlledCopilotRuntime
        {
            BeforeStatusResponse = _ =>
            {
                credential.Invalidate();
                return Task.CompletedTask;
            }
        };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration,
                registration.Binding.ModelSelectionReference!, credential, _ => Task.CompletedTask, timeout.Token));
        Assert.Equal("runtime_sdk_credential_unavailable", failure.Code);
        Assert.Contains(external.Requests, request => request.Method == "status.get");
        Assert.DoesNotContain(external.Requests, request => request.Method == "session.create");
    }

    [Fact]
    public async Task CreationAuthorityIsRequiredAfterSdkPreparationAndBeforeSessionCreation()
    {
        await using var external = new ControlledCopilotRuntime();
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration,
                registration.Binding.ModelSelectionReference!, SdkCredential(), _ =>
                {
                    Assert.Contains(external.Requests, request => request.Method == "status.get");
                    throw new RuntimeAuthorizationException("runtime_registration_stale");
                }, timeout.Token));
        Assert.Equal("runtime_registration_stale", failure.Code);
        Assert.DoesNotContain(external.Requests, request => request.Method == "session.create");
    }

    internal static RuntimeCopilotSessionFactory Factory(
        ControlledCopilotRuntime external, ModelSourceMode mode = ModelSourceMode.HostedCopilot,
        TimeSpan? abortDrainTimeout = null) =>
        new(external.Connection, Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-model-reference"] = new("controlled-model", mode,
                    mode == ModelSourceMode.Byok ? new RuntimeByokProvider(
                        "openai", new Uri("https://byok.test/v1"), "responses") : null)
            }, abortDrainTimeout);

    [Theory]
    [InlineData("openai", "responses", null, "https://byok.test/v1")]
    [InlineData("openai", "completions", null, "https://byok.test/v1")]
    [InlineData("azure", "responses", "2025-04-01-preview", "https://byok.test/openai")]
    public async Task ByokUsesExplicitProviderWithoutCopilotCatalogOrGitHubToken(
        string type, string wireApi, string? azureApiVersion, string expectedBaseUrl)
    {
        await using var external = new ControlledCopilotRuntime { Byok = true };
        var provider = new RuntimeByokProvider(type, new Uri(
            type == "azure" ? "https://byok.test/" : "https://byok.test/v1"), wireApi, azureApiVersion);
        var factory = new RuntimeCopilotSessionFactory(external.Connection,
            Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
            new Dictionary<string, RuntimeModelBinding>
            {
                ["accepted-model-reference"] = new("controlled-model", ModelSourceMode.Byok, provider)
            });
        var registration = Registration();
        registration = registration with
        {
            Binding = registration.Binding with { ModelSourceMode = ModelSourceMode.Byok }
        };
        using var credential = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await factory.CreateAsync(registration,
            registration.Binding.ModelSelectionReference!,
            new SecretCredential(external.SdkCredential, DateTimeOffset.UtcNow.AddMinutes(2)),
            _ => Task.CompletedTask, credential.Token);

        var create = Assert.Single(external.Requests, request => request.Method == "session.create");
        var sdkProvider = create.Parameters.GetProperty("provider");
        Assert.Equal(type, sdkProvider.GetProperty("type").GetString());
        Assert.Equal(expectedBaseUrl, sdkProvider.GetProperty("baseUrl").GetString());
        Assert.Equal(wireApi, sdkProvider.GetProperty("wireApi").GetString());
        Assert.Equal(external.ModelId, sdkProvider.GetProperty("modelId").GetString());
        Assert.Equal(external.ModelId, sdkProvider.GetProperty("wireModel").GetString());
        if (azureApiVersion is not null)
            Assert.Equal(azureApiVersion, sdkProvider.GetProperty("azure").GetProperty("apiVersion").GetString());
        Assert.DoesNotContain(external.Requests, request => request.Method == "models.list");
        Assert.Equal("byok", session.Facts.SourceMode);
        Assert.Equal(SdkMeterSources.ByokTokens, session.Facts.MeterSource);
        Assert.Null(session.Facts.ModelMultiplier);
        await using var usage = session.ReadUsageAsync(credential.Token).GetAsyncEnumerator();
        Assert.True(await usage.MoveNextAsync());
        Assert.Equal(17, usage.Current.InputTokens);
        Assert.Null(usage.Current.TotalNanoAiu);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ModelSourceMode.Byok)]
    public async Task MissingOrIncompatibleAcceptedSourceModeCannotStartSdk(ModelSourceMode? sourceMode)
    {
        await using var external = new ControlledCopilotRuntime();
        var registration = Registration();
        registration = registration with
        {
            Binding = registration.Binding with { ModelSourceMode = sourceMode }
        };
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration, registration.Binding.ModelSelectionReference!,
                SdkCredential(), _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("runtime_model_source_mode_mismatch", failure.Code);
        Assert.Empty(external.Requests);
    }

    [Theory]
    [InlineData("raw-byok-or-installation-key")]
    [InlineData("ghs_installation_token")]
    [InlineData("ghu_token with whitespace")]
    [InlineData("{\"status\":\"signed-in\",\"accessToken\":\"ghs_installation_token\"}")]
    [InlineData("{\"status\":\"reauth-required\",\"accessToken\":\"user-token\"}")]
    [InlineData("{\"status\":\"signed-in\",\"accessToken\":\"user-token\",\"AccessToken\":\"other-token\"}")]
    public async Task RawKeysInstallationTokensAndCredentialEnvelopesCannotReachHostedSdk(string value)
    {
        await using var external = new ControlledCopilotRuntime();
        var registration = Registration();
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration, registration.Binding.ModelSelectionReference!,
                new SecretCredential(value, DateTimeOffset.UtcNow.AddMinutes(2)),
                _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("runtime_copilot_access_token_invalid", failure.Code);
        Assert.Empty(external.Requests);
    }

    [Fact]
    public async Task ExpiredHostedAccessTokenCannotReachNativeSdk()
    {
        await using var external = new ControlledCopilotRuntime();
        var registration = Registration();
        var credential = new SecretCredential(
            external.SdkCredential, DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.Throws<InvalidOperationException>(() =>
            credential.LimitLifetime(DateTimeOffset.UtcNow.AddMinutes(-1)));
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateAsync(registration, registration.Binding.ModelSelectionReference!,
                credential, _ => Task.CompletedTask, CancellationToken.None));
        Assert.Equal("runtime_sdk_credential_unavailable", failure.Code);
        Assert.False(credential.IsUsable());
        Assert.Empty(external.Requests);
    }

    internal static SecretCredential SdkCredential() =>
        new("ghu_external-sdk-credential", DateTimeOffset.UtcNow.AddMinutes(2));

    internal static RuntimeRegistration Registration() => new(
        Guid.NewGuid(), 1,
        new RuntimeBinding(
            "https://broker.test/", Guid.NewGuid().ToString("D"), "tenant", "project", "run",
            "session", "agent", "turn", 1, 1, 1, "context:1", new string('a', 64), 1,
            "environment", "placement", 1, "profile", new Uri("https://runtime.test/configure"),
            new Uri("https://orchestrator.test/internal/runtime/observations"))
        {
            EnvironmentCurrentFencingGeneration = 4,
            EnvironmentProviderFencingGeneration = 7,
            ModelSelectionReference = "accepted-model-reference",
            ModelSourceMode = ModelSourceMode.HostedCopilot
        },
        RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
}
