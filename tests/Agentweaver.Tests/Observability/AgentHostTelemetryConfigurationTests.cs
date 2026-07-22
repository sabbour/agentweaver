extern alias agenthost;

using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;

using AgentHostAzureMonitorBootstrap = agenthost::Agentweaver.AgentHost.AzureMonitorBootstrap;

namespace Agentweaver.Tests.Observability;

public sealed class AgentHostTelemetryConfigurationTests
{
    [Fact]
    public void AgentHost_subscribes_to_Agentweaver_traces_and_metrics()
    {
        AgentHostAzureMonitorBootstrap.TraceSources.Should().Contain("Agentweaver");
        AgentHostAzureMonitorBootstrap.MetricMeters.Should().Contain("Agentweaver");
    }

    [Fact]
    public void Run_metrics_include_project_run_and_parent_correlation()
    {
        var run = CreateRun();

        AssertCorrelationTags(RunOrchestrator.BuildRunTags(run));
        AssertCorrelationTags(RunWatchLoopService.BuildRunTags(run));
    }

    private static void AssertCorrelationTags(KeyValuePair<string, object?>[] tags)
    {
        tags.Should().Contain(new KeyValuePair<string, object?>("project.id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        tags.Should().Contain(new KeyValuePair<string, object?>("run_id", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        tags.Should().Contain(new KeyValuePair<string, object?>("run.id", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        tags.Should().Contain(new KeyValuePair<string, object?>("parent_run_id", "cccccccc-cccc-cccc-cccc-cccccccccccc"));
    }

    private static Run CreateRun() => new()
    {
        Id = RunId.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        ProjectId = ProjectId.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        ParentRunId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
        RepositoryPath = "repo",
        OriginatingBranch = "dev",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "test",
        SubmittingUser = "test",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        AgentName = "morpheus",
    };
}
