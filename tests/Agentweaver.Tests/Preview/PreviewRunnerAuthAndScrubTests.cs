extern alias agenthost;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using PreviewRunner = agenthost::Agentweaver.AgentHost.PreviewRunner;
using PreviewRunnerOptions = agenthost::Agentweaver.AgentHost.PreviewRunnerOptions;
using PreviewRunnerEndpointAuth = agenthost::PreviewRunnerEndpointAuth;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// AgentHost-side security coverage for the deterministic preview step (spec-006 §11):
/// <list type="bullet">
///   <item>BLOCKER 2/A — <c>PreviewRunnerEndpointAuth</c> accepts either the per-run turn token OR the
///   per-run preview-runner credential, and is fail-closed (401) when a credential is configured.</item>
///   <item>BLOCKER A — the child preview process environment is SCRUBBED of the turn token, the
///   preview-runner credential, and known secret env names.</item>
/// </list>
/// </summary>
public sealed class PreviewRunnerAuthAndScrubTests
{
    private const string TurnToken = "turn-token-abc";
    private const string PreviewCredential = "preview-cred-xyz";

    private static AgentHostRuntimeState Configured(string? turn, string? credential)
    {
        var state = new AgentHostRuntimeState();
        state.TryConfigure("run-1", "user-1", turn ?? string.Empty, null, credential);
        return state;
    }

    private static HttpContext ContextWithBearer(string? bearer)
    {
        var ctx = new DefaultHttpContext();
        if (bearer is not null)
            ctx.Request.Headers["Authorization"] = "Bearer " + bearer;
        return ctx;
    }

    [Fact]
    public void Authorize_WithTurnToken_Succeeds()
    {
        var state = Configured(TurnToken, PreviewCredential);
        PreviewRunnerEndpointAuth.Authorize(ContextWithBearer(TurnToken), state).Should().BeTrue();
    }

    [Fact]
    public void Authorize_WithPreviewCredential_Succeeds()
    {
        var state = Configured(TurnToken, PreviewCredential);
        PreviewRunnerEndpointAuth.Authorize(ContextWithBearer(PreviewCredential), state).Should().BeTrue();
    }

    [Fact]
    public void Authorize_WrongToken_FailsClosed_WhenCredentialConfigured()
    {
        var state = Configured(TurnToken, PreviewCredential);
        PreviewRunnerEndpointAuth.Authorize(ContextWithBearer("nope"), state).Should().BeFalse();
    }

    [Fact]
    public void Authorize_MissingToken_FailsClosed_WhenCredentialConfigured()
    {
        var state = Configured(null, PreviewCredential);
        PreviewRunnerEndpointAuth.Authorize(ContextWithBearer(null), state).Should().BeFalse();
    }

    [Fact]
    public void Authorize_DevAllow_OnlyWhenNoCredentialSet()
    {
        var state = Configured(null, null);
        PreviewRunnerEndpointAuth.Authorize(ContextWithBearer(null), state).Should().BeTrue();
    }

    [Fact]
    public void BuildScrubbedChildEnvironment_RemovesTokenCredentialAndSecretNames()
    {
        // Arrange: seed the parent environment so the child would inherit these unless scrubbed.
        var aliasedSecretVar = "MY_APP_SECRET_KEY";
        Environment.SetEnvironmentVariable(aliasedSecretVar, PreviewCredential);
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "gh-token-123");
        Environment.SetEnvironmentVariable("PreviewRunnerCredential", PreviewCredential);
        try
        {
            var runtimeState = Configured(TurnToken, PreviewCredential);
            var runner = new PreviewRunner(
                Options.Create(new PreviewRunnerOptions()),
                NullLogger<PreviewRunner>.Instance,
                clock: null,
                runtimeState: runtimeState);

            // Act
            var env = runner.BuildScrubbedChildEnvironmentForTest("echo hi", Path.GetTempPath());

            // Assert: no env var carries a credential VALUE, and known secret names are gone.
            env.Values.Should().NotContain(TurnToken);
            env.Values.Should().NotContain(PreviewCredential);
            env.Should().NotContainKey("GITHUB_TOKEN");
            env.Should().NotContainKey("PreviewRunnerCredential");
            env.Should().NotContainKey(aliasedSecretVar); // scrubbed by *SECRET* name heuristic
        }
        finally
        {
            Environment.SetEnvironmentVariable(aliasedSecretVar, null);
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("PreviewRunnerCredential", null);
        }
    }
}
