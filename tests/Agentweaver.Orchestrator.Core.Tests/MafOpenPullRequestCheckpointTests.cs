using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Orchestrator.Core;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

public sealed class MafOpenPullRequestCheckpointTests
{
    [Fact]
    public void PersistedIntentRecoversExactlyAndOnlyOwnerVerifiedResultSucceeds()
    {
        var fixture = CreateFixture();
        var running = CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(
            fixture.Checkpoint, fixture.Intent)!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(running));

        Assert.Equal(MafExecutionTaskStatus.Running, recovered.Progress.NonModelSteps["publish"]);
        Assert.True(MafExecutionCheckpointContract.OpenPullRequestIntentsMatch(
            fixture.Intent, recovered.OpenPullRequestIntents["publish"]));
        Assert.Null(CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(
            new(fixture.Checkpoint.Info, recovered), fixture.Intent));
        Assert.Equal(fixture.Intent.Request, recovered.OpenPullRequestIntents["publish"].Request);

        var pullRequest = PullRequest(fixture.Intent);
        var receipt = MafOpenPullRequestContract.CreateReceipt(fixture.Intent, pullRequest);
        var completed = CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(
            new(fixture.Checkpoint.Info, recovered), fixture.Intent, receipt)!;
        var restored = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(completed));

        Assert.Equal(MafExecutionTaskStatus.Succeeded, restored.Progress.NonModelSteps["publish"]);
        Assert.Equal(receipt, restored.OpenPullRequestReceipts["publish"]);
        Assert.Contains("\"pullRequestNumber\":42",
            MafOpenPullRequestContract.ResultSummary(receipt), StringComparison.Ordinal);
        Assert.DoesNotContain("patch", MafOpenPullRequestContract.ResultSummary(receipt), StringComparison.Ordinal);
        Assert.Null(CoordinationEndpoints.PrepareOpenPullRequestCompletionCheckpoint(
            new(fixture.Checkpoint.Info, restored), fixture.Intent, receipt));
    }

    [Fact]
    public void OwnerDecisionAndWorkspaceChangesFailClosed()
    {
        var fixture = CreateFixture();
        var intent = fixture.Intent;
        var source = fixture.Source;
        var pin = source.Pin;
        var actor = source.Actor;

        var changedPin = new SourceControlRepositoryPin(
            "replacement-pin",
            pin.AcceptedRun,
            pin.ProviderBinding,
            pin.Repository,
            pin.ApiCredential,
            pin.CheckoutCredential,
            pin.WebhookCredential,
            pin.ProviderRepositoryId,
            pin.DefaultBranch,
            pin.IsPrivate,
            pin.PinnedAt,
            pin.GitHubAppBinding);
        var ownerChanged = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireCurrentSource(
                intent, actor, intent.Identity, "tenant-1", intent.DecisionStateVersion,
                intent.ExecutionFence, intent.AcceptedSelectionHash, changedPin));
        Assert.Equal("source_control_run_binding_changed", ownerChanged.Code);

        var decisionChanged = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireCurrentSource(
                intent, actor, intent.Identity, "tenant-1", intent.DecisionStateVersion + 1,
                intent.ExecutionFence, intent.AcceptedSelectionHash, pin));
        Assert.Equal("source_control_run_binding_changed", decisionChanged.Code);

        var running = CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(
            fixture.Checkpoint, intent)!;
        var changedWorkspaceIntent = intent with
        {
            Workspace = intent.Workspace with { WorkspaceIncarnationId = Guid.NewGuid() }
        };
        var changedWorkspace = running with
        {
            Revision = running.Revision + 1,
            OpenPullRequestIntents = running.OpenPullRequestIntents.SetItem(
                intent.StepId, changedWorkspaceIntent)
        };
        Assert.Throws<ArgumentException>(() =>
            MafExecutionCheckpointContract.ValidateTransition(running, changedWorkspace));
    }

    [Fact]
    public async Task SourceControlFailureLeavesGateRunningAndBlocksDependents()
    {
        var fixture = CreateFixture();
        var running = CoordinationEndpoints.PrepareOpenPullRequestCheckpoint(
            fixture.Checkpoint, fixture.Intent)!;
        var recovered = MafExecutionCheckpointContract.Deserialize(
            MafExecutionCheckpointContract.Serialize(running));

        using var client = new HttpClient(new SourceControlFailureHandler())
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        var owner = new MafSourceControlOwnerClient(
            client, new MafSourceControlOwnerOptions(new Uri("https://orchestrator.example/")));
        var runtimeActor = new RuntimeActorAuthorization(
            new SecretCredential("test-owner-token", DateTimeOffset.UtcNow.AddMinutes(1)),
            fixture.Source.Actor.Subject);
        var failure = await Assert.ThrowsAsync<CoordinationException>(() =>
            owner.CreateOrReusePullRequestAsync(
                runtimeActor, fixture.Intent.Identity, fixture.Intent.Request, CancellationToken.None));

        Assert.Equal("source_control_pull_request_conflict", failure.Code);
        Assert.Empty(recovered.OpenPullRequestReceipts);
        Assert.Equal(MafExecutionTaskStatus.Running, recovered.Progress.NonModelSteps["publish"]);

        var workflow = WorkflowTestData.Snapshot(WorkflowTestData.Definition(
            WorkflowDefinitionOrigin.BuiltIn,
            WorkflowTestData.Open("implement", 0, []),
            WorkflowTestData.Platform("publish", 1, ["implement"], WorkflowPlatformGate.OpenPullRequest),
            WorkflowTestData.Open("followup", 2, ["publish"])));
        var plan = WorkPlanValidator.ValidateAndSnapshot(
            workflow,
            WorkflowTestData.Plan(
                WorkflowTestData.Item("implementation"),
                WorkflowTestData.Item("follow-up", "followup", dependsOn: ["implementation"])),
            WorkflowTestData.SelectionContext()).Value!;
        var progress = recovered.Progress with
        {
            WorkItems = recovered.Progress.WorkItems
                .SetItem("implementation", MafExecutionTaskStatus.Succeeded)
                .SetItem("follow-up", MafExecutionTaskStatus.Pending)
        };

        var frontier = MafExecutionPlanner.BuildFrontier(
            plan, progress, 0, 0, 10, 10,
            hasNonModelExecutor: step => step.PlatformGate == WorkflowPlatformGate.OpenPullRequest);

        Assert.DoesNotContain(frontier.ReadyActions, action => action.Step.Id == "followup");
        Assert.DoesNotContain("followup", frontier.FailedDependencyIds);
    }

    [Fact]
    public void OwnerResultMustMatchThePersistedHeadAndRepositoryUrl()
    {
        var fixture = CreateFixture();
        var changedHead = PullRequest(fixture.Intent) with { HeadSha = new string('d', 40) };
        var changedUrl = PullRequest(fixture.Intent) with
        {
            HtmlUrl = new Uri("https://github.com/octo/other/pull/42")
        };

        var headFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.CreateReceipt(fixture.Intent, changedHead));
        var urlFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.CreateReceipt(fixture.Intent, changedUrl));
        Assert.Equal("source_control_pull_request_response_invalid", headFailure.Code);
        Assert.Equal("source_control_pull_request_response_invalid", urlFailure.Code);
    }

    [Fact]
    public void ChangedWorkspaceHeadOrBaseIsRejectedBeforePullRequestCreation()
    {
        var fixture = CreateFixture();
        var changedHead = new SourceControlWorkspaceDiffView(
            fixture.Intent.Workspace.WorkspaceId,
            fixture.Intent.Workspace.BaseSha,
            new string('d', 40),
            "changed",
            string.Empty);
        var changedBase = changedHead with
        {
            HeadSha = fixture.Intent.Workspace.HeadSha,
            BaseSha = new string('b', 40)
        };

        var headFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireWorkspaceRevision(fixture.Intent, changedHead));
        var baseFailure = Assert.Throws<CoordinationException>(() =>
            MafOpenPullRequestContract.RequireWorkspaceRevision(fixture.Intent, changedBase));
        Assert.Equal("source_control_run_binding_changed", headFailure.Code);
        Assert.Equal("source_control_run_binding_changed", baseFailure.Code);
    }

    private static FixtureState CreateFixture()
    {
        var source = MafBuildTestSourceInputTests.Fixture().Run;
        var workspace = new SourceControlPullRequestWorkspaceLocator(
            "workspace",
            Guid.Parse("5956a121-5556-4c16-848c-0d4a2937966d"),
            new string('a', 40),
            "feature/agent-change",
            new string('c', 40),
            source.Pin.DefaultBranch);
        var intent = MafOpenPullRequestContract.CreateIntent(
            Guid.Parse("c252ff87-659a-45dc-a307-41369cbe61f7"),
            source.Identity,
            "open-pr-checkpoint",
            2,
            "plan-1",
            "publish",
            3,
            source.Pin.AcceptedRun.Fence,
            source.Pin.AcceptedRun.AcceptedSelectionHash,
            source.Pin,
            workspace,
            1);
        var progress = MafExecutionProgress.Empty with
        {
            NonModelSteps = MafExecutionProgress.Empty.NonModelSteps.Add(
                "publish", MafExecutionTaskStatus.Pending)
        };
        return new(
            source,
            intent,
            new(
                new CheckpointInfo(source.Identity.SessionId, "initial"),
                new(1, "plan-1", 3, progress)));
    }

    private static SourceControlPullRequest PullRequest(MafExecutionOpenPullRequestIntent intent) =>
        new(
            42,
            new Uri("https://github.com/octo/repo/pull/42"),
            "open",
            intent.Request.HeadBranch,
            intent.Request.ExpectedHeadSha,
            intent.Request.BaseBranch,
            intent.Request.ExpectedBaseSha,
            false,
            SourceControlPullRequestDisposition.Created);

    private sealed class SourceControlFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/source-control/sessions/root/pull-requests",
                request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                RequestMessage = request,
                Content = new StringContent(
                    """{"error":"source_control_pull_request_conflict"}""",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed record FixtureState(
        SourceControlRunContext Source,
        MafExecutionOpenPullRequestIntent Intent,
        MafExecutionCheckpointSnapshot Checkpoint);
}
