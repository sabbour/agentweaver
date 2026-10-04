using Microsoft.Extensions.Logging;
using Agentweaver.Api.Git;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Projects;

/// <summary>
/// Application service for project lifecycle: create, rename, configure, delete.
/// All create paths wrap in a CreationScope for rollback compensation (plan section 3.4 E).
/// Delete uses TryBeginDeleteAsync for race-safe Active->Deleting CAS before cancel sweep.
/// </summary>
public sealed class ProjectService
{
    private readonly IProjectStore _store;
    private readonly IProjectWorkspaceProvider _workspace;
    private readonly ProjectGitInitializer _gitInit;
    private readonly GitHubConnectionsPersistenceStore _githubConnections;
    private readonly ILogger<ProjectService> _logger;
    private readonly Uri _repoAppBaseOrigin;

    public ProjectService(
        IProjectStore store,
        IProjectWorkspaceProvider workspace,
        ProjectGitInitializer gitInit,
        GitHubConnectionsPersistenceStore githubConnections,
        IConfiguration configuration,
        ILogger<ProjectService> logger)
    {
        _store = store;
        _workspace = workspace;
        _gitInit = gitInit;
        _githubConnections = githubConnections;
        _repoAppBaseOrigin = new Uri(
            (configuration["Auth:RepoApp:BaseUrl"] ?? "https://github.com").TrimEnd('/'),
            UriKind.Absolute);
        _logger = logger;
    }

    // -----------------------------------------------------------------------
    // Creation
    // -----------------------------------------------------------------------

    public async Task<Project> CreateBlankAsync(
        string name,
        string requestedPath,
        string? defaultProvider,
        string? defaultModelCopilot,
        string? defaultModelFoundry,
        string owner,
        CancellationToken ct = default)
    {
        ValidateName(name);

        var id = ProjectId.New();
        var workingDir = await _workspace.ResolveWorkingDirectoryAsync(id, requestedPath, ct)
            .ConfigureAwait(false);
        EnsureEmptyOrCreatable(workingDir);

        var providerSettings = BuildProviderSettings(defaultProvider, defaultModelCopilot, defaultModelFoundry);
        const string DefaultBranchName = "main";

        bool appCreatedDir = !Directory.Exists(workingDir);
        await _workspace.EnsureWorkspaceAsync(id, workingDir, ct).ConfigureAwait(false);

        string actualBranch;
        bool gitCreated = false;
        try
        {
            actualBranch = _gitInit.InitBlank(workingDir, DefaultBranchName);
            gitCreated = true;
        }
        catch
        {
            TryDeleteDirectory(workingDir);
            throw;
        }

        // Materialize the default workflow into the new project so .agentweaver/workflows/ exists and is
        // visible/editable in the Workspace. The WorkflowRegistry
        // treats this on-disk 'default' as the built-in copy, so it introduces no reserved-id conflict.
        TryMaterializeDefaultWorkflow(workingDir);

        // Materialize the GitHub Copilot agent definition into the new project (agent-file-gen). Best-effort
        // and non-clobbering: the embedded template is a generated copy of .github/agents/agentweaver.agent.md.
        TryMaterializeAgentDefinition(workingDir);

        // Commit any scaffold files written above so the base-branch git tree reflects the starting
        // state. Best-effort: a failure here is logged but never fails project creation.
        _gitInit.CommitAllUntracked(workingDir, "Add scaffold files");

        var project = new Project
        {
            Id = id,
            Name = name,
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDir,
            DefaultBranch = actualBranch,
            Owner = owner,
            ProviderSettings = providerSettings,
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _store.InsertAsync(project, ct).ConfigureAwait(false);
        }
        catch
        {
            // Post-FS / DB failure: compensate only what we created
            if (gitCreated) TryDeleteDirectory(workingDir);
            else if (appCreatedDir) TryDeleteDirectory(workingDir);
            throw;
        }

        return project;
    }

