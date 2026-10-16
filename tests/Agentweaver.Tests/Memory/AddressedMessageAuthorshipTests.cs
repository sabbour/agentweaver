using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Memory;

public sealed class AddressedMessageAuthorshipTests
{
    [Fact]
    public async Task OperatorWithoutRunHeaders_ResolvesAsHumanSender()
    {
        var context = new DefaultHttpContext
        {
            User = CallerContextClaimsAdapter.ToPrincipal(
                new CallerContext { User = "operator" }, AgentweaverAuthenticationSchemes.BrokerBearer),
        };
        var (author, failure) = await RunAuthorship.ResolveMessageAsync(
            context, "project-a", new FakeResolver("project-a"), new FakeCapabilities(), default);
        failure.Should().BeNull();
        author!.AgentName.Should().Be("operator");
        author.SourceRunId.Should().BeNull();
        author.SourceIdentity.Should().Be("operator");
    }

    [Fact]
    public async Task BrokerRunHeaders_RequireValidProjectBoundCapability()
    {
        const string project = "project-a";
        var context = new DefaultHttpContext
        {
            User = CallerContextClaimsAdapter.ToPrincipal(
                new CallerContext { User = "operator" }, AgentweaverAuthenticationSchemes.BrokerBearer),
        };
        context.Request.Headers[RunAuthorshipHeaders.RunId] = "run-1";
        context.Request.Headers[RunAuthorshipHeaders.RunToken] = "valid";
        var resolver = new FakeResolver(project);
        var capabilities = new FakeCapabilities();

        var (author, failure) = await RunAuthorship.ResolveMessageAsync(
            context, project, resolver, capabilities, default);
        failure.Should().BeNull();
        author!.AgentName.Should().Be("Link");
        author.SourceRunId.Should().Be("run-1");

        context.Request.Headers[RunAuthorshipHeaders.RunToken] = "forged";
        var (forged, denied) = await RunAuthorship.ResolveMessageAsync(
            context, project, resolver, capabilities, default);
        forged.Should().BeNull();
        denied.Should().NotBeNull();

        context.Request.Headers[RunAuthorshipHeaders.RunToken] = "valid";
        var (crossProject, crossProjectDenied) = await RunAuthorship.ResolveMessageAsync(
            context, "project-b", resolver, capabilities, default);
        crossProject.Should().BeNull();
        crossProjectDenied.Should().NotBeNull();
    }

    private sealed class FakeResolver(string project) : IRunSubmittingUserResolver
    {
        public Task<string?> GetSubmittingUserAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetWorkingDirectoryAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<(string? ProjectId, string? AgentName)> GetRunIdentityAsync(
            string runId, CancellationToken ct = default)
            => Task.FromResult<(string?, string?)>((project, "Link"));
    }

    private sealed class FakeCapabilities : IRunAuthorshipCapabilityStore
    {
        public Task RegisterAsync(string runId, string token, DateTimeOffset expiresAt, CancellationToken ct)
            => Task.CompletedTask;
        public Task<bool> ValidateAsync(string runId, string token, CancellationToken ct)
            => Task.FromResult(runId == "run-1" && token == "valid");
        public Task RemoveAsync(string runId, CancellationToken ct) => Task.CompletedTask;
    }
}
