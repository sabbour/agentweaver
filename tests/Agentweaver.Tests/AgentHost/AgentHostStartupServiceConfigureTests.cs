extern alias agenthost;
using agenthost::Agentweaver.AgentHost;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using AgentHostOptions = agenthost::Agentweaver.AgentHost.AgentHostOptions;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostStartupService = agenthost::Agentweaver.AgentHost.AgentHostStartupService;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// Regression guard for bug #221: the per-run <c>AutoApproveTools</c> flag delivered by the API in
/// the warm-pool <c>POST /configure</c> body must seed the pod's <see cref="IRunOptionsStore"/> so
/// <c>CopilotAIAgent</c>'s HITL gate auto-approves <c>web_fetch</c> under autopilot. Before the fix
/// the pod booted a fresh store defaulting <c>AutoApproveTools=false</c>, so every request stalled
/// the 5-minute gate and auto-denied.
/// </summary>
public sealed class AgentHostStartupServiceConfigureTests
{
    [Fact]
    public void ConfigureAsync_seeds_pod_run_options_with_autoApproveTools()
    {
        const string runId = "run-configure-221";
        var runOptions = new InMemoryRunOptionsStore();

        var service = new AgentHostStartupService(
            BuildAgent(runOptions),
            Options.Create(new AgentHostOptions()),
            new AgentHostRuntimeState(),
            runOptions,
            NullLogger<AgentHostStartupService>.Instance);

        // ConfigureAsync seeds the run-options store synchronously (before any await) and only THEN
        // begins SetupAsync — which needs a live Copilot client we don't have here. We therefore do
        // NOT await the returned task; we assert the synchronous store seeding and observe the task
        // so its eventual (expected) SetupAsync failure never surfaces as an unobserved exception.
        var task = service.ConfigureAsync(
            runId, userId: "sabbour", turnBearerToken: "tok",
            kvUserSecretName: null, gitHubAccessToken: null, workingDirectory: null,
            autoApproveTools: true, ct: new CancellationToken(canceled: true));
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

        runOptions.Get(runId).AutoApproveTools.Should().BeTrue(
            "the AutoApproveTools flag from /configure must seed the pod's IRunOptionsStore (bug #221)");
    }

    [Fact]
    public void ConfigureAsync_leaves_autoApproveTools_false_when_flag_off()
    {
        const string runId = "run-configure-221-off";
        var runOptions = new InMemoryRunOptionsStore();

        var service = new AgentHostStartupService(
            BuildAgent(runOptions),
            Options.Create(new AgentHostOptions()),
            new AgentHostRuntimeState(),
            runOptions,
            NullLogger<AgentHostStartupService>.Instance);

        var task = service.ConfigureAsync(
            runId, userId: "sabbour", turnBearerToken: "tok",
            kvUserSecretName: null, gitHubAccessToken: null, workingDirectory: null,
            autoApproveTools: false, ct: new CancellationToken(canceled: true));
        _ = task.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);

        runOptions.Get(runId).AutoApproveTools.Should().BeFalse();
    }

    private static CopilotAIAgent BuildAgent(IRunOptionsStore runOptions)
    {
        var config = new ConfigurationBuilder().Build();
        var factory = new GitHubCopilotClientFactory(
            config, new NullGitHubTokenStore(), new FixedInstallationScopeStub());
        return new CopilotAIAgent(
            factory,
            new FixedInstallationScopeStub(),
            SandboxExecutorFactory.CreatePassthrough(),
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance,
            questionGate: null,
            runOptions: runOptions);
    }
}