    public async Task<Project> CreateFromGitHubAsync(
        ProjectId id,
        string name,
        string sourceRepository,
        string cloneUrl,
        string requestedPath,
        string? defaultProvider,
        string? defaultModelCopilot,
        string? defaultModelFoundry,
        string owner,
        string accessToken,
        Func<Project, CancellationToken, Task>? onReserved = null,
        Func<Project, CancellationToken, Task>? onPrepared = null,
        Func<Project, CancellationToken, Task>? onCreationFailed = null,
        CancellationToken ct = default)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(sourceRepository))
            throw new ArgumentException("Source repository must not be empty.", nameof(sourceRepository));
        if (string.IsNullOrWhiteSpace(cloneUrl))
            throw new ArgumentException("Clone URL must not be empty.", nameof(cloneUrl));
        ValidateGitHubHttpsUrl(cloneUrl);

        var workingDir = await _workspace.ResolveWorkingDirectoryAsync(id, requestedPath, ct)
            .ConfigureAwait(false);
        EnsureEmptyOrCreatable(workingDir);

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException(
                "A live Repo App authorization is required to create a project from a GitHub repository.");

        var providerSettings = BuildProviderSettings(defaultProvider, defaultModelCopilot, defaultModelFoundry);

        var now = DateTimeOffset.UtcNow;
        var project = new Project
        {
            Id = id,
            Name = name,
            Origin = ProjectOrigin.FromGitHub(sourceRepository),
            WorkingDirectory = workingDir,
            DefaultBranch = "main",
            Owner = owner,
            ProviderSettings = providerSettings,
            State = ProjectState.Creating,
            CreatedAt = now,
            UpdatedAt = now
        };

        _logger.LogInformation(
            "GitHub project creation phase {Phase} for project {ProjectId} and repository {Repository}",
            "reserve", id, sourceRepository);
        ct.ThrowIfCancellationRequested();
        try
        {
            await _store.InsertAsync(project, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            var existing = await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "GitHub project creation reservation already exists for project {ProjectId} in state {State}",
                    id,
                    existing.State);
                return existing;
            }

            throw;
        }

        var phase = "authorization";
        try
        {
            if (onReserved is not null)
                await onReserved(project, CancellationToken.None).ConfigureAwait(false);

            phase = "workspace";
            await _workspace.EnsureWorkspaceAsync(id, workingDir, CancellationToken.None).ConfigureAwait(false);
            phase = "clone";
            var defaultBranch = _gitInit.Clone(
                workingDir,
                cloneUrl,
                accessToken,
                GitClonePurpose.ProjectCreation);
            phase = "scaffold";
            TryMaterializeDefaultWorkflow(workingDir);
            TryMaterializeAgentDefinition(workingDir);
            _gitInit.CommitAllUntracked(workingDir, "Add scaffold files");

            phase = "authorization";
            if (onPrepared is not null)
                await onPrepared(project, CancellationToken.None).ConfigureAwait(false);

            phase = "activate";
            var completedAt = DateTimeOffset.UtcNow;
            await _store.UpdateCreationStateAsync(
                id, ProjectState.Active, defaultBranch, completedAt, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation(
                "GitHub project creation phase {Phase} completed for project {ProjectId} and repository {Repository}",
                phase, id, sourceRepository);
            return project with
            {
                DefaultBranch = defaultBranch,
                State = ProjectState.Active,
                UpdatedAt = completedAt,
            };
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(workingDir);
            if (onCreationFailed is not null)
            {
                try
                {
                    await onCreationFailed(project, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogError(
                        cleanupEx,
                        "Failed to revoke GitHub project authorization after creation failure for project {ProjectId}",
                        id);
                }
            }
            var failedAt = DateTimeOffset.UtcNow;
            try
            {
                await _store.UpdateCreationStateAsync(
                    id, ProjectState.Failed, project.DefaultBranch, failedAt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception stateEx)
            {
                _logger.LogError(
                    stateEx,
                    "Failed to persist GitHub project creation failure for project {ProjectId} in phase {Phase}",
                    id,
                    phase);
            }

            _logger.LogError(
                ex,
                "GitHub project creation failed for project {ProjectId} and repository {Repository} in phase {Phase}",
                id,
                sourceRepository,
                phase);
            throw new ProjectCreationFailedException(id, phase, ex);
        }
    }

    public async Task<Project> ResumePreparedGitHubCreationAsync(
        Project project,
        CancellationToken ct = default)
    {
        if (project.State != ProjectState.Creating ||
            project.Origin.Kind != ProjectOriginKind.FromGitHub ||
            !_workspace.IsAvailable(project.WorkingDirectory))
            return project;

        var defaultBranch = _gitInit.GetCurrentBranch(project.WorkingDirectory);
        var completedAt = DateTimeOffset.UtcNow;
        await _store.UpdateCreationStateAsync(
            project.Id,
            ProjectState.Active,
            defaultBranch,
            completedAt,
            ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Recovered prepared GitHub project creation for project {ProjectId}.",
            project.Id);
        return project with
        {
            DefaultBranch = defaultBranch,
            State = ProjectState.Active,
            UpdatedAt = completedAt,
        };
    }

    public async Task<Project> ConnectCreatedRepositoryAsync(
        ProjectId id,
        string fullName,
        string cloneUrl,
        string accessToken,
        CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project '{id}' was not found.");
        if (project.Origin.Kind != ProjectOriginKind.Blank)
            throw new InvalidOperationException("This project already has a connected repository.");

        _gitInit.PushToNewRemote(project.WorkingDirectory, cloneUrl, project.DefaultBranch, accessToken);
        var now = DateTimeOffset.UtcNow;
        var origin = ProjectOrigin.FromGitHub(fullName);
        await _githubConnections.CompleteRepositoryAttachmentAsync(
            id.ToString(),
            token => _store.UpdateOriginAsync(id, origin, now, token),
            ct).ConfigureAwait(false);
        return project with { Origin = origin, UpdatedAt = now };
    }

    // -----------------------------------------------------------------------
    // Updates
    // -----------------------------------------------------------------------

    public async Task<bool> RenameAsync(ProjectId id, string name, CancellationToken ct = default)
    {
        ValidateName(name);
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;
        await _store.UpdateNameAsync(id, name, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateProviderSettingsAsync(
        ProjectId id, string? defaultProvider, string? defaultModelCopilot,
        string? defaultModelFoundry,
        string? blueprintGenerationModel = null,
        string? workflowGenerationModel = null,
        string? outcomeSpecGenerationModel = null,
        CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;
        var settings = BuildProviderSettings(defaultProvider, defaultModelCopilot, defaultModelFoundry);
        var now = DateTimeOffset.UtcNow;
        await _store.UpdateProviderSettingsAsync(id, settings, now, ct).ConfigureAwait(false);
        await _store.UpdateGenerationModelSettingsAsync(
            id,
            NormalizeModelId(blueprintGenerationModel, nameof(blueprintGenerationModel)),
            NormalizeModelId(workflowGenerationModel, nameof(workflowGenerationModel)),
            NormalizeModelId(outcomeSpecGenerationModel, nameof(outcomeSpecGenerationModel)),
            now,
            ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdatePreviewSettingsAsync(
        ProjectId id, int approvalTimeoutMinutes, int lifetimeMinutes, int dnsConvergenceTimeoutSeconds, CancellationToken ct = default)
    {
        if (approvalTimeoutMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(approvalTimeoutMinutes),
                "Preview approval timeout must be between 1 and 1440 minutes.");
        if (lifetimeMinutes is < 1 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(lifetimeMinutes),
                "Preview lifetime must be between 1 and 1440 minutes.");
        if (dnsConvergenceTimeoutSeconds is < 60 or > 3600)
            throw new ArgumentOutOfRangeException(nameof(dnsConvergenceTimeoutSeconds),
                "Preview infrastructure convergence timeout must be between 60 and 3600 seconds.");

        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;
        await _store.UpdatePreviewSettingsAsync(
            id, approvalTimeoutMinutes, lifetimeMinutes, dnsConvergenceTimeoutSeconds, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return true;
    }

    // -----------------------------------------------------------------------
    // Race-safe delete
    // -----------------------------------------------------------------------

    /// <summary>
    /// Confirm-gated, race-safe project deletion (FR-019):
    /// 1. CAS Active -> Deleting via TryBeginDeleteAsync
    /// 2. Enumerate non-terminal runs, Abandon + force-terminal each
    /// 3. ReleaseAsync workspace
    /// 4. Delete the project record (files are preserved)
    /// </summary>
    public async Task<bool> DeleteAsync(
        ProjectId id,
        IRunStore runStore,
        RunWorkflowRegistry workflowRegistry,
        CancellationToken ct = default)
    {
        // Gate: flip Active -> Deleting; rejects if already Deleting or missing
        bool gateAcquired = await _store.TryBeginDeleteAsync(id, ct).ConfigureAwait(false);
        if (!gateAcquired) return false;

        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;

        // Cancel in-flight runs (all non-terminal statuses)
        var nonTerminalStatuses = new[]
        {
            RunStatus.Pending, RunStatus.InProgress, RunStatus.AwaitingReview,
            RunStatus.Committing, RunStatus.Merging
        };
        var activeRuns = await runStore.GetRunsByProjectAndStatusesAsync(id, nonTerminalStatuses, ct)
            .ConfigureAwait(false);

        foreach (var run in activeRuns)
        {
            workflowRegistry.Abandon(run.Id.ToString());
            await runStore.TrySetTerminalOutcomeAsync(
                run.Id,
                TerminalRunOutcome.Create(RunStatus.Failed, EventTypes.RunFailed, new { reason = "cancelled: project deleted" }, DateTimeOffset.UtcNow, run.LifecycleGeneration),
                "cancelled: project deleted",
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Cancelled run {RunId} for deleted project {ProjectId}", run.Id, id);
        }

        // Release workspace (no-op locally; detaches mount in cloud)
        await _workspace.ReleaseAsync(id, project.WorkingDirectory, ct).ConfigureAwait(false);

        // Record-only delete — files preserved
        await _store.DeleteAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    public async Task RollbackCreationAsync(
        ProjectId id,
        IRunStore runStore,
        RunWorkflowRegistry workflowRegistry,
        CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (project is null) return;

        try
        {
            // The project-row delete is the authoritative database boundary: skill state is
            // removed by cascading foreign keys in the same transaction/statement.
            await DeleteAsync(id, runStore, workflowRegistry, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project record while rolling back project {ProjectId}", id);
            throw;
        }
        finally
        {
            TryDeleteDirectory(project.WorkingDirectory);
        }
    }

    // -----------------------------------------------------------------------
    // Read helpers
    // -----------------------------------------------------------------------

    public async Task<ProjectView?> GetViewAsync(ProjectId id, CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return null;
        return ToView(project);
    }

    public async Task<IReadOnlyList<ProjectView>> ListViewsAsync(CancellationToken ct = default)
    {
        var projects = await _store.ListAsync(ct).ConfigureAwait(false);
        return projects.Select(ToView).ToList();
    }

    private ProjectView ToView(Project p) => new()
    {
        Project = p,
        Available = p.State == ProjectState.Active && _workspace.IsAvailable(p.WorkingDirectory)
    };

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name must not be empty.", nameof(name));
    }

    private static void EnsureEmptyOrCreatable(string path)
    {
        if (!Directory.Exists(path)) return;  // will be created — OK
        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException(
                $"Working directory '{path}' already exists and is not empty. " +
                "Choose an empty or non-existent directory.");
    }

    private static ProjectProviderSettings BuildProviderSettings(
        string? defaultProvider, string? defaultModelCopilot, string? defaultModelFoundry)
    {
        ValidateModelId(defaultModelCopilot, nameof(defaultModelCopilot));
        ValidateModelId(defaultModelFoundry, nameof(defaultModelFoundry));

        var provider = string.IsNullOrWhiteSpace(defaultProvider)
            ? ModelSource.GitHubCopilot
            : ModelSourceExtensions.FromApiString(defaultProvider);
        return new ProjectProviderSettings
        {
            DefaultProvider = provider,
            GitHubCopilotModel = string.IsNullOrWhiteSpace(defaultModelCopilot) ? null : defaultModelCopilot,
            MicrosoftFoundryModel = string.IsNullOrWhiteSpace(defaultModelFoundry) ? null : defaultModelFoundry
        };
    }

    private void ValidateGitHubHttpsUrl(string sourceRepository)
    {
        if (!Uri.TryCreate(sourceRepository, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.IdnHost, _repoAppBaseOrigin.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != _repoAppBaseOrigin.Port ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "source_repository must be an HTTPS URL on the configured GitHub origin.",
                nameof(sourceRepository));
        }
    }

    public sealed class ProjectCreationFailedException(ProjectId projectId, string phase, Exception innerException)
        : Exception($"Project creation failed during {phase}.", innerException)
    {
        public ProjectId ProjectId { get; } = projectId;
        public string Phase { get; } = phase;
    }

    private static void ValidateModelId(string? modelId, string paramName)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var value = modelId.Trim();
        if (!(value.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
              value.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ||
              value.StartsWith("o", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Model id '{modelId}' is not allowed.", paramName);
    }

    private static string? NormalizeModelId(string? modelId, string paramName)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        ValidateModelId(modelId, paramName);
        return modelId.Trim();
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up directory {Path} during rollback", path);
        }
    }

    /// <summary>
    /// Best-effort materialization of the default workflow into the project's working directory at
    /// <c>.agentweaver/workflows/default.yaml</c> so the directory exists and is visible/editable in the
    /// Workspace. Non-clobbering and never
    /// throws: project creation must not fail if this write fails, because the workflow registry
    /// regenerates the default from <see cref="Workflows.DefaultWorkflowTemplate"/> at runtime and treats
    /// this on-disk copy as the built-in default (no reserved-id conflict).
    /// </summary>
    private void TryMaterializeDefaultWorkflow(string workingDir)
    {
        var written = Workflows.DefaultWorkflowTemplate.TryMaterialize(workingDir, out var error);
        if (error is not null)
            _logger.LogWarning(
                "Failed to materialize the default workflow into {Path} ({Error}); the runtime default will be used instead.",
                Path.Combine(workingDir, Workflows.DefaultWorkflowTemplate.RelativeFilePath), error);
        else if (written)
            _logger.LogInformation(
                "Materialized the default workflow into {Path}.",
                Path.Combine(workingDir, Workflows.DefaultWorkflowTemplate.RelativeFilePath));
    }

    /// <summary>
    /// Best-effort materialization of the GitHub Copilot agent definition into the project's working
    /// directory at <c>.github/agents/agentweaver.agent.md</c> (agent-file-gen). Non-clobbering and never
    /// throws: project creation must not fail if this write fails. The embedded template is a generated,
    /// byte-identical copy of the repo's <c>.github/agents/agentweaver.agent.md</c>, whose "## Tool map"
    /// block is derived from the MCP server source via <c>scripts/gen-docs.mjs</c>.
    /// </summary>
    private void TryMaterializeAgentDefinition(string workingDir)
    {
        var written = AgentDefinitionTemplate.TryMaterialize(workingDir, out var error);
        if (error is not null)
            _logger.LogWarning(
                "Failed to materialize the agent definition into {Path} ({Error}).",
                Path.Combine(workingDir, AgentDefinitionTemplate.RelativeFilePath), error);
        else if (written)
            _logger.LogInformation(
                "Materialized the agent definition into {Path}.",
                Path.Combine(workingDir, AgentDefinitionTemplate.RelativeFilePath));
    }
}
