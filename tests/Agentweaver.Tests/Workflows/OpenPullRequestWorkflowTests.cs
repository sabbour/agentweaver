using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

public sealed class OpenPullRequestWorkflowTests
{
    [Fact]
    public void Loader_AcceptsOpenPullRequestNodeType()
    {
        var yaml = """
        id: open-pr-sample
        name: Open Pull Request Sample
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
          - id: open-pr
            type: open_pull_request
            label: Open Pull Request
            title: "Agentweaver: {outcome_summary}"
            body: "Automated changes from run {run_id} on {worktree_branch}."
            base: main
            head: feature/generated
            draft: true
          - id: record
            type: scribe
            label: Record
          - id: done
            type: terminal
            label: Done
        edges:
          - from: implement
            to: open-pr
          - from: open-pr
            to: record
          - from: record
            to: done
        """;

        var result = WorkflowDefinitionLoader.Load(yaml, "test");

        result.IsValid.Should().BeTrue(result.Error);
        var node = result.Definition!.Nodes.Single(n => n.Id == "open-pr");
        node.Type.Should().Be(WorkflowNodeType.OpenPullRequest);
        node.PrTitle.Should().Be("Agentweaver: {outcome_summary}");
        node.PrBody.Should().Be("Automated changes from run {run_id} on {worktree_branch}.");
        node.PrBase.Should().Be("main");
        node.PrHead.Should().Be("feature/generated");
        node.PrDraft.Should().BeTrue();
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition).Should().BeEmpty();
    }

    [Fact]
    public void Loader_AcceptsOpenPullRequestAfterPeerReviewApproval()
    {
        var yaml = """
        id: open-pr-after-review
        name: Open Pull Request After Review
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
          - id: build-test
            type: build_test
            label: Build & Test
          - id: open-pr
            type: open_pull_request
            label: Open Pull Request
          - id: record
            type: scribe
            label: Record
          - id: done
            type: terminal
            label: Done
          - id: declined
            type: terminal
            label: Declined
        edges:
          - from: implement
            to: build-test
          - from: build-test
            to: open-pr
            when: approved
          - from: build-test
            to: implement
            when: request-changes
          - from: build-test
            to: declined
            when: declined
          - from: open-pr
            to: record
          - from: record
            to: done
        """;

        var result = WorkflowDefinitionLoader.Load(yaml, "test");

        result.IsValid.Should().BeTrue(result.Error);
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition!).Should().BeEmpty();
    }

    [Fact]
    public void Loader_AcceptsOpenPullRequestAfterMerge()
    {
        var yaml = """
        id: open-pr-after-merge
        name: Open Pull Request After Merge
        start: implement
        nodes:
          - id: implement
            type: prompt
            label: Implement
          - id: review
            type: check
            label: Review
            gate_kind: human-review
            branches:
              - approved
              - request-changes
              - declined
          - id: merge
            type: merge
            label: Merge
          - id: open-pr
            type: open_pull_request
            label: Open Pull Request
          - id: record
            type: scribe
            label: Record
          - id: done
            type: terminal
            label: Done
          - id: declined
            type: terminal
            label: Declined
        edges:
          - from: implement
            to: review
          - from: review
            to: merge
            when: approved
          - from: review
            to: implement
            when: request-changes
          - from: review
            to: declined
            when: declined
          - from: merge
            to: open-pr
            when: merged
          - from: merge
            to: review
            when: blocked
          - from: open-pr
            to: record
          - from: record
            to: done
        """;

        var result = WorkflowDefinitionLoader.Load(yaml, "test");

        result.IsValid.Should().BeTrue(result.Error);
        RunWorkflowGraphBinder.GetBindabilityErrors(result.Definition!).Should().BeEmpty();
    }

    [Fact]
    public void RenderTemplate_SubstitutesAllPlaceholders()
    {
        var output = new AgentTurnOutput(
            RunId: "run-123456",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 2,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-123",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false);

        var rendered = OpenPullRequestTurnExecutor.RenderTemplate(
            "{run_id} / {worktree_branch} / {originating_branch} / {outcome_summary}", output);

        rendered.Should().Be(
            "run-123456 / agentweaver/integration/run-123 / main / 2 steps produced changes on `agentweaver/integration/run-123`.");
    }

    [Theory]
    [InlineData("owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "owner", "repo")]
    public void TryParseOwnerRepo_HandlesAllSupportedForms(string repository, string expectedOwner, string expectedRepo)
    {
        var parsed = OpenPullRequestTurnExecutor.TryParseOwnerRepo(repository, out var owner, out var repo);

        parsed.Should().BeTrue();
        owner.Should().Be(expectedOwner);
        repo.Should().Be(expectedRepo);
    }

    [Fact]
    public void TryParseOwnerRepo_RejectsUnparseableInput()
    {
        var parsed = OpenPullRequestTurnExecutor.TryParseOwnerRepo("not-a-repo", out _, out _);

        parsed.Should().BeFalse();
    }

    [Fact]
    public async Task Executor_OpensPullRequest_AndEmitsCompletedEvent()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(42, "https://github.com/acme/widgets/pull/42"));
        var projectStore = new SingleProjectStore(MakeProject("acme/widgets", "main"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance,
            projectStore: projectStore);

        var input = new AgentTurnOutput(
            RunId: "run-1-workflow",
            TreeHash: "tree",
            Diff: "diff --git a/file.txt b/file.txt",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-1-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: projectStore.Project.Id.ToString());

        var output = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        output.Should().BeSameAs(input);
        prClient.LastOwner.Should().Be("acme");
        prClient.LastRepo.Should().Be("widgets");
        prClient.LastHead.Should().Be("agentweaver/integration/run-1-workflow");
        prClient.LastBase.Should().Be("main");
        prClient.LastToken.Should().Be("token-123");
    }

    [Fact]
    public async Task Executor_UsesOverridesForTitleBodyBaseHeadDraft()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        var projectStore = new SingleProjectStore(MakeProject("acme/widgets", "main"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance,
            projectStore: projectStore,
            title: "Custom title {run_id}",
            body: "Custom body",
            baseBranch: "release",
            headBranch: "custom-head",
            draft: true);

        var input = new AgentTurnOutput(
            RunId: "run-2-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-2-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: projectStore.Project.Id.ToString());

        await executor.HandleAsync(input, context: null!, CancellationToken.None);

        prClient.LastTitle.Should().Be("Custom title run-2-workflow");
        prClient.LastBody.Should().Be("Custom body");
        prClient.LastBase.Should().Be("release");
        prClient.LastHead.Should().Be("custom-head");
        prClient.LastDraft.Should().BeTrue();
    }

    [Fact]
    public async Task Executor_ReturnsInputUnchanged_WhenNoHeadBranchAvailable()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance);

        var input = new AgentTurnOutput(
            RunId: "run-3-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: string.Empty,
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false);

        var output = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        output.Should().BeSameAs(input);
        prClient.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Executor_ReturnsInputUnchanged_WhenNoConnectedRepository()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance,
            projectStore: null);

        var input = new AgentTurnOutput(
            RunId: "run-4-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-4-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false);

        var output = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        output.Should().BeSameAs(input);
        prClient.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Executor_ReturnsInputUnchanged_WhenNoAccessToken()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        var projectStore = new SingleProjectStore(MakeProject("acme/widgets", "main"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider(null),
            NullLoggerFactory.Instance,
            projectStore: projectStore);

        var input = new AgentTurnOutput(
            RunId: "run-5-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-5-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: projectStore.Project.Id.ToString());

        var output = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        output.Should().BeSameAs(input);
        prClient.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Executor_ReturnsInputUnchanged_WhenGitHubApiFails()
    {
        var prClient = new RecordingPullRequestClient(
            GitHubPullRequestResult.Failed("pull-request-already-exists", "A pull request already exists for this branch."));
        var projectStore = new SingleProjectStore(MakeProject("acme/widgets", "main"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance,
            projectStore: projectStore);

        var input = new AgentTurnOutput(
            RunId: "run-6-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-6-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: projectStore.Project.Id.ToString());

        var output = await executor.HandleAsync(input, context: null!, CancellationToken.None);

        output.Should().BeSameAs(input);
        prClient.WasCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Executor_FallsBackToProjectDefaultBranch_WhenNoBaseOverride()
    {
        var prClient = new RecordingPullRequestClient(GitHubPullRequestResult.Ok(1, "https://github.com/acme/widgets/pull/1"));
        var projectStore = new SingleProjectStore(MakeProject("acme/widgets", "develop"));
        var executor = new OpenPullRequestTurnExecutor(
            prClient,
            new FixedGitHubRepositoryCapabilityCredentialProvider("token-123"),
            NullLoggerFactory.Instance,
            projectStore: projectStore);

        var input = new AgentTurnOutput(
            RunId: "run-7-workflow",
            TreeHash: "tree",
            Diff: "diff",
            StepCount: 1,
            WorktreePath: AppContext.BaseDirectory,
            WorktreeBranch: "agentweaver/integration/run-7-workflow",
            RepositoryPath: AppContext.BaseDirectory,
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            ProjectId: projectStore.Project.Id.ToString());

        await executor.HandleAsync(input, context: null!, CancellationToken.None);

        prClient.LastBase.Should().Be("develop");
    }

    private static Project MakeProject(string sourceRepository, string defaultBranch) => new()
    {
        Id = ProjectId.New(),
        Name = "Widgets",
        Origin = ProjectOrigin.FromGitHub(sourceRepository),
        WorkingDirectory = AppContext.BaseDirectory,
        DefaultBranch = defaultBranch,
        Owner = "owner",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State = ProjectState.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingPullRequestClient(GitHubPullRequestResult result) : IGitHubPullRequestClient
    {
        public bool WasCalled { get; private set; }
        public string? LastOwner { get; private set; }
        public string? LastRepo { get; private set; }
        public string? LastTitle { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastBase { get; private set; }
        public string? LastHead { get; private set; }
        public bool LastDraft { get; private set; }
        public string? LastToken { get; private set; }

        public Task<GitHubPullRequestResult> CreatePullRequestAsync(
            string owner, string repo, string title, string? body, string baseBranch, string headBranch,
            bool draft, string accessToken, CancellationToken ct = default)
        {
            WasCalled = true;
            LastOwner = owner;
            LastRepo = repo;
            LastTitle = title;
            LastBody = body;
            LastBase = baseBranch;
            LastHead = headBranch;
            LastDraft = draft;
            LastToken = accessToken;
            return Task.FromResult(result);
        }

        public Task<GitHubPullRequestResult?> FindOpenPullRequestAsync(
            string owner,
            string repo,
            string baseBranch,
            string headBranch,
            string accessToken,
            CancellationToken ct = default) =>
            Task.FromResult<GitHubPullRequestResult?>(null);
    }

    private sealed class FixedGitHubRepositoryCapabilityCredentialProvider(string? token)
        : IGitHubRepositoryCapabilityCredentialProvider
    {
        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(
            string runId,
            CancellationToken ct = default) =>
            Task.FromResult<GitHubCapabilitySnapshotCredential?>(
                string.IsNullOrWhiteSpace(token)
                    ? null
                    : new("snapshot-test", token, DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    private sealed class SingleProjectStore(Project project) : IProjectStore
    {
        public Project Project => project;

        public Task InsertAsync(Project project, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult(id == project.Id ? project : null);
        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateGenerationModelSettingsAsync(ProjectId id, string? blueprintGenerationModel, string? workflowGenerationModel, string? outcomeSpecGenerationModel, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryBeginDeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(ProjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePickupSettingsAsync(ProjectId id, int maxReadyPerHeartbeat, bool autopilot, bool autoApproveTools, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateDefaultWorkflowAsync(ProjectId id, string? workflowId, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateActiveReviewPolicyAsync(ProjectId id, string? policyName, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSandboxProfileAsync(ProjectId id, string? sandboxProfile, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
