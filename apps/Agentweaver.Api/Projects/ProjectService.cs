using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;

namespace Agentweaver.Api.Projects;

/// <summary>
/// Application service for project lifecycle: create, rename, configure, relink, delete.
/// All create paths wrap in a CreationScope for rollback compensation (plan section 3.4 E).
/// Delete uses TryBeginDeleteAsync for race-safe Active->Deleting CAS before cancel sweep.
/// </summary>
public sealed class ProjectService
{
    private readonly IProjectStore _store;
    private readonly IProjectWorkspaceProvider _workspace;
    private readonly ProjectGitInitializer _gitInit;
    private readonly IGitHubTokenStore _tokenStore;
    private readonly IGitHubTokenScopeProvider _scopeProvider;
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(
        IProjectStore store,
        IProjectWorkspaceProvider workspace,
        ProjectGitInitializer gitInit,
        IGitHubTokenStore tokenStore,
        IGitHubTokenScopeProvider scopeProvider,
        ILogger<ProjectService> logger,
        IGitHubAccessTokenProvider? accessTokenProvider = null)
    {
        _store = store;
        _workspace = workspace;
        _gitInit = gitInit;
        _tokenStore = tokenStore;
        _scopeProvider = scopeProvider;
        _accessTokenProvider = accessTokenProvider;
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

        // Materialize the default review policy into the new project (Feature 010). Best-effort: never fail
        // creation if this write fails — the loader regenerates the default from DefaultReviewPolicyTemplate
        // at runtime. A blank project is empty, so this always writes the default policy.
        TryMaterializeDefaultReviewPolicy(workingDir);

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
        string name,
        string sourceRepository,
        string requestedPath,
        string? defaultProvider,
        string? defaultModelCopilot,
        string? defaultModelFoundry,
        string owner,
        CancellationToken ct = default)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(sourceRepository))
            throw new ArgumentException("Source repository must not be empty.", nameof(sourceRepository));
        ValidateGitHubHttpsUrl(sourceRepository);

        var id = ProjectId.New();
        var workingDir = await _workspace.ResolveWorkingDirectoryAsync(id, requestedPath, ct)
            .ConfigureAwait(false);
        EnsureEmptyOrCreatable(workingDir);

        // Resolve a valid GitHub access token (refresh-aware; fail closed if signed out)
        var scope = _scopeProvider.Resolve(owner);
        string? accessToken;
        if (_accessTokenProvider is not null)
        {
            accessToken = await _accessTokenProvider.GetValidAccessTokenAsync(scope, ct).ConfigureAwait(false);
        }
        else
        {
            var tokenEntry = await _tokenStore.GetAsync(scope, ct).ConfigureAwait(false);
            accessToken = tokenEntry.Status == GitHubTokenStatus.SignedIn ? tokenEntry.AccessToken : null;
        }
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException(
                "GitHub sign-in is required to create a project from a GitHub repository. Sign in with 'agentweaver github sign-in'.");

        var providerSettings = BuildProviderSettings(defaultProvider, defaultModelCopilot, defaultModelFoundry);

        bool appCreatedDir = !Directory.Exists(workingDir);
        await _workspace.EnsureWorkspaceAsync(id, workingDir, ct).ConfigureAwait(false);

        string defaultBranch;
        bool dirWasCreated = appCreatedDir;
        try
        {
            defaultBranch = _gitInit.Clone(workingDir, sourceRepository, accessToken!);
        }
        catch
        {
            TryDeleteDirectory(workingDir);
            throw;
        }

        // Materialize the default review policy into the cloned project (Feature 010). Best-effort and
        // non-clobbering: TryMaterialize skips the write when the file already exists, so a repo that
        // ships its own review policies is never overwritten. Never fails creation; the loader
        // regenerates the default from DefaultReviewPolicyTemplate at runtime.
        TryMaterializeDefaultReviewPolicy(workingDir);

        // Materialize the GitHub Copilot agent definition into the cloned project (agent-file-gen). Best-effort
        // and non-clobbering: a repo that already ships its own .github/agents/agentweaver.agent.md is never
        // overwritten. Never fails creation.
        TryMaterializeAgentDefinition(workingDir);

        // Commit any scaffold files written above so the base-branch git tree reflects the starting
        // state. Best-effort: a failure here is logged but never fails project creation.
        _gitInit.CommitAllUntracked(workingDir, "Add scaffold files");

        var project = new Project
        {
            Id = id,
            Name = name,
            Origin = ProjectOrigin.FromGitHub(sourceRepository),
            WorkingDirectory = workingDir,
            DefaultBranch = defaultBranch,
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
            TryDeleteDirectory(workingDir);
            throw;
        }

        return project;
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
        string? defaultModelFoundry, CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;
        var settings = BuildProviderSettings(defaultProvider, defaultModelCopilot, defaultModelFoundry);
        await _store.UpdateProviderSettingsAsync(id, settings, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        return true;
    }

    // -----------------------------------------------------------------------
    // Relink
    // -----------------------------------------------------------------------

    /// <summary>
    /// Relinks a project to a moved or restored working directory. Accepts a non-empty
    /// directory (unlike creation). Validates that it is a valid git repository and,
    /// where determinable, that the origin matches the project's recorded origin.
    /// Re-derives DefaultBranch from HEAD.
    /// </summary>
    public async Task<bool> RelinkAsync(
        ProjectId id, string newPath, CancellationToken ct = default)
    {
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return false;

        var canonicalPath = Path.GetFullPath(newPath);
        if (!Directory.Exists(canonicalPath))
            throw new ArgumentException($"Directory '{canonicalPath}' does not exist.", nameof(newPath));

        // Validate it is a git repository
        if (!Repository.IsValid(canonicalPath))
            throw new InvalidOperationException(
                $"Directory '{canonicalPath}' is not a valid git repository.");

        // Validate origin matches where possible (from-GitHub projects)
        if (project.Origin.Kind == ProjectOriginKind.FromGitHub
            && !string.IsNullOrWhiteSpace(project.Origin.SourceRepository))
        {
            using var repo = new Repository(canonicalPath);
            var remote = repo.Network.Remotes["origin"];
            if (remote is not null)
            {
                var remoteUrl = remote.Url ?? string.Empty;
                var expected = project.Origin.SourceRepository!;
                // Accept both "owner/repo" and full HTTPS URL
                if (!remoteUrl.Contains(expected, StringComparison.OrdinalIgnoreCase)
                    && !expected.Contains(remoteUrl, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        $"Directory '{canonicalPath}' has remote '{remoteUrl}' which does not match " +
                        $"the project's source repository '{expected}'.");
            }
        }

        // Re-derive default branch
        string defaultBranch;
        using (var repo = new Repository(canonicalPath))
        {
            defaultBranch = repo.Head.FriendlyName;
        }

        await _store.UpdateWorkingDirectoryAsync(id, canonicalPath, defaultBranch, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
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
            await runStore.TrySetTerminalStatusAsync(
                run.Id,
                RunStatus.Failed,
                DateTimeOffset.UtcNow,
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
        var project = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (project is null) return;

        try
        {
            await DeleteAsync(id, runStore, workflowRegistry, ct).ConfigureAwait(false);
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
        Available = _workspace.IsAvailable(p.WorkingDirectory)
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
                "Use relink to associate an existing repository, or choose an empty or non-existent directory.");
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

    private static void ValidateGitHubHttpsUrl(string sourceRepository)
    {
        if (!Uri.TryCreate(sourceRepository, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !sourceRepository.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("source_repository must be an HTTPS GitHub URL starting with https://github.com/.", nameof(sourceRepository));
        }
    }

    private static void ValidateModelId(string? modelId, string paramName)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        var value = modelId.Trim();
        if (!(value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) ||
              value.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) ||
              value.StartsWith("o", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Model id '{modelId}' is not allowed.", paramName);
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
    /// Best-effort materialization of the default review policy into the project's working directory at
    /// <c>.agentweaver/review-policies/default.yaml</c> (Feature 010, FR-032). Non-clobbering and never
    /// throws: project creation must not fail if this write fails, because the review-policy registry
    /// regenerates the default from <see cref="ReviewPolicies.DefaultReviewPolicyTemplate"/> at runtime.
    /// </summary>
    private void TryMaterializeDefaultReviewPolicy(string workingDir)
    {
        var written = ReviewPolicies.DefaultReviewPolicyTemplate.TryMaterialize(workingDir, out var error);
        if (error is not null)
            _logger.LogWarning(
                "Failed to materialize the default review policy into {Path} ({Error}); the runtime default will be used instead.",
                Path.Combine(workingDir, ReviewPolicies.DefaultReviewPolicyTemplate.RelativeFilePath), error);
        else if (written)
            _logger.LogInformation(
                "Materialized the default review policy into {Path}.",
                Path.Combine(workingDir, ReviewPolicies.DefaultReviewPolicyTemplate.RelativeFilePath));
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
