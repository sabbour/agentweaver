using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

/// <summary>
/// Regression coverage for the per-turn client refresh path on a BYOK deployment. A BYOK pod is
/// deliberately sent NO GitHub Copilot capability credential, so the pre-call refresh predicate
/// ("credential is null or expiring") used to be true on every turn and dropped the run into the
/// Copilot-only client path, failing each turn with <c>github_copilot_auth_required</c>.
/// </summary>
public sealed class ByokTurnRefreshTests
{
    [Fact]
    public async Task ByokMode_NeverRefreshesCopilotClient_AcrossManyTurns()
    {
        var credentials = new CountingCopilotCapabilityCredentialProvider();
        await using var agent = CreateAgent(
            credentials, new StubByokProviderConfigurationProvider(ByokProvider()));

        for (var turn = 0; turn < 5; turn++)
        {
            (await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None))
                .Should().BeFalse("a BYOK run holds no Copilot credential to refresh");
        }

        credentials.GetCredentialCalls.Should().Be(
            0,
            "the Copilot run-bound credential path must never be consulted in BYOK mode");
    }

    [Fact]
    public async Task ByokMode_ResolvesActiveProviderOnce_AndUsesTheByokClientPath()
    {
        var byokProvider = new StubByokProviderConfigurationProvider(ByokProvider());
        var credentials = new CountingCopilotCapabilityCredentialProvider();
        await using var agent = CreateAgent(credentials, byokProvider);

        await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None);
        await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None);

        (await agent.ResolveByokProviderConfigurationAsync(CancellationToken.None))
            .Should().NotBeNull();
        byokProvider.GetCalls.Should().Be(1, "the active model source is resolved once per run");

        await using var client = await agent.CreateProviderClientAsync(CancellationToken.None);
        client.Should().NotBeNull();
        credentials.GetCredentialCalls.Should().Be(
            0, "BYOK client creation must not redeem a Copilot capability snapshot");
    }

    [Fact]
    public async Task CopilotMode_MissingCredential_StillTriggersRefresh()
    {
        var credentials = new CountingCopilotCapabilityCredentialProvider(credential: null);
        await using var agent = CreateAgent(credentials, byokProvider: null);

        (await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None))
            .Should().BeTrue("a Copilot-mode run with no live credential must still refresh");
        credentials.GetCredentialCalls.Should().Be(1);
    }

    [Fact]
    public async Task CopilotMode_LiveCredential_DoesNotRefresh()
    {
        var credentials = new CountingCopilotCapabilityCredentialProvider(
            new GitHubCapabilitySnapshotCredential("snapshot-test", "token", DateTimeOffset.UtcNow.AddHours(1)));
        await using var agent = CreateAgent(credentials, byokProvider: null);

        (await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task CopilotMode_MissingCredential_StillFailsClosedOnClientCreation()
    {
        var credentials = new CountingCopilotCapabilityCredentialProvider(credential: null);
        await using var agent = CreateAgent(credentials, byokProvider: null);

        var act = async () => await agent.CreateProviderClientAsync(CancellationToken.None);

        await act.Should().ThrowAsync<GitHubCopilotUnauthorizedException>()
            .Where(ex => ex.ErrorCode == GitHubCopilotUnauthorizedException.AuthRequiredErrorCode);
    }

    [Fact]
    public async Task NoRunId_DoesNotRefresh()
    {
        var credentials = new CountingCopilotCapabilityCredentialProvider();
        await using var agent = CreateAgent(credentials, byokProvider: null, runId: "");

        (await agent.ShouldRefreshClientBeforeAiCallAsync(CancellationToken.None))
            .Should().BeFalse();
        credentials.GetCredentialCalls.Should().Be(0);
    }

    private static ByokProviderConfiguration ByokProvider() =>
        new(
            Id: "byok-1",
            Name: "Contoso",
            Type: "openai",
            BaseUrl: "https://contoso.example/v1",
            Model: "contoso-large",
            ApiKey: "byok-key");

    private static TestCopilotAIAgent CreateAgent(
        IGitHubCopilotCapabilityCredentialProvider credentials,
        IByokProviderConfigurationProvider? byokProvider,
        string runId = "run-byok-turn-refresh")
    {
        var factory = new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), credentials);
        var agent = new TestCopilotAIAgent(
            factory,
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance,
            byokProviderConfiguration: byokProvider);
        agent.SetRunIdForTest(runId);
        return agent;
    }

    /// <summary>
    /// Exposes the per-run identifier that <see cref="CopilotAIAgent.SetupAsync"/> would normally
    /// assign, so the refresh decision can be exercised without provisioning a real Copilot client.
    /// </summary>
    private sealed class TestCopilotAIAgent(
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IByokProviderConfigurationProvider? byokProviderConfiguration)
        : CopilotAIAgent(
            factory,
            executor,
            sandboxPolicyStore,
            approvalStore,
            toolApprovalGate,
            logger,
            byokProviderConfiguration: byokProviderConfiguration)
    {
        public void SetRunIdForTest(string runId) => _runId = runId;
    }

    private sealed class StubByokProviderConfigurationProvider(ByokProviderConfiguration? configuration)
        : IByokProviderConfigurationProvider
    {
        public int GetCalls { get; private set; }

        public Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct)
        {
            GetCalls++;
            return Task.FromResult(configuration);
        }
    }

    private sealed class CountingCopilotCapabilityCredentialProvider(
        GitHubCapabilitySnapshotCredential? credential = null)
        : IGitHubCopilotCapabilityCredentialProvider
    {
        public int GetCredentialCalls { get; private set; }

        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
            string runId, CancellationToken ct = default)
        {
            GetCredentialCalls++;
            return Task.FromResult(credential);
        }
    }
}
