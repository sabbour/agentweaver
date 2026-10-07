using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
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
        await using var session = await factory.CreateHostedAsync(
            registration, registration.Binding.ModelSelectionReference!,
            SdkCredential(), timeout.Token);
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
    public async Task EffectiveModelMismatchRejectsAndDisposesTheActualNativeSession()
    {
        await using var external = new ControlledCopilotRuntime { EffectiveModelId = "other-model" };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            Factory(external).CreateHostedAsync(registration,
                registration.Binding.ModelSelectionReference!, SdkCredential(), timeout.Token));
        Assert.Equal("runtime_sdk_effective_model_mismatch", error.Code);
        Assert.Contains(external.Requests, request => request.Method == "session.destroy");
    }

    [Fact]
    public async Task NativeMissingMeasurementsRemainNullAndDuplicateEventsKeepTheirStableIdentity()
    {
        await using var external = new ControlledCopilotRuntime { EmitUsageAfterCreate = false };
        var registration = Registration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await Factory(external).CreateHostedAsync(
            registration, registration.Binding.ModelSelectionReference!, SdkCredential(), timeout.Token);
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
            Factory(external).CreateHostedAsync(
                registration, registration.Binding.ModelSelectionReference!, credential, timeout.Token));
        Assert.Equal("runtime_sdk_credential_unavailable", failure.Code);
        Assert.Contains(external.Requests, request => request.Method == "session.destroy");
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
        await using var session = await Factory(external).CreateHostedAsync(
            registration, registration.Binding.ModelSelectionReference!, SdkCredential(), timeout.Token);
        await external.EmitUsageAsync(external.UsageData(
            model: foreign == "model" ? "other-model" : null,
            initiator: foreign == "initiator" ? "subagent" : null),
            agentId: foreign == "agent" ? "foreign-agent" : null);
        await using var usage = session.ReadUsageAsync(timeout.Token).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(async () =>
            await usage.MoveNextAsync());
        Assert.Equal("runtime_sdk_usage_binding_invalid", error.Code);
    }

    internal static RuntimeCopilotSessionFactory Factory(ControlledCopilotRuntime external) =>
        new(external.Connection, Path.GetFullPath(Path.Combine("native-sdk-test", Guid.NewGuid().ToString("N"))),
            new Dictionary<string, string> { ["accepted-model-reference"] = "controlled-model" });

    internal static SecretCredential SdkCredential() =>
        new("external-sdk-credential", DateTimeOffset.UtcNow.AddMinutes(2));

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
            ModelSelectionReference = "accepted-model-reference"
        },
        RuntimeRegistrationState.Active, DateTimeOffset.UtcNow.AddMinutes(5));
}
