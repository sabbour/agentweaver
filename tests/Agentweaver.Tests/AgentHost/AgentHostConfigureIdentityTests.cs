extern alias agenthost;

using System.Text.Json;
using FluentAssertions;
using Xunit;

using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using ConfigureRequest = agenthost::ConfigureRequest;

namespace Agentweaver.Tests.AgentHost;

/// <summary>
/// Regression guard for issue #335: the warm-pool <c>POST /configure</c> path must carry the run's
/// projectId and agentName so the in-pod <c>CopilotAIAgent.SetupAsync</c> receives them and injects
/// the Agentweaver API tools (record_memory, get_memory, submit_decision, ...). Before the fix the
/// per-run configuration omitted these entirely, so warm pods fell back to the empty static
/// <c>AgentHost__ProjectId</c>/<c>AgentName</c> options and the memory tools were never injected.
/// </summary>
public sealed class AgentHostConfigureIdentityTests
{
    [Fact]
    public void ConfigureRequest_carries_projectId_and_agentName_into_run_configuration()
    {
        var request = JsonSerializer.Deserialize<ConfigureRequest>(
            """{"runId":"run-335","projectId":"project-335","agentName":"Stark"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        request.Should().NotBeNull();
        var configuration = request!.ToRunConfiguration();
        configuration.ProjectId.Should().Be("project-335");
        configuration.AgentName.Should().Be("Stark");
    }

    [Fact]
    public void TryConfigure_applies_projectId_and_agentName_to_runtime_state()
    {
        var state = new AgentHostRuntimeState();

        var applied = state.TryConfigure(new AgentHostRunConfiguration(
            RunId: "run-335",
            UserId: "sabbour",
            TurnBearerToken: "tok",
            KvUserSecretName: null,
            GitHubAccessToken: null,
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: "/workspace/run-335",
            ProjectId: "project-335",
            AgentName: "Stark"));

        applied.Should().BeTrue();
        state.ProjectId.Should().Be("project-335");
        state.AgentName.Should().Be("Stark");
    }
}
