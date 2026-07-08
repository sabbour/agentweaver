using FluentAssertions;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.ConsoleFacade;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Security;
using Agentweaver.Domain;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Console;

public sealed class ConsoleFacadeAgentTests
{
    [Fact]
    public void SystemPrompt_UsesAgentweaverAgentDefinition_AndPinsConsoleGuardrails()
    {
        var prompt = CopilotConsoleFacadeAgent.BuildSystemPromptForTests(
            """
            # Agentweaver Driver
            You drive it exclusively through the `agentweaver-*` MCP tools.
            """,
            projectId: "project-1",
            runId: "run-1",
            route: "/console");

        prompt.Should().Contain("Agentweaver Driver");
        prompt.Should().Contain("project-1");
        prompt.Should().Contain("run-1");
        prompt.Should().Contain("project_list");
        prompt.Should().Contain("coordinator_work_plan_get");
        prompt.Should().Contain("read-only");
        prompt.Should().Contain("do NOT claim you executed it");
        prompt.Should().Contain("not a generic chat conversation");
    }

    [Fact]
    public async Task ConsoleTurnService_UsesProjectModel_NotGenerationDefault()
    {
        var project = new Project
        {
            Id = ProjectId.New(),
            Name = "Console Test",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = "C:\\repo",
            DefaultBranch = "main",
            Owner = "octocat",
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
                GitHubCopilotModel = "claude-sonnet-4.6",
            },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var facade = new CapturingConsoleFacadeAgent();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Generation:Model"] = "gpt-5.4",
            })
            .Build();
        var service = new ConsoleTurnService(
            new SingleProjectStore(project),
            new EmptyRunStore(),
            steering: null!,
            facade,
            new ConsoleConversationStore(),
            config);

        await service.HandleAsync(
            new ConsoleTurnRequest
            {
                Text = "show status",
                ProjectId = project.Id.ToString(),
                ConversationId = "conversation-1",
            },
            new CallerContext { User = "octocat" },
            authorizationHeader: "Bearer test",
            CancellationToken.None);

        facade.LastRequest.Should().NotBeNull();
        facade.LastRequest!.ModelId.Should().Be("claude-sonnet-4.6");
        facade.LastRequest.ModelId.Should().NotBe("gpt-5.4");
    }

    private sealed class CapturingConsoleFacadeAgent : IConsoleFacadeAgent
    {
        public ConsoleFacadeAgentRequest? LastRequest { get; private set; }

        public Task<ConsoleFacadeAgentResponse> RunTurnAsync(
            ConsoleFacadeAgentRequest request,
            CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new ConsoleFacadeAgentResponse("ok", []));
        }
    }

    private sealed class SingleProjectStore(Project project) : IProjectStore
    {
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
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class EmptyRunStore : IRunStore
    {
        public Task InsertAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetAsync(RunId runId, CancellationToken ct = default) => Task.FromResult<Run?>(null);
        public Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewToInProgressAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryTransitionReviewAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? reviewer = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryTransitionToCommittingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryRevertCommittingAsync(RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> TryStartMergingAsync(RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> RevertMergingAsync(RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) => throw new NotImplementedException();
        public Task<bool> CompleteMergingAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null) => throw new NotImplementedException();
        public Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> SetAssembleReadyAsync(RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount, DateTimeOffset endedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TrySetTerminalStatusAsync(RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateToInProgressAsync(RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteAsync(RunId runId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetSandboxInfoAsync(RunId runId, string? backend, string? claimName, string? podName, string? @namespace, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, bool includeChildren = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
