using Agentweaver.Api.Auth;
using FluentAssertions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// The probe must use the SAME SDK/native runtime path the Copilot CLI uses so Agentweaver's own
/// OAuth app registration does not determine the outcome.
/// </summary>
public sealed class GitHubCopilotEntitlementProbeTests
{
    [Fact]
    public async Task Probe_returns_true_when_sdk_lists_models()
    {
        var factory = new StubClientFactory
        {
            Client = new StubClient
            {
                Models = [new ModelInfo { Id = "gpt-5" }],
            },
        };
        var probe = CreateProbe(factory);

        var result = await probe.ProbeAsync("gho_test-token");

        result.Should().BeTrue();
        factory.LastAccessToken.Should().Be("gho_test-token");
        factory.Client.Started.Should().BeTrue();
        factory.Client.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_is_inconclusive_when_sdk_listing_fails()
    {
        var factory = new StubClientFactory
        {
            Client = new StubClient
            {
                Exception = new InvalidOperationException("Failed to list models"),
            },
        };
        var probe = CreateProbe(factory);

        (await probe.ProbeAsync("gho_test-token")).Should().BeNull();
        factory.Client.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Probe_is_inconclusive_without_a_token()
    {
        var factory = new StubClientFactory();
        var probe = CreateProbe(factory);

        (await probe.ProbeAsync("  ")).Should().BeNull();
        factory.LastAccessToken.Should().BeNull();
    }

    private static GitHubCopilotEntitlementProbe CreateProbe(StubClientFactory factory) =>
        new(factory, NullLogger<GitHubCopilotEntitlementProbe>.Instance);

    private sealed class StubClientFactory : ICopilotEntitlementSdkClientFactory
    {
        public string? LastAccessToken { get; private set; }

        public StubClient Client { get; set; } = new();

        public ICopilotEntitlementSdkClient Create(string accessToken)
        {
            LastAccessToken = accessToken;
            return Client;
        }
    }

    private sealed class StubClient : ICopilotEntitlementSdkClient
    {
        public IList<ModelInfo> Models { get; set; } = [];

        public Exception? Exception { get; set; }

        public bool Started { get; private set; }

        public bool Disposed { get; private set; }

        public Task<IList<ModelInfo>> ListModelsAsync(CancellationToken ct)
        {
            Started = true;
            if (Exception is not null)
                throw Exception;

            return Task.FromResult(Models);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
