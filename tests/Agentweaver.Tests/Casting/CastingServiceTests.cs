using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Casting;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Squad.Analysis;
using Agentweaver.Squad.Catalog;
using Agentweaver.Squad.Model;

namespace Agentweaver.Tests.Casting;

public sealed class CastingServiceTests : IDisposable
{
    private readonly string _root;

    public CastingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"casting-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData("Coordinator")]
    [InlineData("Work Monitor")]
    [InlineData("RAI Reviewer")]
    public async Task ProposeManualCastAsync_WithReservedBespokeTitle_IsRejected(string reservedTitle)
    {
        var workingDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        var projectId = ProjectId.New();
        var project = new Project
        {
            Id = projectId,
            Name = "casting-service-test",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = "test-user",
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var proposals = new RecordingProposalStore();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = new CastingService(
            new SingleProjectStore(project),
            new CatalogReader(),
            proposals,
            new UnusedAgentRunner(),
            new ProjectSignalScanner(),
            NullLogger<CastingService>.Instance,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            new EmptyRunStore());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.ProposeManualCastAsync(
            projectId.ToString(),
            ["backend-engineer", "custom-role"],
            universeOverride: "Inception",
            ct: CancellationToken.None,
            bespokeRoles: new Dictionary<string, BespokeRole>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom-role"] = new("custom-role", reservedTitle, "Custom charter.")
            }));

        Assert.Equal("roleIds", ex.ParamName);
        Assert.Contains(reservedTitle, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reserved", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(proposals.StoredProposal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
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
        public Task UpdateOriginAsync(ProjectId id, ProjectOrigin origin, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateSourceBlueprintAsync(ProjectId id, string? blueprintId, string? blueprintType, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateAllowedWorkflowIdsAsync(ProjectId id, IReadOnlyList<string>? allowedWorkflowIds, DateTimeOffset updatedAt, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IProjectTeamMutationLease?> TryBeginTeamMutationAsync(ProjectId id, long expectedRevision, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class RecordingProposalStore : ICastProposalStore
    {
        public CastProposal? StoredProposal { get; private set; }

        public void Store(string projectId, CastProposal proposal, string owner) => StoredProposal = proposal;
        public (CastProposal? Proposal, string? Owner) Get(string projectId, string proposalId) => (null, null);
        public bool Remove(string projectId, string proposalId) => false;
        public CastProposal? GetByProject(string projectId) => null;
        public IReadOnlyList<(CastProposal Proposal, string Owner, DateTimeOffset ExpiresAt)> ListByProject(string projectId) => [];
    }

    private sealed class UnusedAgentRunner : IAgentRunner
    {
        public Task<string> ExecuteAsync(string task, string workingDirectory, string repositoryPath, ModelSource modelSource, string runId, string? modelId, System.Threading.Channels.ChannelWriter<RunEvent>? stream, CancellationToken ct, string? systemPromptContext = null, string? userId = null) =>
            throw new NotImplementedException();
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
