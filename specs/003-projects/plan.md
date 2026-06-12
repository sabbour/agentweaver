# Implementation Plan: Projects

**Branch**: `003-projects` | **Date**: 2026-06-11T14:20:02.8823612-07:00 | **Spec**: `specs/003-projects/spec.md`

**Input**: Feature specification from `specs/003-projects/spec.md` (FR-001..FR-026, six user stories, edge cases, key entities, success criteria, and the Session 2026-06-11 clarifications) and the Scaffolder Constitution v1.2.0.

---

## 1. Summary and Approach

A Project is the top-level, persistent container that pairs a working directory with the project's AI configuration and owns the agent runs from features 001 and 002. This plan introduces a `Project` domain record, a new SQLite `projects` table with a `SqliteProjectStore` (mirroring `SqliteRunStore`), a `ProjectService` that creates projects blank (`git init`) or from a GitHub repository (`git clone`), a unified GitHub OAuth device-flow sign-in that both grants repository access and authorizes the GitHub Copilot provider, and full CLI and Web parity over a new `/api/projects` surface. Project identity lives in SQLite, NOT in the per-repo `.scaffolder/settings.yml` (which `YamlSandboxPolicyStore` reserves for run-scoped sandbox policy).

The design reuses, and does not duplicate, what already exists:

- The two-provider rule is enforced through the existing `ModelSource` enum (`github-copilot`, `microsoft-foundry`); no provider is added (Principle II, FR-013).
- The agent loop, runners (`AgentRunnerDispatcher` -> `GitHubCopilotAgentRunner` / `FoundryAgentRunner`), the workflow, and run streaming are reused; a project supplies the working directory and the provider/model defaults a run consumes (Principles I, V). The ONE additive change to the run path is a per-run model id (section 3.7): the real `Run` record and `runs` table carry only a provider (`ModelSource`), never a model id, so the stored project default model cannot reach a run today. This plan threads a `string? ModelId` end to end so FR-014/FR-015/SC-004 are actually satisfiable.
- Provider credentials remain global / installation-wide but are keyed by authenticated caller/tenant in the token store (section 3.5): identical contract local and cloud, only the backing key scope differs (single OS user locally; per-tenant key in cloud). GitHub Copilot is authorized by the GitHub sign-in token store IN PLACE OF a Copilot API key; Microsoft Foundry keeps its own `Providers:MicrosoftFoundry:*` credentials and is NEVER satisfied by the GitHub token (FR-005, FR-013, FR-016). After an explicit sign-out the system fails closed and does NOT fall back to a config token (section 3.6, section 11).

The single largest net-new surface is GitHub authentication. The codebase today has only bearer API-key auth (`ApiKeyAuthMiddleware` + `ApiKeyRegistry`); there is no OAuth/device flow anywhere. This plan builds a real device-flow sign-in service plus a secret-grade global token store (OS credential manager locally; encrypted server-side secret store in cloud), with centralized token redaction. No placeholder auth is introduced (Principle VII).

Deployment parity (Principle VI, FR-025) is satisfied by a fully specified storage seam (section 3.3): `IProjectWorkspaceProvider` with a `LocalFilesystemWorkspaceProvider` (current behavior, unchanged) and a `PersistentVolumeWorkspaceProvider` that maps each project to a managed per-project persistent volume mounted at the same path. The provider owns real, implementable application logic - deterministic path mapping, mount validation, availability detection, lifecycle ensure/release - while the actual cloud volume allocation/attachment is environment-supplied and explicitly delineated (section 3.3). Because both deployments expose the working directory at the same path, the "working directory = run sandbox boundary" model (FR-022, Principle X) is identical in both. This is a working seam, not a placeholder.

Three correctness fixes are grounded in the real run path: (1) blank projects create an initial empty commit on a default branch at creation, because `WorktreeManager.AddWorktree` requires an existing branch with a commit tip and a bare `git init` repo is unborn (section 3.4 and 3.7, B); (2) project deletion uses a transactional `deleting` gate plus an authoritative force-to-terminal sweep, because `RunWorkflowRegistry.Abandon` only cancels a token and the watch loop writes no terminal status on abandonment (section 3.4, section 4.4, C); (3) creation rollback compensates every path, deleting only app-created artifacts and never user content (section 3.4, E).

**Approach in one line:** new domain + SQLite persistence (incl. per-run `model_id`) -> `ProjectService` over `IProjectWorkspaceProvider` + LibGit2Sharp (blank create commits an empty default branch) -> caller/tenant-keyed GitHub device-flow sign-in + token store + sign-out-fail-closed Copilot authorization swap -> `/api/projects` and `/api/auth/github/*` endpoints with race-safe delete -> CLI parity (incl. project-run) -> Web parity -> docs -> early OAuth-grant spike + tests + security review.

---

## 2. Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`) for all backend, CLI, and domain code; TypeScript + React 19 (Fluent 2) for the Web UI.

**Primary Dependencies**: ASP.NET Core minimal APIs (inline in `apps/Scaffolder.Api/Program.cs`); `Microsoft.Data.Sqlite` v10.0.8 (raw SQL, no EF/Dapper); `LibGit2Sharp` v0.31.0 (already referenced) for `git init`/`git clone`; `YamlDotNet` v18.0.0 (sandbox policy only); Microsoft Agent Framework runners (reused); Spectre.Console (CLI); React 19.2 + `@fluentui/react-components` v9 + `react-router-dom` v7 (Web).

**Storage**: SQLite at `%LOCALAPPDATA%/scaffolder/scaffolder.db` via `SqliteDb` (`AppPaths.DataDirectory`). New `projects` table created in `SqliteDb.SchemaSql`; `runs.project_id` and `runs.model_id` are added via the existing `TryAlterAsync` migration pattern (mirroring the seven existing `ALTER TABLE runs ADD COLUMN` migrations in `EnsureCreatedAsync`). GitHub tokens are NEVER stored in SQLite or on the project record; they live in a dedicated secret store (OS credential manager locally; encrypted secret store in cloud), keyed by caller/tenant.

**Model selection**: GitHub Copilot per-run model is set via `SessionConfig.Model` (a `string` on the SDK's `SessionConfigBase`, confirmed against `GitHub.Copilot.SDK` 1.0.0-beta.2); available models for the unavailable-model recovery flow come from the SDK model-listing surface (`CopilotClientOptions.OnListModels` / `GetModelsResponse`). Microsoft Foundry's model is the Azure OpenAI deployment passed to `AzureOpenAIClient.GetChatClient(deployment)`; a per-run override flows through `FoundryClientFactory.CreateChatClient(string? deploymentOverride)`.

**Testing**: xUnit with `WebApplicationFactory<Program>` (new `ProjectsWebApplicationFactory`, mirroring `ScaffolderWebApplicationFactory`); Vitest + Testing Library for the Web UI. Live GitHub device-flow tests are gated behind a `GITHUB_INTEGRATION_TESTS` environment variable; the device-flow state machine is unit-tested against a configurable GitHub base URL.

**Target Platform**: Windows developer machine (primary) and hosted-cloud Linux service (same build) per Principle VI. No environment-specific code path; deployment differences are isolated behind `IProjectWorkspaceProvider` and `IGitHubTokenStore`.

**Project Type**: Web application (authoritative ASP.NET Core API) with two thin clients (CLI, Web UI). The CLI contains NO project business logic; it calls the API (Principles III, IV).

**Constraints**: Exactly two providers (Principle II); every capability API-first and reachable identically from CLI and Web (Principles III, IV); project working directory is the run sandbox boundary and must be empty or non-existent at creation (FR-003, FR-004, FR-022); delete is record-only and human-gated and must first cancel in-flight runs (FR-019, Principle X); tokens/secrets must never appear in any output, log, or telemetry (FR-005, FR-016, FR-023, Principles IX, XI); no emojis anywhere (Principle VIII).

---

## 3. Architecture and Component Design

### 3.1 Project domain record and `ProjectId`

**Location**: `packages/Scaffolder.Domain/` (new files), alongside `Run.cs`, `RunId.cs`, `ModelSource.cs`.

`ProjectId` mirrors `RunId` exactly (UUID v4 backed, unguessable, `ToString("D")`):

```csharp
namespace Scaffolder.Domain;

public readonly record struct ProjectId(Guid Value)
{
    public static ProjectId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
    public static ProjectId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out ProjectId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new ProjectId(g); return true; }
        id = default; return false;
    }
}
```

Project origin discriminates how the project was created (FR-001, key entity "Project origin"):

```csharp
public enum ProjectOriginKind { Blank, FromGitHub }

public sealed record ProjectOrigin
{
    public required ProjectOriginKind Kind { get; init; }
    /// <summary>Source repository reference for FromGitHub (e.g. "owner/name" or an HTTPS URL); null for Blank.</summary>
    public string? SourceRepository { get; init; }

    public static readonly ProjectOrigin Blank = new() { Kind = ProjectOriginKind.Blank };
    public static ProjectOrigin FromGitHub(string source) =>
        new() { Kind = ProjectOriginKind.FromGitHub, SourceRepository = source };

    public string ToApiString() => Kind == ProjectOriginKind.Blank ? "blank" : "github";
}
```

Provider settings store ONLY defaults; credentials are never here (FR-012, FR-014, FR-016). The two model fields are keyed to the two permitted `ModelSource` values:

```csharp
public sealed record ProjectProviderSettings
{
    /// <summary>The project's default provider, one of exactly two permitted values (FR-012, FR-013, Principle II).</summary>
    public required ModelSource DefaultProvider { get; init; }
    /// <summary>Default model for GitHub Copilot; null means "use the runtime default model".</summary>
    public string? GitHubCopilotModel { get; init; }
    /// <summary>Default model for Microsoft Foundry; null means "use the runtime default model".</summary>
    public string? MicrosoftFoundryModel { get; init; }
}
```

The Project record (FR-002, FR-024, key entity "Project"):

```csharp
/// <summary>
/// Lifecycle state. `Active` is the normal state. `Deleting` is set by the
/// transactional delete gate (section 3.4, FR-019) so no new run can be created
/// for the project once deletion has begun; it is a one-way transition.
/// </summary>
public enum ProjectState { Active, Deleting }

public sealed record Project
{
    public required ProjectId Id { get; init; }
    public required string Name { get; init; }                 // user-facing label; not unique (edge case "Empty or duplicate name")
    public required ProjectOrigin Origin { get; init; }
    public required string WorkingDirectory { get; init; }      // sandbox boundary for all the project's runs (FR-006, FR-022)
    public required string DefaultBranch { get; init; }         // branch a project run forks from; for blank projects this is the branch created at init (FR-003, section 3.7 B)
    public required string Owner { get; init; }                 // accountable human (FR-024); never a secret
    public required ProjectProviderSettings ProviderSettings { get; init; }
    public required ProjectState State { get; init; }           // Active | Deleting (FR-019 delete gate, section 3.4 C)
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
```

Availability (FR-026) is NOT a stored column; it is computed at read time by `IProjectWorkspaceProvider.IsAvailable(WorkingDirectory)` and surfaced as a derived `ProjectView.Available` flag (section 3.4). This keeps the persisted record truthful and avoids stale availability state. `State`, by contrast, IS persisted because it is the authoritative delete gate that the run-creation endpoint and the deletion flow read/write transactionally (section 3.4 C).

### 3.2 `projects` table schema and `SqliteProjectStore`

The schema is added to `SqliteDb.SchemaSql` (idempotent `CREATE TABLE IF NOT EXISTS`), and `runs.project_id` is added through `TryAlterAsync` in `EnsureCreatedAsync` exactly like the existing run-column migrations. Credentials have NO column anywhere (FR-016).

```sql
CREATE TABLE IF NOT EXISTS projects (
    project_id                         TEXT PRIMARY KEY,
    name                               TEXT NOT NULL,
    origin_kind                        TEXT NOT NULL,   -- 'blank' | 'github'
    origin_source                      TEXT,            -- repo ref for 'github'; NULL for 'blank'
    working_directory                  TEXT NOT NULL,
    default_branch                     TEXT NOT NULL,   -- branch a project run forks from (FR-003 blank-init branch; section 3.7 B)
    owner                              TEXT NOT NULL,
    default_provider                   TEXT NOT NULL,   -- 'github-copilot' | 'microsoft-foundry'
    default_model_github_copilot       TEXT,
    default_model_microsoft_foundry    TEXT,
    state                              TEXT NOT NULL DEFAULT 'active',  -- 'active' | 'deleting' (FR-019 delete gate)
    created_at                         TEXT NOT NULL,
    updated_at                         TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_projects_created_at ON projects(created_at);
```

`TryAlterAsync` migrations appended to `SqliteDb.EnsureCreatedAsync` (links a run to its project for the FR-022 boundary association, and carries the per-run model id for FR-014/FR-015). These mirror the seven existing `ALTER TABLE runs ADD COLUMN ...` migrations already in `EnsureCreatedAsync`:

```csharp
await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN project_id TEXT;", ct);
await TryAlterAsync(connection, "ALTER TABLE runs ADD COLUMN model_id TEXT;", ct);   // per-run model id (section 3.7 A)
await TryAlterAsync(connection, "CREATE INDEX IF NOT EXISTS ix_runs_project_id ON runs(project_id);", ct);
```

`model_id` is nullable: pre-feature runs and runs that omit a model continue to work (null = "use the provider runtime default"). The `projects` table is created via `CREATE TABLE IF NOT EXISTS` with `state DEFAULT 'active'`; on a pre-existing `projects` table the `default_branch` and `state` columns are also added defensively via `TryAlterAsync` (`ALTER TABLE projects ADD COLUMN default_branch TEXT;` / `... ADD COLUMN state TEXT NOT NULL DEFAULT 'active';`) so the migration is additive and idempotent.

`SqliteProjectStore` (in `apps/Scaffolder.Api/Infrastructure/`, mirroring `SqliteRunStore`: `OpenConnectionAsync`, parameterized `AddWithValue`, `Map(reader)`, `Ts()`/`NullableTs()`, ordinal-documented `SelectSql`). It implements a Domain interface so runners/services depend on the abstraction (like `ISandboxPolicyStore`):

```csharp
namespace Scaffolder.Domain;

public interface IProjectStore
{
    Task InsertAsync(Project project, CancellationToken ct = default);
    Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default);                                  // ordered by created_at
    Task<bool> UpdateNameAsync(ProjectId id, string name, DateTimeOffset updatedAt, CancellationToken ct = default);          // FR-017
    Task<bool> UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, DateTimeOffset updatedAt, CancellationToken ct = default); // FR-018
    Task<bool> UpdateWorkingDirectoryAsync(ProjectId id, string workingDirectory, DateTimeOffset updatedAt, CancellationToken ct = default);          // FR-026 relink
    /// <summary>
    /// Transactional delete gate (FR-019 C): atomically flips state Active -> Deleting in a single
    /// conditional UPDATE (... WHERE project_id = $id AND state = 'active'). Returns true if THIS caller
    /// won the gate, false if the project is missing or already Deleting. This is the concurrency guard
    /// that closes the delete/run-create race: run creation is rejected once state = 'deleting'.
    /// </summary>
    Task<bool> TryBeginDeleteAsync(ProjectId id, DateTimeOffset updatedAt, CancellationToken ct = default);  // FR-019 CAS gate
    Task<bool> DeleteAsync(ProjectId id, CancellationToken ct = default);                                    // record-only; FR-019
}
```

`DeleteAsync` removes ONLY the row (`DELETE FROM projects WHERE project_id = $id;`). On-disk files are never touched here (FR-019). Each update returns `true` when exactly one row changed so endpoints can return 404 on a missing project.

`SqliteRunStore` gains two project-scoped reads (it currently has only `GetByStatusAsync(single status)`, which is not project-aware) plus one atomic project-run reservation that closes the residual delete/run-create TOCTOU (C, FR-019):

```csharp
// All runs for a project (newest first) - backs GET /api/projects/{id}/runs (ProjectPage run list).
Task<IReadOnlyList<Run>> GetRunsByProjectAsync(ProjectId projectId, CancellationToken ct = default);
// Non-terminal runs for a project in the given statuses - backs the delete cancellation sweep (FR-019 C).
Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
    ProjectId projectId, IReadOnlyCollection<RunStatus> statuses, CancellationToken ct = default);
// Atomically inserts a reserved (Pending) run row ONLY if the project is still Active, in a single
// transaction, so the project-Active check and the run-row insert cannot be split by a concurrent
// delete (C, FR-019). Returns true if the reservation row was written (project was Active), false if
// the project is missing or already 'deleting'. Mirrors the existing conditional-write CAS style
// (TrySetTerminalStatusAsync, TryTransition*). The run row exists BEFORE any worktree creation or
// workflow-registry side effect (section 4.3).
Task<bool> TryCreateProjectRunAsync(Run reservedRun, ProjectId projectId, CancellationToken ct = default);
```

Both reads filter on the new `project_id` column; `GetRunsByProjectAndStatusesAsync` adds a `status IN (...)` clause built from the requested terminal/non-terminal set. `TryCreateProjectRunAsync` performs a guarded insert -- conceptually `INSERT INTO runs (...) SELECT @values WHERE EXISTS (SELECT 1 FROM projects WHERE project_id = $id AND state = 'active')` (raw `Microsoft.Data.Sqlite`, one transaction) -- and returns `rows > 0`, so the project-Active check and the run-row insert commit, or fail, as one atomic statement. The reservation commits a `Pending` row BEFORE any side effect, so if a post-reservation side effect (worktree, workflow start, or the `Pending -> InProgress` transition) later fails, section 4.3 compensates that row to terminal `Failed` rather than leaving it dangling -- the reserved `Pending` is self-healing, not reliant on a later delete sweep. `SqliteRunStore.InsertAsync`, `SelectSql`/ordinals, and `Map` are extended for `project_id` and `model_id` (section 3.7).

### 3.3 `IProjectWorkspaceProvider` (FR-025 deployment-parity seam)

The storage/workspace abstraction is the FR-025 resolution. It lives in `Scaffolder.Domain` (interface) with implementations in `apps/Scaffolder.Api/Infrastructure/`. It is a fully implementable application seam, not a stub: every method below has concrete, testable behavior in BOTH implementations. The only part that is environment-supplied is the physical cloud-volume allocation, which is explicitly delineated at the end of this section.

```csharp
namespace Scaffolder.Domain;

/// <summary>Outcome of resolving/ensuring a project workspace; carries the absolute working directory.</summary>
public sealed record WorkspaceHandle(string WorkingDirectory, string BackendName);

/// <summary>
/// Resolves, validates, and manages the lifecycle of the physical working directory that backs
/// a project. The working directory is identical in path across deployments, so the
/// "working directory = sandbox boundary" model (FR-022) is identical too (Principle VI, FR-025).
/// </summary>
public interface IProjectWorkspaceProvider
{
    /// <summary>Backend name for observability (e.g. "local-filesystem", "persistent-volume").</summary>
    string BackendName { get; }

    /// <summary>
    /// Maps the user's requested path to the absolute on-disk working directory for the project.
    /// Local: canonicalized identity (current behavior, Path.GetFullPath). Cloud: the per-project
    /// path under the configured persistent-volume mount root. Pure mapping; does not touch disk.
    /// </summary>
    Task<string> ResolveWorkingDirectoryAsync(ProjectId id, string requestedPath, CancellationToken ct = default);

    /// <summary>
    /// Ensures the resolved working directory is present and usable as a sandbox boundary before a
    /// create or a run. Local: creates the directory if absent and confirms it is writable. Cloud:
    /// validates that the per-project volume is mounted at the resolved path and writable (mount
    /// validation); it does NOT allocate the volume (see boundary below). Throws a clear, non-leaking
    /// WorkspaceUnavailableException when the mount is missing so creation/run fails closed.
    /// </summary>
    Task<WorkspaceHandle> EnsureWorkspaceAsync(ProjectId id, string workingDirectory, CancellationToken ct = default);

    /// <summary>True when the resolved working directory exists and is an accessible, valid sandbox
    /// boundary (FR-026). Local: Directory.Exists + read/write probe. Cloud: mount present + writable.</summary>
    bool IsAvailable(string workingDirectory);

    /// <summary>
    /// Releases provider-held resources for a project WITHOUT deleting user content (record-only delete,
    /// FR-019). Local: no-op. Cloud: flush/detach the per-project mount handle; the volume and its files
    /// are left intact for the operator to reclaim or re-attach.
    /// </summary>
    Task ReleaseAsync(ProjectId id, string workingDirectory, CancellationToken ct = default);
}
```

- `LocalFilesystemWorkspaceProvider` (default; current behavior unchanged): `ResolveWorkingDirectoryAsync` returns `Path.GetFullPath(requestedPath)`; `EnsureWorkspaceAsync` creates the directory when absent and confirms it is writable; `IsAvailable` returns `Directory.Exists(path)` plus a read/write probe; `ReleaseAsync` is a no-op. `BackendName = "local-filesystem"`.
- `PersistentVolumeWorkspaceProvider` (cloud target): owns the deterministic mapping `ProjectId -> {Workspace:PersistentVolume:MountRoot}/{projectId}` (the resolved path the user sees and the run sandbox uses); `EnsureWorkspaceAsync` performs mount validation (the mapped path exists, is a mount point, and is writable) and throws `WorkspaceUnavailableException` when the mount is absent; `IsAvailable` performs the same mount/writability check (feeding `ProjectView.Available` and FR-026); `ReleaseAsync` detaches the mount handle and never deletes files. `BackendName = "persistent-volume"`.

**Infrastructure boundary (explicit, per Principle VI/VII).** Provider-OWNED application logic (built now, fully tested): path mapping, mount validation, availability detection, ensure/release lifecycle, and clear fail-closed errors. ENVIRONMENT-SUPPLIED/operational (out of application scope by design): the actual allocation and attachment of the per-project cloud volume (for example a Kubernetes PersistentVolumeClaim, a CSI driver, or a cloud-disk attach), which the deployment/orchestrator performs out of band and surfaces to the app as a mount appearing under `MountRoot`. The provider DETECTS and VALIDATES that mount; it never calls a cloud provisioning API itself. This keeps the seam complete and implementable while drawing a precise line at the infrastructure that legitimately belongs to the deployment - so it is a working seam, not a placeholder, and the local-directory model never precludes cloud.

Selection is by configuration `Workspace:Provider` (`local` default, `persistent-volume` for cloud), registered in DI. No environment branching leaks into `ProjectService` or the endpoints (Principle VI).

### 3.4 `ProjectService`

**Location**: `apps/Scaffolder.Api/Projects/ProjectService.cs` (new `Projects/` folder, peer to `Runs/`). Depends on `IProjectStore`, `IProjectWorkspaceProvider`, `IGitHubTokenStore`, `IGitHubAuthService` (for owner identity), `RunWorkflowRegistry` + `SqliteRunStore` (for delete-time run cancellation), `RepositoryRootValidator` (path canonicalization, reused), and `ILogger<ProjectService>`.

```csharp
public sealed class ProjectService
{
    Task<Project> CreateBlankAsync(string name, string requestedWorkingDirectory, string ownerFallback, CancellationToken ct);
    Task<Project> CreateFromGitHubAsync(string name, string sourceRepository, string requestedWorkingDirectory, string ownerFallback, CancellationToken ct);
    Task<Project> RenameAsync(ProjectId id, string newName, CancellationToken ct);
    Task<Project> UpdateProviderSettingsAsync(ProjectId id, ProjectProviderSettings settings, CancellationToken ct);
    Task<Project> RelinkAsync(ProjectId id, string newWorkingDirectory, CancellationToken ct);          // FR-026
    Task DeleteAsync(ProjectId id, bool confirmed, CancellationToken ct);                                // FR-019
    Task<IReadOnlyList<ProjectView>> ListAsync(CancellationToken ct);                                    // FR-008 (+availability)
    Task<ProjectView?> GetAsync(ProjectId id, CancellationToken ct);
}
```

Key behaviors (each mapped to its FR):

- **Name validation (FR-007)**: reject empty/whitespace-only names with a clear reason BEFORE touching the filesystem; no directory or record is created on failure.
- **Empty-or-non-existent directory rule for CREATION only (FR-003, FR-004, edge case "Target directory already exists or is non-empty")**: a private `EnsureEmptyOrCreatable(resolvedPath)` helper rejects creation when the resolved directory exists and is non-empty; it never overwrites or adopts content. Non-existent or empty is accepted. This rule is NOT applied to relink (see Relink below).
- **Creation compensation scope (FR-007, E)**: every create path runs inside a `CreationScope` that records whether the app created the working directory (vs adopted a pre-existing empty one) and whether git init/clone wrote into it. The order is always: (1) resolve + `EnsureWorkspaceAsync`, (2) validate emptiness, (3) filesystem work (init or clone), (4) `IProjectStore.InsertAsync`. If ANY step after (1) fails - including a clone failure, a blank-init failure, OR a DB `InsertAsync` failure after the filesystem already succeeded - the scope's `CompensateAsync` deletes ONLY app-created artifacts (the directory if the app created it; otherwise just the git metadata/contents the app wrote into a pre-existing empty dir) and persists NO record. Pre-existing user content is never deleted. This closes the "git init/clone succeeds then DB persistence fails" partial-project gap.
- **Blank create (FR-003, B)**: resolve path via the workspace provider and `EnsureWorkspaceAsync`, validate emptiness, then `LibGit2Sharp.Repository.Init(resolvedPath)` followed by an INITIAL EMPTY COMMIT on a default branch: stage nothing and `repo.Commit("Initial commit", sig, sig, new CommitOptions { AllowEmptyCommit = true })`, with the branch named from `Workspace:DefaultBranch` (default `main`). This is required because `WorktreeManager.AddWorktree` looks up `repo.Branches[originatingBranch]` and forks from `origin.Tip`; a bare `git init` repo is unborn (no branch ref, no commit tip) and `AddWorktree` would throw "Originating branch not found." The created branch name is recorded as `Project.DefaultBranch` so a run can start immediately. Persist with `Origin = ProjectOrigin.Blank`.
- **From-GitHub create (FR-004, FR-005, FR-023)**: resolve and `EnsureWorkspaceAsync`, validate emptiness, then `Repository.Clone(sourceUrl, resolvedPath, cloneOptions)` where `cloneOptions.FetchOptions.CredentialsProvider` returns a `UsernamePasswordCredentials` whose password is the token resolved from `IGitHubTokenStore` for the caller's scope (ephemeral, in-process only). The token is NEVER embedded in the remote URL and never written to `.git/config` (section 11 item 7). `Project.DefaultBranch` is set to the cloned repo's checked-out HEAD branch (`repo.Head.FriendlyName`). Record `Origin = ProjectOrigin.FromGitHub(sourceRepository)`. On any clone failure (bad ref, no network, no access, interruption), the `CreationScope` compensates (delete only app-created artifacts) and persists NO record, surfacing a clear, credential-free reason (FR-007, edge cases "Invalid or unreachable GitHub reference", "Clone interrupted mid-way").
- **Owner resolution (FR-024)**: `Owner = (await gitHubAuth.GetStatusAsync()).Login` when a GitHub sign-in is present; otherwise `ownerFallback` (the local OS/installation user, supplied by the endpoint from `CallerContext.User`). Recorded on the project for accountability/audit; never a secret.
- **Provider settings update (FR-013, FR-018, edge case "Unsupported provider")**: validate `DefaultProvider` parses to one of the two `ModelSource` values via `ModelSourceExtensions.FromApiString`; reject anything else.
- **Rename (FR-017)**: update `name` + `updated_at`.
- **Relink (FR-026, D) - SEPARATE validation from creation**: relink reuses `RepositoryRootValidator.ValidateAndCanonicalize` (accessible, safe, canonicalized, real-path resolved, UNC/device/relative rejected) but DELIBERATELY does NOT apply the empty-dir rule, because FR-026 recovery typically points the project at a moved or restored, NON-empty working directory. Relink additionally requires that the target is a valid git repository (`Repository.IsValid(path)`) and, where determinable for a `FromGitHub` project, that its `origin` remote matches the project's recorded `SourceRepository` (a clear "directory does not match this project's repository" error otherwise). On success it updates `working_directory` (and re-derives `DefaultBranch` from the relinked repo's HEAD) so the sandbox boundary is valid again; existing files are preserved.
- **Delete (FR-019, Principle X, C) - race-safe gate + await-to-terminal**: require `confirmed == true` (endpoint also enforces explicit confirmation). Then: (1) `IProjectStore.TryBeginDeleteAsync` atomically flips `state` Active -> Deleting; if it returns false, the project is missing (404) or already deleting (idempotent 202/409). This CAS flip is the serialization point: once `Deleting` is set, the atomic `TryCreateProjectRunAsync` reservation (section 3.2) can no longer insert a run row (its `WHERE EXISTS ... state = 'active'` guard fails), so no NEW `POST /api/projects/{id}/runs` can begin - the delete/run-create race is closed at the database level rather than via a separate pre-read. (2) Enumerate the project's non-terminal runs via `GetRunsByProjectAndStatusesAsync(id, [Pending, InProgress, Committing, Merging, AwaitingReview])` - this set includes any run whose reservation committed in the instant BEFORE the flip (it is already a persisted `Pending` row), so a run-create that won the race is cancelled here rather than orphaned. (3) For each: call `RunWorkflowRegistry.Abandon(runId)` to cancel the run's `CancellationTokenSource` (stopping the agent loop) AND authoritatively `SqliteRunStore.TrySetTerminalStatusAsync(runId, Failed, now, "cancelled: project deleted")` - this is required because, on abandonment, the watch loop catches the cancellation and logs "abandoned" WITHOUT writing any terminal status, so `Abandon` alone leaves the run non-terminal. Emit a `run.cancelled` event (section 4.4). `TrySetTerminalStatusAsync` is conditional (only non-terminal -> terminal) and idempotent, so it is safe against the watch loop and any dual writer. (4) Re-query `GetRunsByProjectAndStatusesAsync`; if any non-terminal run remains (a late unconditional `UpdateReviewReadyAsync` landed in the small window after Abandon but before the agent loop observed cancellation), repeat step 3 with a bounded retry (e.g. up to N attempts with a short backoff) until the set is empty or the bound is hit (then 500 with a non-leaking message, leaving the project in `Deleting` for retry). (5) Only when zero non-terminal runs remain, call `IProjectWorkspaceProvider.ReleaseAsync` (detach only; never deletes files) and `IProjectStore.DeleteAsync`. On-disk files are always preserved.

`ProjectView` is the read model returned to endpoints:

```csharp
public sealed record ProjectView(Project Project, bool Available);   // Available from IProjectWorkspaceProvider.IsAvailable (FR-026)
```

### 3.5 GitHub OAuth device-flow sign-in and tenancy-keyed token store

This is the net-new security surface (FR-005, FR-016). Interfaces live in `Scaffolder.Domain` so both `Scaffolder.Api` and `Scaffolder.AgentRuntime` (which already references Domain) can consume them; implementations live in `apps/Scaffolder.Api/`.

```csharp
namespace Scaffolder.Domain;

public sealed record DeviceFlowStart(string UserCode, string VerificationUri, int IntervalSeconds, int ExpiresInSeconds);
public enum GitHubAuthState { SignedOut, Pending, SignedIn, Expired, Denied }
public sealed record GitHubAuthStatus(GitHubAuthState State, string? Login);   // Login only; NEVER a token

/// <summary>
/// Identifies the caller/tenant whose GitHub credentials are being read or written. The contract is
/// identical local vs cloud (FR-016, Principle VI); only the key SCOPE differs: a single fixed
/// installation key locally, a per-authenticated-caller key in cloud. This removes the global
/// mutable token state that allowed token-bleed and sign-out races between callers.
/// </summary>
public readonly record struct GitHubTokenScope(string Key)
{
    public static readonly GitHubTokenScope Installation = new("installation");   // local single-OS-user default
    public static GitHubTokenScope ForUser(string user) => new($"user:{user}");   // cloud per-tenant
}

/// <summary>Resolves the active token scope for the current execution context.</summary>
public interface IGitHubTokenScopeProvider
{
    GitHubTokenScope Resolve(string? caller);   // local: always Installation; cloud: ForUser(caller)
}

/// <summary>
/// The single "Sign in with GitHub" device flow, reachable identically from CLI and Web (FR-005, Principle IV).
/// A successful sign-in grants clone access (incl. private) AND authorizes GitHub Copilot in place of a Copilot API key.
/// It NEVER authorizes Microsoft Foundry. All operations are scoped to the caller (FR-016).
/// </summary>
public interface IGitHubAuthService
{
    Task<DeviceFlowStart> StartDeviceFlowAsync(GitHubTokenScope scope, CancellationToken ct = default);  // POST github.com/login/device/code
    Task<GitHubAuthStatus> PollAsync(GitHubTokenScope scope, CancellationToken ct = default);            // POST github.com/login/oauth/access_token (device_code held server-side, per scope)
    Task<GitHubAuthStatus> GetStatusAsync(GitHubTokenScope scope, CancellationToken ct = default);       // identity only
    Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default);                           // purge token + write SignedOut tombstone; Copilot/clone fail closed (section 11 item 3)
}

/// <summary>
/// Tenancy-keyed GitHub token store (FR-016, Principle VI). Tokens are secret-grade and NEVER persisted
/// on a project record, in SQLite, or in logs/telemetry (FR-005, FR-016, FR-023, Principles IX, XI).
/// Every method is keyed by GitHubTokenScope so two callers in a shared (cloud) deployment can never read
/// or clobber each other's credentials. The interface is identical local vs cloud; only the backing key scope differs.
/// </summary>
public interface IGitHubTokenStore
{
    Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default);  // SignedIn token | SignedOut tombstone | NeverSignedIn
    Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default);
    Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default); // login only
    Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default);                 // purge token, write SignedOut tombstone (H)
}

/// <summary>
/// Tri-state result so the Copilot factory can distinguish an explicit sign-out (fail closed, no config
/// fallback) from never having signed in (config fallback permitted, but LOCAL installation scope ONLY -
/// never under the cloud `caller` scope). See section 3.6 / 11 item 3 (FR-005).
/// </summary>
public enum GitHubTokenStatus { SignedIn, SignedOut, NeverSignedIn }
public sealed record GitHubTokenEntry(GitHubTokenStatus Status, string? AccessToken);

public sealed record GitHubToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string Login, string[] Scopes);
public sealed record GitHubIdentity(string Login);
```

Implementations:

- `GitHubDeviceFlowAuthService` (`apps/Scaffolder.Api/Auth/`): real device-flow against a configurable GitHub base URL (`Auth:GitHub:BaseUrl`, default `https://github.com`) using `Auth:GitHub:ClientId`. Requests the minimal scopes pinned by the section 3.8 spike (section 11 item 1). Holds the in-flight `device_code` server-side keyed by scope (never returned to clients); on success persists the token via `IGitHubTokenStore.SetAsync(scope, ...)`.
- `OsCredentialStoreGitHubTokenStore` (local): Windows Credential Manager / DPAPI-protected entries; the credential target name embeds `scope.Key` (always `installation` locally). Never plaintext on disk. A `SignedOut` tombstone is a distinct stored marker (no token) so fallback is suppressed after explicit sign-out (H).
- `EncryptedSecretStoreGitHubTokenStore` (cloud): encrypted server-side secret store, one entry per `scope.Key` (per-user/per-tenant; section 11 item 5). Same tri-state SignedIn / SignedOut / NeverSignedIn semantics.
- `IGitHubTokenScopeProvider`: `FixedInstallationScopeProvider` locally (always `Installation`); `CallerTokenScopeProvider` in cloud resolves `GitHubTokenScope.ForUser(caller)`. The API path supplies `caller` from `CallerContext.User`; the background runner path (no HttpContext) supplies it from the run's owner threaded through `AgentTurnInput` (section 3.6 / 3.7).

Token store + scope provider selection is by config (`Auth:GitHub:TokenStore` = `os` default locally, `secret-store` in cloud; `Auth:GitHub:ScopeProvider` = `installation` default locally, `caller` in cloud). They are registered in DI BEFORE `AddAgentRuntime()` so `GitHubCopilotClientFactory` (a singleton) can resolve `IGitHubTokenStore`.

### 3.6 Copilot authorization swap (no stored key, fail-closed) and Foundry separation

`GitHubCopilotClientFactory` today is constructed synchronously and reads `Providers:GitHubCopilot:GitHubToken`/`ApiKey` directly (its `CreateClient()` is called synchronously at `GitHubCopilotAgentRunner` construction). It gains an `IGitHubTokenStore` + `IGitHubTokenScopeProvider` dependency and becomes asynchronous (`CreateClientAsync(scope, modelId, ct)`), because the token is now read at run time per scope, not at construction. The token resolution implements an EXPLICIT, fail-closed fallback policy (FR-005, H) using the tri-state entry from section 3.5:

```csharp
public GitHubCopilotClientFactory(IConfiguration configuration, IGitHubTokenStore tokenStore) { ... }

public async Task<CopilotClient> CreateClientAsync(GitHubTokenScope scope, string? modelId, CancellationToken ct)
{
    var options = new CopilotClientOptions();
    var entry = await _tokenStore.GetAsync(scope, ct);                    // tri-state (FR-005, H)
    var token = entry.Status switch
    {
        GitHubTokenStatus.SignedIn      => entry.AccessToken,            // use the sign-in token (FR-005)
        GitHubTokenStatus.SignedOut     => null,                         // explicit sign-out: FAIL CLOSED, config fallback SUPPRESSED (H)
        GitHubTokenStatus.NeverSignedIn => _configFallbackToken,         // never signed in: config token MAY be used (LOCAL non-interactive installs only; null under cloud caller scope)
        _ => null
    };
    if (string.IsNullOrWhiteSpace(token))
        throw new GitHubCopilotUnauthorizedException("GitHub Copilot is not authorized; sign in with GitHub.");  // non-leaking
    options.GitHubToken = token;
    return new CopilotClient(options);
}
```

The fallback policy is now unambiguous (resolves the sign-out vs config-fallback contradiction, H): a stored `Providers:GitHubCopilot:GitHubToken` may satisfy Copilot ONLY in the `NeverSignedIn` state (a non-interactive install that never used the device flow). After an explicit `SignOutAsync` writes the `SignedOut` tombstone, the factory returns no token and Copilot fails closed - it does NOT silently fall back to the config token. The `NeverSignedIn` config-token fallback is further confined to the LOCAL / non-interactive installation scope (`GitHubTokenScope.Installation`): it is explicitly DISABLED for the cloud `caller` scope, where an interactive device-flow sign-in is always required and no installation-wide config token exists in a multi-tenant deployment, so `_configFallbackToken` resolves to null unless the scope is `Installation`. This is asserted by a dedicated test (section 7, Phase 7).

Because the factory is now async and scope-aware, the call site at `GitHubCopilotAgentRunner` is updated to `await CreateClientAsync(scope, modelId, ct)`. The `scope` comes from `IGitHubTokenScopeProvider` resolved from the run owner threaded via `AgentTurnInput` (the runner executes in a detached `Task.Run` with no HttpContext, so the owner must travel with the run - section 3.5 / 3.7 G). The token is then looked up by the resolved `GitHubTokenScope` KEY (`IGitHubTokenScopeProvider.Resolve(owner)` -> `Installation` locally, `ForUser(owner)` in cloud), NOT by the raw project owner or GitHub login directly: the owner is only the INPUT to scope resolution and the token store is keyed exclusively by the resulting `GitHubTokenScope`, so the background-runner lookup key is identical to the API-path key for the same caller and a cloud token-lookup mismatch is avoided after this async, scope-aware factory change.

Per-run model application (A): the resolved per-run `modelId` (section 3.7) is applied where each SDK actually accepts a model. For Copilot, the model is set on the session via `SessionConfig.Model = modelId` (verified to exist on the SDK's `SessionConfigBase`); a null `modelId` leaves the SDK default in place. For Foundry, the model is the Azure OpenAI deployment, so `FoundryClientFactory` gains `CreateChatClient(string? deploymentOverride)` that calls `AzureOpenAIClient.GetChatClient(deploymentOverride ?? _configuredDeployment)`; a null override keeps the configured deployment. Neither factory ever logs the token or the model resolution inputs in a way that could leak a credential (section 11 item 6).

When a valid GitHub sign-in is present, NO Copilot-specific API key is required, prompted for, or stored (FR-005, FR-016, SC-010). `FoundryClientFactory` continues to read ONLY `Providers:MicrosoftFoundry:{Endpoint,ApiKey,Deployment}` (plus the new per-run deployment override); it has no reference to `IGitHubTokenStore`, so no code path lets a GitHub token satisfy a Foundry call (FR-013, SC-010, section 11 item 8).

### 3.7 Run-to-project linkage and per-run model plumbing (A, B)

**Project linkage + blank-branch semantics (B).** The `Run` record gains an optional `ProjectId? ProjectId` (nullable for backward compatibility with pre-feature runs). Run creation resolves the run's `RepositoryPath` from the project's `WorkingDirectory`, so the existing sandbox machinery (worktree rooted at `RepositoryPath`, `SandboxPolicyValidator`) treats the project directory as the boundary with zero changes to the sandbox enforcement code (FR-022, Principle X). The worktree is forked from the project's `DefaultBranch` (which, for a blank project, is the branch created by the initial empty commit in section 3.4 B) so `WorktreeManager.AddWorktree` finds an existing branch with a commit tip instead of throwing "Originating branch not found." A run started against an unavailable project (workspace not present) is rejected before any worktree is created (FR-026), and a run started against a project in `Deleting` state is rejected by the delete gate (section 3.4 C).

**Per-run model plumbing end to end (A, FR-014/FR-015, SC-004).** Today a run carries only a provider (`Run.ModelSource`); there is no per-run model id anywhere in the chain. This feature threads an optional `string? ModelId` through the entire run-execution path so a specific model can be selected per run and the project default is honored when omitted:

1. **Request DTO**: `CreateProjectRunRequest` / `CreateRunRequest` gains `[JsonPropertyName("model_id")] string? ModelId` alongside the existing `model_source`.
2. **Resolution order** (computed at run-create, section 4.3): explicit per-run `model_id` -> the project's `ProviderSettings` default for the SELECTED provider (`GitHubCopilotModel` or `MicrosoftFoundryModel`) -> `null` (meaning "use the provider runtime default"). The selected provider itself follows the same explicit-then-project-default order and must be one of the two `ModelSource` values.
3. **Domain**: `Run` gains `string? ModelId` (immutable, set at creation).
4. **Persistence**: a `runs.model_id TEXT NULL` column added via `TryAlterAsync` (additive migration, section 3.2); `SqliteRunStore` reads/writes it; existing rows read back `null` (backward compatible).
5. **Workflow message**: `AgentTurnInput` (which today carries `RunId, Task, WorktreePath, WorktreeBranch, RepositoryPath, OriginatingBranch, ModelSource`) GAINS `string? ModelId` AND the run owner identity (`string SubmittingUser`, sourced from `Run.SubmittingUser` when the workflow input is constructed) so the detached background runner can resolve the GitHub token scope without an HttpContext (G). `AgentTurnExecutor` passes `ModelId` and the owner into the dispatcher alongside `ModelSource.FromApiString(input.ModelSource)`. The owner is used SOLELY as the input to `IGitHubTokenScopeProvider.Resolve(...)`; the resulting `GitHubTokenScope` key - not the owner or GitHub login - is what keys the token lookup (section 3.6), so the runner and the API resolve the identical scope for the same caller.
6. **Runner contract**: `IAgentRunner.ExecuteAsync(...)` and `AgentRunnerDispatcher` gain a `string? modelId` parameter, routed to the two runners. `GitHubCopilotAgentRunner` applies it via `SessionConfig.Model` (section 3.6); `FoundryAgentRunner` applies it via `FoundryClientFactory.CreateChatClient(modelId)` (section 3.6).
7. **API validation + unavailable-model recovery (spec edge case "default model no longer available")**: at run-create, when a `model_id` is explicit (or a stored project default is non-null), it is validated against the selected provider's available models - for Copilot via the SDK model listing (`CopilotClientOptions.OnListModels` / the SDK `GetModelsResponse`), for Foundry against the configured/available deployment(s). An unknown or no-longer-available model returns HTTP 409 `model_unavailable` with the list of available model ids, so the client (Web run dialog or CLI) can re-prompt. This makes a stale project-default model a recoverable, clearly-surfaced condition rather than a hard run failure. The project's `ProviderSettings` are presented as the pre-selected provider/model when a run is created, but both remain overridable per run (FR-015, SC-004).

### 3.8 Copilot OAuth grant validation spike and fallback (I, FR-005)

The single "Sign in with GitHub" must grant BOTH private-repo clone access AND GitHub Copilot authorization with one device-flow token. Whether one OAuth grant can satisfy both is the highest-risk unknown in the design, so it is an EARLY validation spike at the START of Phase 2 (not a late integration detail) - Phase 2 does not proceed on the assumption until the spike confirms it.

- **Scope constraint**: the device-flow authorization is pinned to the minimal scopes `repo` (private clone) + `read:user` (identity/owner, FR-024). No broader scope is requested. The exact grant is recorded in config (`Auth:GitHub:ClientId`, `Auth:GitHub:Scopes`) and pinned against the GitHub Copilot SDK / OAuth app expectations (the SDK's `CopilotClientOptions.GitHubToken` path is the seam the token feeds; the .NET cookbook at the GitHub awesome-copilot repo is the reference for the exact grant the SDK expects).
- **Spike outcome A (preferred)**: one device-flow token with `repo` + `read:user` is accepted by `CopilotClientOptions.GitHubToken` for Copilot AND by LibGit2Sharp `UsernamePasswordCredentials` for clone. The factory feeds the SAME stored token to both paths. This is the simplest UX and is the default if the spike confirms it.
- **Spike outcome B (concrete fallback, still satisfies FR-005)**: if a single token cannot authorize both, the design splits responsibilities WITHOUT adding a second user-facing sign-in: the OAuth device-flow token (scope `repo` + `read:user`) covers clone, while Copilot is authorized via the SDK's own logged-in-user device authorization (`CopilotClientOptions.UseLoggedInUser`, verified to exist on the SDK). Both are driven from the one "Sign in with GitHub" action; the user still performs a single sign-in gesture, and Foundry is never involved. The token store records the clone token under the caller scope (section 3.5); Copilot uses the SDK's logged-in-user path.
- Either outcome keeps sign-out fail-closed (section 3.6 H): signing out purges the stored clone token and, in outcome B, also clears the SDK logged-in-user session so Copilot stops working too.
- The spike's deliverable is a short recorded decision (which outcome) plus the pinned scope list; the rest of Phase 2 builds on the confirmed path. This removes the "late surprise" risk the reviewers flagged.

---

## 4. Integration Points

### 4.1 API endpoints (Principle III, FR-020)

All under `/api`, behind `ApiKeyAuthMiddleware`, with snake_case DTOs in `apps/Scaffolder.Api/Contracts/Dtos.cs` (matching the existing `[JsonPropertyName]` convention). New endpoints registered inline in `Program.cs` following the existing `MapPost`/`MapGet` style.

Project CRUD and lifecycle:

| Method + Route | Purpose | FRs |
|---|---|---|
| `POST /api/projects` | Create blank or from GitHub (body discriminated by `origin`) | FR-001, FR-003, FR-004, FR-006, FR-007, FR-024 |
| `GET /api/projects` | List all projects as cards data, each with `available` | FR-008, FR-009, FR-010, FR-026 |
| `GET /api/projects/{id}` | Open/enter a project (full record + availability) | FR-011, FR-026 |
| `PATCH /api/projects/{id}` | Rename | FR-017 |
| `PUT /api/projects/{id}/provider-settings` | View/set default provider + per-provider default model | FR-012, FR-013, FR-014, FR-018 |
| `POST /api/projects/{id}/relink` | Relink to a moved/restored working directory (may be non-empty) | FR-026 |
| `DELETE /api/projects/{id}?confirm=true` | Record-only delete; gate to `Deleting`, cancel + await in-flight runs to terminal, then remove record (files preserved) | FR-019 |
| `POST /api/projects/{id}/runs` | Start a run in the project (working dir + `project_id` + resolved provider/model); 409 when project is `Deleting` | FR-014, FR-015, FR-022 |
| `GET /api/projects/{id}/runs` | List the project's runs (newest first) for the ProjectPage run list | FR-009, FR-020 |

GitHub sign-in (reachable identically from both clients; FR-005, Principle IV):

| Method + Route | Purpose |
|---|---|
| `POST /api/auth/github/device` | Start device flow; returns `user_code`, `verification_uri`, `interval`, `expires_in` |
| `POST /api/auth/github/poll` | Poll/exchange the held device code; returns auth state |
| `GET /api/auth/github` | Current sign-in status (login only, never a token) |
| `POST /api/auth/github/sign-out` | Purge the token; Copilot and clone fail closed afterward |

Representative DTOs:

```csharp
public sealed record CreateProjectRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }                 // "blank" | "github"
    [JsonPropertyName("source_repository")] public string? SourceRepository { get; init; } // required when origin == "github"
    [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; init; }
    [JsonPropertyName("default_provider")] public string? DefaultProvider { get; init; }   // defaults to github-copilot
    [JsonPropertyName("default_model_github_copilot")] public string? DefaultModelGitHubCopilot { get; init; }
    [JsonPropertyName("default_model_microsoft_foundry")] public string? DefaultModelMicrosoftFoundry { get; init; }
}

public sealed record ProjectResponse
{
    [JsonPropertyName("project_id")] public required string ProjectId { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("origin")] public required string Origin { get; init; }              // "blank" | "github"
    [JsonPropertyName("source_repository")] public string? SourceRepository { get; init; }
    [JsonPropertyName("working_directory")] public required string WorkingDirectory { get; init; }
    [JsonPropertyName("owner")] public required string Owner { get; init; }
    [JsonPropertyName("default_provider")] public required string DefaultProvider { get; init; }
    [JsonPropertyName("default_model_github_copilot")] public string? DefaultModelGitHubCopilot { get; init; }
    [JsonPropertyName("default_model_microsoft_foundry")] public string? DefaultModelMicrosoftFoundry { get; init; }
    [JsonPropertyName("available")] public required bool Available { get; init; }          // FR-026
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("updated_at")] public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record GitHubDeviceFlowResponse
{
    [JsonPropertyName("user_code")] public required string UserCode { get; init; }
    [JsonPropertyName("verification_uri")] public required string VerificationUri { get; init; }
    [JsonPropertyName("interval")] public required int Interval { get; init; }
    [JsonPropertyName("expires_in")] public required int ExpiresIn { get; init; }
}

public sealed record GitHubAuthStatusResponse
{
    [JsonPropertyName("state")] public required string State { get; init; }                // signed_out | pending | signed_in | expired | denied
    [JsonPropertyName("login")] public string? Login { get; init; }                        // never a token
}

public sealed record CreateProjectRunRequest
{
    [JsonPropertyName("task")] public string? Task { get; init; }                          // the run instruction
    [JsonPropertyName("model_source")] public string? ModelSource { get; init; }           // optional per-run provider override (one of the two); else project default
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }                   // optional per-run model override (A); else project default for provider; else runtime default
    [JsonPropertyName("base_branch")] public string? BaseBranch { get; init; }             // optional; else project DefaultBranch (B)
}

public sealed record ProjectRunSummaryResponse
{
    [JsonPropertyName("run_id")] public required string RunId { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("model_source")] public required string ModelSource { get; init; }
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
}
```

Endpoint behaviors mirror the existing run endpoints: validate inputs, return `400` with `{ "error": ... }` on bad input, `404` on missing project, `409` when deleting without `confirm=true`, `409` `model_unavailable` (with the available model id list) when a requested or stale-default model is not available (section 3.7), `409` when a run-create targets a project already in `Deleting` state (section 3.4 C), and resolve the accountable owner from `ApiKeyAuthMiddleware.GetCaller(httpContext).User` as the local-user fallback (FR-024). Errors from clone/auth are surfaced with credential-free messages (FR-023, section 11 item 6).

### 4.2 DI registrations in `Program.cs`

Added to the composition block (around lines 31-61), BEFORE `builder.Services.AddAgentRuntime()` so the Copilot factory can resolve `IGitHubTokenStore`:

```csharp
builder.Services.AddSingleton<IProjectStore, SqliteProjectStore>();
builder.Services.AddSingleton<IProjectWorkspaceProvider>(sp =>
    sp.GetRequiredService<IConfiguration>()["Workspace:Provider"] == "persistent-volume"
        ? new PersistentVolumeWorkspaceProvider(/* config */)
        : new LocalFilesystemWorkspaceProvider());
builder.Services.AddSingleton<IGitHubTokenStore>(sp =>
    sp.GetRequiredService<IConfiguration>()["Auth:GitHub:TokenStore"] == "secret-store"
        ? new EncryptedSecretStoreGitHubTokenStore(/* config */)
        : new OsCredentialStoreGitHubTokenStore());
builder.Services.AddSingleton<IGitHubTokenScopeProvider>(sp =>
    sp.GetRequiredService<IConfiguration>()["Auth:GitHub:ScopeProvider"] == "caller"
        ? new CallerTokenScopeProvider()
        : new FixedInstallationScopeProvider());
builder.Services.AddSingleton<IGitHubAuthService, GitHubDeviceFlowAuthService>();
builder.Services.AddHttpClient();   // for the device-flow service
builder.Services.AddSingleton<ProjectService>();
```

`SqliteDb.EnsureCreatedAsync()` (already awaited at startup) creates the `projects` table (with `default_branch` and `state` columns) and runs the additive `runs.project_id` and `runs.model_id` migrations via `TryAlterAsync` (section 3.2). `GitHubCopilotClientFactory` registration is unchanged in shape; it now takes `IGitHubTokenStore` + `IGitHubTokenScopeProvider` via constructor injection, which is why the token store + scope provider are registered BEFORE `AddAgentRuntime()`.

### 4.3 Run creation using a project working directory, `project_id`, and per-run model (A, B)

`POST /api/projects/{id}/runs` loads the project and: (1) fast-fails with 409 when the loaded `project.State == Deleting` (an early, non-authoritative optimization only - the authoritative guard is the atomic reservation in step 6); (2) rejects when `available == false` (FR-026, workspace not present); (3) resolves the provider via explicit `model_source` -> project default provider, and the model via explicit `model_id` -> project default for that provider -> null (runtime default) (section 3.7 A); (4) validates the resolved model against the provider's available models, returning 409 `model_unavailable` with the available list when it is gone (spec edge case "default model no longer available"); (5) builds the run record (`repository_path = project.WorkingDirectory`, `project_id`, the resolved `model_id`, base branch from the request or `project.DefaultBranch` - section 3.7 B, so a freshly created blank project starts from its initial-commit branch) in `Pending` status; (6) ATOMICALLY RESERVES the run via `SqliteRunStore.TryCreateProjectRunAsync(reservedRun, projectId, ct)` (section 3.2), which inserts the `Pending` row guarded by the project being Active in a single transaction. If it returns false the project moved to `Deleting` between the load and the insert, so the run-create is rejected with 409 - this conditional insert, not the step-1 read, is the AUTHORITATIVE guard that closes the TOCTOU. Only AFTER the reservation commits does the flow perform side effects: create the worktree (forked from the resolved base branch) and start the workflow, then transition the reserved row `Pending -> InProgress`, recording `worktree_path`/`worktree_branch` via a small `SqliteRunStore` update that mirrors the existing post-insert status updates. Because the reserved row is already committed, this post-reservation side-effect sequence (worktree creation, workflow start, and the `Pending -> InProgress` transition) is wrapped in a compensation scope so a reservation can never leak as a stuck `Pending` row: on ANY failure after `TryCreateProjectRunAsync` succeeds and before the run is registered in `RunWorkflowRegistry` and observable in the watch loop, the flow (1) authoritatively terminalizes the reserved row via the conditional+idempotent `SqliteRunStore.TrySetTerminalStatusAsync(runId, Failed, now, "run_start_failed")`, (2) emits the existing terminal run-failed event (`EventTypes.RunFailed` = `run.failed`, carrying a non-leaking `reason`) so the failure is observable in the run stream (Principle V), and (3) cleans up only what THIS attempt created, in reverse order of creation - mirroring the `CreationScope` idiom from section 3.4 / issue E - undoing the workflow-registry entry (`RunWorkflowRegistry.Abandon`/`Remove`), the worktree (`WorktreeManager.RemoveWorktree`), and the stream entry (`RunStreamStore.Remove`). This makes the reserved `Pending` row self-healing on start failure rather than relying on a future project-delete sweep to reap it, and it mirrors the existing direct `/api/runs` revision path, which on a start failure already terminalizes via `TrySetTerminalStatusAsync(Failed, "revision_start_failed")` and records `EventTypes.RunFailed`.

This deliberately reorders the existing `RunOrchestrator.StartRunAsync` sequence, which today calls `WorktreeManager.AddWorktree` (a filesystem side effect) and only THEN `SqliteRunStore.InsertAsync` (persisting an already-`InProgress` row): for a project run the reserved row must be persisted atomically with the Active check BEFORE any worktree or workflow-registry side effect. `RunOrchestrator` gains a project-run entry that takes the pre-reserved run and skips the legacy insert-after-worktree order; the unrelated direct `/api/runs` path is unchanged. `SqliteRunStore.InsertAsync` is still extended to persist `project_id` and `model_id` for that direct path. The owner is carried onto the run (`SubmittingUser`) so the background runner can resolve the GitHub token scope (section 3.5 G).

### 4.4 Race-safe run cancellation on project delete (C)

`ProjectService.DeleteAsync` implements the gate + await-to-terminal sequence from section 3.4: `TryBeginDeleteAsync` (CAS Active -> Deleting, the serialization point that makes the atomic `TryCreateProjectRunAsync` reservation - sections 3.2/4.3 - fail for any run-create that has not already committed its reserved row), then for every non-terminal run from `GetRunsByProjectAndStatusesAsync` it calls `RunWorkflowRegistry.Abandon(runId)` (cancels the run CTS) AND the authoritative `SqliteRunStore.TrySetTerminalStatusAsync(runId, Failed, now, "cancelled: project deleted")`, emitting a new `EventTypes.RunCancelled = "run.cancelled"` event so the cancellation is observable in the run stream (Principle V) and audit log (Principle X). `Abandon` alone is insufficient because the watch loop only logs on abandonment without writing a terminal status; `TrySetTerminalStatusAsync` is conditional+idempotent, so it is safe against the watch loop and the unconditional `UpdateReviewReadyAsync` writer. The method re-queries and bounded-retries until zero non-terminal runs remain, then calls `IProjectWorkspaceProvider.ReleaseAsync` and `IProjectStore.DeleteAsync`. Using the existing `Failed` terminal state avoids a status-enum/web-union migration; the dedicated event captures the cancellation reason (FR-019). A concurrency integration test asserts that a delete racing a `POST /{id}/runs` either never starts the run (reservation rejected once `Deleting`) or cancels the reserved/started run to a terminal state, and never leaves an orphaned live run (section 7, Phase 7, T023b).

### 4.5 CLI subcommands (Principle IV, FR-021)

New top-level commands `project` and `github` added to the `CliEntryPoint` dispatch switch in `apps/Scaffolder.Cli/Program.cs`, with new `ProjectCommands.cs` and `GitHubAuthCommands.cs` and corresponding `ApiClient` methods and `Models.cs` DTOs. The CLI holds NO business logic; it calls the API.

```text
scaffolder project create --blank --name <n> --dir <path>
scaffolder project create --from-repo <owner/name> --name <n> --dir <path>
scaffolder project list
scaffolder project show <project-id>
scaffolder project configure <project-id> --default-provider <github-copilot|microsoft-foundry>
                                          [--copilot-model <m>] [--foundry-model <m>]
scaffolder project rename <project-id> --name <new-name>
scaffolder project relink <project-id> --dir <path>
scaffolder project run <project-id> --task <text> [--provider <github-copilot|microsoft-foundry>] [--model <id>] [--base-branch <b>]
scaffolder project runs <project-id>          # list the project's runs (parity with Web ProjectPage run list)
scaffolder project delete <project-id> --confirm
scaffolder github sign-in     # prints user_code + verification_uri, then polls to completion
scaffolder github sign-out
scaffolder github status
```

`PrintUsage()` is extended to document the new commands, and the CLI dispatch guard that enumerates valid top-level commands (currently `run`, `sandbox-policy`) is extended with `project` and `github`. `scaffolder project run` gives the CLI the same start-a-run-inside-a-project capability the Web ProjectPage has (Principle IV parity), including the per-run provider/model override (A) and optional base branch (B); it calls `POST /api/projects/{id}/runs`. `scaffolder project runs` calls `GET /api/projects/{id}/runs`. `scaffolder github sign-in` renders the device code and verification URL with Spectre.Console and polls `POST /api/auth/github/poll` at the returned interval until signed in, expired, or denied.

### 4.6 Web pages (Principle IV, FR-009, FR-010, FR-021)

The landing page becomes the project gallery. New/changed files under `apps/web/src/`, extending `ScaffolderApiClient` (`client.ts`), `types.ts`, and `App.tsx` routes (`react-router-dom` v7), built with Fluent 2 (Principle IV):

- `App.tsx` routes: `/` -> `ProjectGalleryPage` (cards landing), `/projects/:projectId` -> `ProjectPage` (enter project; lists its runs via `GET /api/projects/{id}/runs`, plus a start-run dialog), `/projects/:projectId/settings` -> `ProjectSettingsPage` (provider/model defaults), with the existing run watch reachable from within a project. A header "Sign in with GitHub" control (`GitHubSignIn.tsx`) is reachable from every page.
- `ProjectGalleryPage`: one Fluent `Card` per project (name + origin badge + availability state), an empty state that still offers both create actions (FR-010), and "Create blank" / "Create from repository" dialogs.
- `ProjectPage`: lists the project's runs (newest first) and a "Start run" dialog that pre-selects the project default provider/model but allows a per-run provider/model override (A, FR-015) and optional base branch (B); on a 409 `model_unavailable` it shows the returned available-model list so the user can re-pick (spec edge case "default model no longer available").
- `GitHubSignIn`: shows the device `user_code` and `verification_uri`, opens the verification URL, and polls status; reflects signed-in login.
- `client.ts` gains `listProjects`, `getProject`, `createProject`, `renameProject`, `updateProjectProviderSettings`, `relinkProject`, `deleteProject`, `startProjectRun`, `listProjectRuns`, `startGitHubDeviceFlow`, `pollGitHubAuth`, `getGitHubAuthStatus`, `signOutGitHub`. `types.ts` gains `Project`, `ProjectOrigin`, `ProjectProviderSettings`, `CreateProjectRequest`, `CreateProjectRunRequest`, `ProjectRunSummary`, `GitHubDeviceFlow`, `GitHubAuthStatus`.

### 4.7 Documentation updates (Principle IV parity, documented capabilities)

- `docs/reference/api.md`: add a "Projects" endpoint section, a "GitHub authentication" section, and new configuration keys (`Auth:GitHub:ClientId`, `Auth:GitHub:BaseUrl`, `Auth:GitHub:TokenStore`, `Workspace:Provider`) under "Configuration keys".
- `docs/reference/cli.md`: add the `scaffolder project ...` and `scaffolder github ...` commands.
- `docs/reference/web.md`: document the gallery route, project detail/settings routes, and the sign-in flow.
- `docs/reference/events.md`: document the new `run.cancelled` event and the fact that deleting a project cancels in-flight runs to a visible terminal state.

---

## 5. Constitution Compliance

| Principle | Obligation | How Satisfied |
|---|---|---|
| I -- Agent Runtime | MAF agent loop only | No runner/agent-loop change; projects only supply working dir + provider/model defaults to existing MAF runners |
| II -- Model Sources | Exactly two providers | Reuse `ModelSource` enum; `ProjectProviderSettings.DefaultProvider` validated via `ModelSourceExtensions.FromApiString`; any third provider rejected (FR-013) |
| III -- API-First | API authoritative | Every project capability is a `/api/projects` or `/api/auth/github` endpoint; CLI/Web hold no project logic (FR-020) |
| IV -- Two Front-Ends at Parity | CLI and Web equal | Create-blank, create-from-repo, list, configure, rename, relink, delete, sign-in are all reachable identically from CLI and Web over the API (FR-021, US5) |
| V -- Observable Runs | Stream steps live | Run streaming unchanged; project-delete cancellation emits `run.cancelled` into the run stream/audit log |
| VI -- Deployment Parity | Same build local+cloud | `IProjectWorkspaceProvider` (local filesystem + persistent-volume) keeps the working-directory=boundary model identical via a full resolve/ensure/availability/release lifecycle with an explicit infra boundary; token store/auth keyed by `GitHubTokenScope` so identical contracts run local and cloud; FR-025 resolved (section 3.3, 3.5) |
| VII -- No Mocks/Fakes/Placeholders | Functional from commit one | Real device-flow auth, real OS/secret token store, real `git init` + initial commit / `git clone`; the cloud workspace provider is a fully specified, testable seam (path mapping, mount validation, availability, release) with only physical volume allocation left to the deployment; no placeholder auth |
| VIII -- No Emojis | None in product | No emojis in any code, DTO, CLI string, Web string, doc, or commit produced by this plan |
| IX -- Responsible AI | Privacy + accountability | Tokens/secrets never on the project record, in SQLite, in logs, or in telemetry; redaction is extended beyond sandbox output to clone/auth exception logging AND API error responses (FR-016, FR-023, section 11 item 6); a named owner is recorded for every project (FR-024) |
| X -- Safe Execution | Sandbox boundary + human gate | Project working dir is the run boundary (FR-022); delete is human-confirmed and uses a transactional gate that cancels in-flight runs to a terminal state before record removal (FR-019); runs blocked on invalid/unavailable boundary or a project being deleted (FR-026) |
| XI -- Agent Governance Toolkit | Governance/telemetry centralized | Token redaction reuses and extends the central `SandboxOutputRedactor` pipeline (now also covering clone/auth logs and API responses); the permitted-provider allowlist stays in the shared governance layer, not in clients |

### Complexity Tracking

| Added Complexity | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| New `projects` table + `SqliteProjectStore` | Project identity needs a persistence home distinct from per-repo `.scaffolder/settings.yml` (FR-002) | Storing identity in the per-repo YAML couples a project to a file inside its own working dir, breaks listing when the dir is unavailable (FR-026), and conflicts with the sandbox-only YAML convention |
| `IProjectWorkspaceProvider` seam | FR-025 deployment parity; keep boundary model identical local and cloud (Principle VI) | Hardcoding local paths precludes hosted-cloud execution and violates Principle VI |
| GitHub device-flow + `IGitHubTokenStore` | FR-005/FR-016 are net-new; no OAuth exists today | Reusing the bearer API key cannot grant repo clone or authorize Copilot; a Copilot API key contradicts FR-005 |
| Copilot authorization swap in `GitHubCopilotClientFactory` | FR-005/FR-013: sign-in authorizes Copilot in place of a key | A separate stored Copilot key violates FR-005/FR-016 and SC-010 |
| `run.cancelled` event (no new `RunStatus`) | FR-019 requires a visible terminal state for cancelled runs | A new `Cancelled` status ripples through `RunStatusExtensions`, the web union, and migrations for marginal benefit; `Failed` + reason + event is sufficient and visible |
| `runs.project_id` column | Associate a run with its project boundary (FR-022) | Inferring the project purely from `repository_path` is fragile when two projects ever share a path or after relink (FR-026) |
| `runs.model_id` column + `string? ModelId` threaded through Run/AgentTurnInput/IAgentRunner/dispatcher/factories | Per-run model selection (FR-014/FR-015, SC-004): runs today carry only a provider, never a model id; the id must reach the SDK (`SessionConfig.Model` / Foundry deployment) | Reusing only `ModelSource` cannot express which model; encoding the model inside the provider string would overload an enum and break `FromApiString` validation |
| `projects.state` (Active/Deleting) + `TryBeginDeleteAsync` CAS gate + atomic `TryCreateProjectRunAsync` reservation | FR-019 delete must be race-safe: no new run may start once deletion begins, and in-flight runs must reach a visible terminal state first | A non-transactional "list then cancel then delete" - OR a run-create that merely READS `project.state` before inserting - leaves a TOCTOU window where a request that read `Active` still inserts/starts a run after the flip, orphaning a live run; making the run-row insert conditional on `state = 'active'` in the SAME transaction, with the state flip as the serialization point, is what actually closes it |
| `IGitHubTokenStore`/`IGitHubAuthService` keyed by `GitHubTokenScope` (+ `IGitHubTokenScopeProvider`) | FR-016/Principle VI: a shared cloud deployment must isolate each caller's credentials; identical contract, only the key scope differs | Global mutable token state lets one caller read or clobber another's token and races sign-out across callers (token-bleed) |
| Tri-state token entry (SignedIn/SignedOut/NeverSignedIn) | FR-005: sign-out must fail closed, but a never-signed-in non-interactive install may still use a config token; the two cases are indistinguishable with a nullable token | A simple `string?` cannot tell "explicitly signed out" from "never signed in", forcing either a silent fallback after sign-out (contradicts FR-005) or no fallback at all (breaks non-interactive installs) |

---

## 6. Project Structure

### Documentation (this feature)

```text
specs/003-projects/
+-- spec.md                     # Feature specification (FR-025 closed by this change)
+-- plan.md                     # This file
+-- checklists/
    +-- requirements.md         # Updated to 16/16
```

### Source Code

```text
packages/Scaffolder.Domain/
+-- Project.cs                          # NEW: Project record
+-- ProjectId.cs                        # NEW: strongly-typed id (mirrors RunId)
+-- ProjectOrigin.cs                    # NEW: ProjectOriginKind + ProjectOrigin
+-- ProjectProviderSettings.cs          # NEW: default provider + per-provider default model
+-- IProjectStore.cs                    # NEW: persistence abstraction
+-- IProjectWorkspaceProvider.cs        # NEW: FR-025 storage seam
+-- IGitHubAuthService.cs               # NEW: device-flow sign-in contract (scope-keyed)
+-- IGitHubTokenStore.cs                # NEW: tenancy-keyed token store (+ GitHubToken, GitHubIdentity, GitHubTokenScope, GitHubTokenEntry/Status)
+-- IGitHubTokenScopeProvider.cs        # NEW: resolves the active GitHubTokenScope (installation local / per-caller cloud)
+-- Run.cs                              # MODIFIED: add ProjectId? ProjectId and string? ModelId
+-- EventTypes.cs                       # MODIFIED: add RunCancelled = "run.cancelled"

apps/Scaffolder.Api/
+-- Program.cs                          # MODIFIED: DI registrations (token store/scope before AddAgentRuntime) + /api/projects + /api/auth/github endpoints
+-- Projects/
|   +-- ProjectService.cs               # NEW: create (with compensation scope) / rename / configure / relink / race-safe delete
|   +-- ProjectView.cs                  # NEW: read model with Available flag
+-- Auth/
|   +-- GitHubDeviceFlowAuthService.cs  # NEW: real device flow (scope-keyed)
|   +-- FixedInstallationScopeProvider.cs          # NEW: local scope provider (installation)
|   +-- CallerTokenScopeProvider.cs                # NEW: cloud scope provider (per-caller)
|   +-- OsCredentialStoreGitHubTokenStore.cs       # NEW: local secret store (tri-state, scope-keyed)
|   +-- EncryptedSecretStoreGitHubTokenStore.cs    # NEW: cloud secret store (tri-state, per-tenant)
+-- Infrastructure/
|   +-- SqliteDb.cs                     # MODIFIED: projects DDL (state, default_branch) + runs.project_id + runs.model_id migrations
|   +-- SqliteProjectStore.cs           # NEW: store + TryBeginDeleteAsync gate (mirrors SqliteRunStore)
|   +-- SqliteRunStore.cs               # MODIFIED: persist/read project_id + model_id; GetRunsByProjectAsync + GetRunsByProjectAndStatusesAsync
|   +-- LocalFilesystemWorkspaceProvider.cs        # NEW (default; ensure/resolve/available/release)
|   +-- PersistentVolumeWorkspaceProvider.cs       # NEW (cloud seam; mount validation/mapping/release)
+-- Contracts/
|   +-- Dtos.cs                         # MODIFIED: project + github + project-run (model_id) DTOs
+-- Git/
|   +-- ProjectGitInitializer.cs        # NEW: LibGit2Sharp Init + initial empty commit on default branch / Clone with ephemeral credentials

packages/Scaffolder.AgentRuntime/
+-- Providers/GitHubCopilotClientFactory.cs        # MODIFIED: async CreateClientAsync(scope, modelId, ct); tri-state fail-closed token; SessionConfig.Model
+-- Providers/FoundryClientFactory.cs              # MODIFIED: CreateChatClient(string? deploymentOverride) for per-run model
+-- Workflow/WorkflowMessages.cs                   # MODIFIED: AgentTurnInput gains string? ModelId + string SubmittingUser (owner for token scope)
+-- Workflow/AgentTurnExecutor.cs                  # MODIFIED: pass ModelId + owner scope into dispatcher
+-- AgentRunnerDispatcher.cs / IAgentRunner.cs     # MODIFIED: thread string? modelId to both runners
+-- Providers/GitHubCopilotAgentRunner.cs / FoundryAgentRunner.cs # MODIFIED: apply per-run model id

apps/Scaffolder.Cli/
+-- Program.cs                          # MODIFIED: project + github top commands (dispatch guard extended)
+-- ProjectCommands.cs                  # NEW (incl. project run / project runs parity)
+-- GitHubAuthCommands.cs               # NEW
+-- ApiClient.cs                        # MODIFIED: project + github + project-run methods
+-- Models.cs                           # MODIFIED: project + github DTOs

apps/web/src/
+-- App.tsx                             # MODIFIED: gallery + project + settings routes
+-- pages/
|   +-- ProjectGalleryPage.tsx          # NEW: cards landing + empty state + create dialogs
|   +-- ProjectPage.tsx                 # NEW: open/enter a project; run list + start-run dialog (model override)
|   +-- ProjectSettingsPage.tsx         # NEW: provider/model defaults
+-- components/
|   +-- GitHubSignIn.tsx                # NEW: device-flow sign-in control
+-- api/
    +-- client.ts                       # MODIFIED: project + github + project-run methods
    +-- types.ts                        # MODIFIED: project + github + project-run types

docs/reference/
+-- api.md, cli.md, web.md, events.md   # MODIFIED: projects + github auth + run.cancelled + model selection

tests/Scaffolder.Tests/
+-- Helpers/ProjectsWebApplicationFactory.cs       # NEW (mirrors ScaffolderWebApplicationFactory)
+-- Projects/
    +-- SqliteProjectStoreTests.cs                 # incl. TryBeginDeleteAsync CAS gate, GetRunsByProjectAndStatusesAsync
    +-- ProjectServiceCreateTests.cs               # blank (initial commit) + from-repo + empty-dir rule + owner resolution + rollback after FS-success/DB-fail (E)
    +-- ProjectServiceDeleteTests.cs               # confirm + gate + cancel-in-flight to terminal + record-only
    +-- ProjectDeleteConcurrencyTests.cs           # delete races run-create; no orphaned live run (C)
    +-- ProjectServiceRelinkTests.cs               # FR-026: accepts moved non-empty dir; rejects mismatched/empty-invalid (D)
    +-- ProjectBlankRunTests.cs                    # freshly created blank project can start a run (B)
    +-- ProjectRunModelTests.cs                    # per-run model override + project-default resolution + 409 model_unavailable (A)
    +-- WorkspaceProviderTests.cs                  # resolve/ensure/available/release; mount validation
    +-- ProjectEndpointsTests.cs                   # CRUD + parity over WebApplicationFactory
    +-- GitHubDeviceFlowTests.cs                   # state machine (configurable base URL); live gated
    +-- GitHubTokenStoreTests.cs                   # tri-state + per-scope isolation (G)
    +-- CopilotSignOutFailClosedTests.cs           # after sign-out, NO config fallback (H)
    +-- TokenRedactionTests.cs                     # zero secret occurrences in logs + API responses (SC-006, section 11 item 6)
```

---

## 7. Phased Task Breakdown

### Phase 0: Domain and Persistence

| ID | Task | Description | FRs |
|---|---|---|---|
| T001 | Add domain types | `ProjectId`, `ProjectOrigin`, `ProjectProviderSettings`, `ProjectState`, `Project` (incl. `DefaultBranch`, `State`) in `Scaffolder.Domain` per section 3.1 | FR-001, FR-012, FR-024 |
| T002 | Add store + workspace + auth interfaces | `IProjectStore` (incl. `TryBeginDeleteAsync`), `IProjectWorkspaceProvider` (resolve/ensure/available/release), `IGitHubAuthService`, `IGitHubTokenStore` + `IGitHubTokenScopeProvider` (+ `GitHubTokenScope`, tri-state `GitHubTokenEntry`, token/identity records) | FR-002, FR-005, FR-016, FR-019, FR-025 |
| T003 | Schema + migrations | Add `projects` DDL (incl. `state`, `default_branch`) + index to `SqliteDb.SchemaSql`; add `runs.project_id`, `runs.model_id`, and defensive `projects.state`/`default_branch` via `TryAlterAsync` | FR-002, FR-014, FR-019, FR-022 |
| T004 | `SqliteProjectStore` + run-store extensions | CRUD mirroring `SqliteRunStore` + `TryBeginDeleteAsync` CAS gate; extend `SqliteRunStore` insert/read for `project_id` + `model_id`; add `GetRunsByProjectAsync` + `GetRunsByProjectAndStatusesAsync` + the atomic `TryCreateProjectRunAsync` (conditional insert guarded by project `state = 'active'`, C); add `ProjectId? ProjectId` and `string? ModelId` to `Run` | FR-002, FR-008, FR-014, FR-017, FR-018, FR-019 |

### Phase 1: ProjectService, git, workspace abstraction

| ID | Task | Description | FRs |
|---|---|---|---|
| T005 | Workspace providers (full lifecycle) | `LocalFilesystemWorkspaceProvider` (default) + `PersistentVolumeWorkspaceProvider` (cloud seam): `ResolveWorkingDirectoryAsync`, `EnsureWorkspaceAsync` (mount validation), `IsAvailable`, `ReleaseAsync`; config-selected; documented infra boundary (F) | FR-006, FR-025, FR-026 |
| T006 | `ProjectGitInitializer` (blank initial commit + clone) | LibGit2Sharp `Repository.Init` PLUS an initial empty commit on the configured default branch so a blank project is immediately runnable (B); `Repository.Clone` with ephemeral credentials; derive `DefaultBranch` from HEAD | FR-003, FR-004, FR-007, FR-023 |
| T007 | `ProjectService` core + creation rollback | Create-blank, create-from-repo, empty-or-creatable rule, owner resolution, rename, configure; wrap every create path in a `CreationScope` that tracks app-created vs pre-existing-empty and compensates on ANY failure incl. post-FS DB-insert failure, deleting only app-created artifacts (E) | FR-001, FR-003, FR-004, FR-007, FR-012-FR-018, FR-024 |
| T007b | Relink validation (separate from creation) | `RelinkAsync`: reuse `RepositoryRootValidator` (canonical/safe) WITHOUT the empty-dir rule; accept a moved/restored non-empty dir; require a valid git repo and, where determinable, matching origin; re-derive `DefaultBranch` (D) | FR-026 |
| T008 | Race-safe delete with run cancellation | Confirm-gated delete using `TryBeginDeleteAsync` (Active->Deleting CAS) to block new runs; enumerate non-terminal runs via `GetRunsByProjectAndStatusesAsync` (incl. any run whose atomic reservation committed just before the flip); per run `Abandon` + authoritative `TrySetTerminalStatusAsync(Failed,"cancelled: project deleted")` + `run.cancelled`; re-query/bounded-retry to verify terminal; `ReleaseAsync` + record-only removal (C) | FR-019 |

**Phase 1 dependency note (clone vs auth).** Phase 1's from-GitHub clone path (T006/T007) consumes GitHub tokens, but the concrete token store and device-flow auth are not built until Phase 2. Phase 1 therefore codes strictly against the `IGitHubTokenStore` interface (defined in Phase 0, T002) and is exercised with an in-memory test fake; the concrete `OsCredentialStoreGitHubTokenStore` / `EncryptedSecretStoreGitHubTokenStore` and `GitHubDeviceFlowAuthService` land in Phase 2 (T009/T010). This keeps the phase dependency ordering coherent - interface-first in Phase 0, consumed behind the seam in Phase 1, concretely implemented in Phase 2 - with no forward dependency on unimplemented code.

### Phase 2: GitHub OAuth device flow + token store + Copilot authorization swap

| ID | Task | Description | FRs |
|---|---|---|---|
| T008a | OAuth grant validation spike (FIRST) | Early spike (section 3.8 I): pin scopes to `repo` + `read:user`; verify one device-flow token authorizes BOTH private clone and Copilot (`CopilotClientOptions.GitHubToken`); if not, adopt the documented fallback (clone via token + Copilot via SDK `UseLoggedInUser`), still one sign-in gesture. Record the chosen outcome before building on it | FR-005 |
| T009 | Token store implementations (tenancy-keyed, tri-state) | `OsCredentialStoreGitHubTokenStore` (local) + `EncryptedSecretStoreGitHubTokenStore` (cloud), each keyed by `GitHubTokenScope` with SignedIn/SignedOut(tombstone)/NeverSignedIn semantics; never plaintext, never on project record; `IGitHubTokenScopeProvider` (installation local / per-caller cloud) (G) | FR-016, FR-023 |
| T010 | `GitHubDeviceFlowAuthService` | Real device flow with the pinned minimal scopes; server-held device code per scope; lifecycle (refresh, expiry, revocation); `SignOutAsync` purges token + writes SignedOut tombstone | FR-005 |
| T011 | Copilot authorization swap (async, fail-closed) + per-run model | `GitHubCopilotClientFactory` -> async `CreateClientAsync(scope, modelId, ct)`: SignedIn token used, SignedOut fails closed (NO config fallback), NeverSignedIn may use config token (H); apply per-run model via `SessionConfig.Model`; `FoundryClientFactory.CreateChatClient(deploymentOverride)`; thread `string? ModelId` + owner scope through `AgentTurnInput`/`AgentTurnExecutor`/`IAgentRunner`/dispatcher (A); Foundry has no GitHub token path | FR-005, FR-013, FR-014, FR-015, FR-016 |
| T012 | Token redaction (extended) | Register GitHub token patterns in the central `SandboxOutputRedactor` and extend redaction beyond sandbox output to clone/auth EXCEPTION logging AND API error responses (section 11 item 6) | FR-005, FR-016, FR-023 |

### Phase 3: API endpoints

| ID | Task | Description | FRs |
|---|---|---|---|
| T013 | Project endpoints | `POST/GET/PATCH/PUT/DELETE` per section 4.1 incl. `GET /api/projects/{id}/runs`; DTOs in `Dtos.cs`; owner fallback from `CallerContext.User`; relink + delete map to T007b/T008 | FR-001, FR-007-FR-019, FR-020, FR-024, FR-026 |
| T014 | GitHub auth endpoints | `POST /api/auth/github/device`, `POST /poll`, `GET`, `POST /sign-out`; scope from `CallerContext.User` | FR-005 |
| T015 | Run-in-project endpoint (model + gate) | `POST /api/projects/{id}/runs`: reject when `Deleting` (409) or unavailable (FR-026); resolve provider+model (explicit -> project default -> runtime default); validate model and return 409 `model_unavailable` with the available list (spec edge case); inject working dir + `project_id` + resolved `model_id` + base branch from `DefaultBranch` (A, B); ATOMICALLY reserve the run via `TryCreateProjectRunAsync` (Pending, guarded by project Active) BEFORE worktree/workflow side effects, returning 409 if the project moved to `Deleting`; on ANY post-reservation start failure, compensate by terminalizing the reserved run to `Failed` (`TrySetTerminalStatusAsync`), emitting the run-failed event (`run.failed`), and undoing the partial worktree/registry/stream entries in reverse order (`CreationScope` idiom, section 3.4 / issue E) so a reservation never leaks as a stuck `Pending` (C) | FR-014, FR-015, FR-019, FR-022, FR-026 |
| T016 | DI wiring + `run.cancelled` | Register stores/providers/services/scope provider BEFORE `AddAgentRuntime`; add `EventTypes.RunCancelled` | FR-016, FR-019, FR-025 |

### Phase 4: CLI parity

| ID | Task | Description | FRs |
|---|---|---|---|
| T017 | `ProjectCommands` + ApiClient | `project create/list/show/configure/rename/relink/delete` PLUS `project run` (per-run provider/model/base-branch override) and `project runs` (list) for CLI/Web parity; extend the dispatch guard with `project`/`github`; HTTP-only | FR-001, FR-008, FR-011, FR-014, FR-015, FR-017-FR-019, FR-021 |
| T018 | `GitHubAuthCommands` | `github sign-in/sign-out/status`; render device code + poll | FR-005, FR-021 |

### Phase 5: Web parity

| ID | Task | Description | FRs |
|---|---|---|---|
| T019 | Gallery landing | `ProjectGalleryPage` cards + empty state + create-blank/create-from-repo dialogs; routes in `App.tsx` | FR-008, FR-009, FR-010, FR-011, FR-021 |
| T020 | Project detail/settings | `ProjectPage` (run list via `GET /{id}/runs` + start-run dialog with per-run provider/model override and 409 `model_unavailable` re-pick) + `ProjectSettingsPage` (two-provider constraint enforced in UI from API) | FR-009, FR-012-FR-018, FR-021 |
| T021 | Sign-in component + client/types | `GitHubSignIn`; extend `ScaffolderApiClient` + `types.ts` | FR-005, FR-021 |

### Phase 6: Documentation

| ID | Task | Description | FRs |
|---|---|---|---|
| T022 | Reference docs | Update `api.md`, `cli.md`, `web.md`, `events.md` per section 4.7 | FR-020, FR-021 |

### Phase 7: Tests and security review

| ID | Task | Description | FRs |
|---|---|---|---|
| T023 | Backend unit/integration | `ProjectsWebApplicationFactory`; store (+ `TryBeginDeleteAsync`, `GetRunsByProjectAndStatusesAsync`), service create/delete/relink, endpoints CRUD + parity. Named cases: blank project initial-commit then start a run (B); per-run model override + project-default resolution + 409 `model_unavailable` (A); relink accepts a moved NON-empty dir but rejects mismatched origin (D); creation rollback when filesystem succeeds then `InsertAsync` fails, asserting only app-created artifacts are removed and user content is preserved (E) | FR-001-FR-019, FR-022, FR-026 |
| T023b | Delete/run-create concurrency | `ProjectDeleteConcurrencyTests`: spawn delete and `POST /{id}/runs` concurrently and assert the run is EITHER never started (atomic `TryCreateProjectRunAsync` reservation rejected with 409 once `Deleting`) OR started-then-cancelled-to-terminal; no run remains active/orphaned after delete completes; the reservation makes the project-Active check and the run insert one transaction; cancelled runs reach a visible terminal state with `run.cancelled`; also assert that a post-reservation side-effect failure (worktree creation or workflow start throws after `TryCreateProjectRunAsync` commits) terminalizes the reserved run to `Failed`, emits `run.failed`, and cleans up the partial worktree -- leaving no leaked `Pending` (C) | FR-019 |
| T024 | Auth + redaction + fail-closed tests | Device-flow state machine (configurable base URL; live gated); token store tri-state + per-scope isolation so two callers never read/clobber each other (G); after explicit sign-out Copilot fails closed with NO config fallback (H); redaction asserts zero secret occurrences in logs AND API responses | FR-005, FR-016, FR-023, SC-006 |
| T025 | Web tests | Gallery empty state + cards, create dialogs, settings two-provider constraint, project run list + start-run dialog with model override, sign-in flow (Vitest) | FR-009, FR-010, FR-013, FR-015, FR-021 |
| T026 | Security review | Walk the 8 Security Design items; verify no token leak path, sign-out fail-closed, tenancy isolation, and Foundry separation | FR-005, FR-013, FR-016, FR-023 |

**Phase list:** 0, 1, 2, 3, 4, 5, 6, 7.

---

## 8. Requirements Coverage

| FR | Requirement (short) | Component / Phase |
|---|---|---|
| FR-001 | Create blank or from GitHub | `ProjectService` (P1) + `POST /api/projects` (P3) + clients (P4/P5) |
| FR-002 | Materialize working dir + persist record | `SqliteProjectStore` + `projects` table (P0) |
| FR-003 | Blank: empty/non-existent dir + `git init` | `ProjectGitInitializer.Init` + `EnsureEmptyOrCreatable` (P1) |
| FR-004 | From-repo: empty/non-existent dir + clone | `ProjectGitInitializer.Clone` + validation (P1) |
| FR-005 | GitHub sign-in (device flow); authorizes Copilot, not Foundry | OAuth grant spike + `GitHubDeviceFlowAuthService` + tenancy-keyed tri-state token store + fail-closed Copilot swap (P2) + endpoints/clients (P3/P4/P5) |
| FR-006 | User chooses working dir; recorded; boundary | `ProjectService` + `IProjectWorkspaceProvider` resolve/ensure (P1) |
| FR-007 | Reject empty name; clear failure; no partial project | `ProjectService` name validation + `CreationScope` rollback covering post-FS DB failure (P1) |
| FR-008 | List all projects | `IProjectStore.ListAsync` + `GET /api/projects` (P0/P3) |
| FR-009 | Landing cards: name + origin + create entry points | `ProjectGalleryPage` + `ProjectPage` run list via `GET /{id}/runs` (P5) |
| FR-010 | Empty state with both create actions | `ProjectGalleryPage` empty state (P5) |
| FR-011 | Selecting a project opens it | `GET /api/projects/{id}` + `ProjectPage` / CLI `show` (P3/P4/P5) |
| FR-012 | Store default provider + per-provider model | `ProjectProviderSettings` + store (P0/P1) |
| FR-013 | Exactly two providers; Copilot via sign-in; Foundry own creds | `ModelSource` validation + Copilot swap + Foundry untouched (P2) |
| FR-014 | Default model selectable per provider | `ProjectProviderSettings` + `PUT /provider-settings`; `runs.model_id` plumbing + resolution order (P1/P2/P3) |
| FR-015 | Defaults only; provider/model overridable per run | `POST /api/projects/{id}/runs` model resolution + per-run `model_id` override + 409 `model_unavailable` recovery (P2/P3) |
| FR-016 | Credentials global; never on project/logs/telemetry | Tenancy-keyed `IGitHubTokenStore` + extended redaction; no token columns (P2) |
| FR-017 | Rename project | `ProjectService.RenameAsync` + `PATCH /api/projects/{id}` (P1/P3) |
| FR-018 | Edit provider settings post-create | `ProjectService.UpdateProviderSettingsAsync` (P1/P3) |
| FR-019 | Delete: confirm + cancel in-flight + record-only | Atomic `TryCreateProjectRunAsync` reservation (run-create) with post-reservation start-failure compensation (reserved run terminalized to `Failed` + `run.failed`, self-healing rather than only swept on delete) + `TryBeginDeleteAsync` gate + `Abandon`+`TrySetTerminalStatusAsync` + verify-requery + `run.cancelled` (P1/P3) |
| FR-020 | All capabilities API-first | `/api/projects` (incl. `/{id}/runs`) + `/api/auth/github` (P3) |
| FR-021 | CLI and Web parity | `ProjectCommands` (incl. `project run`/`project runs`)/`GitHubAuthCommands` (P4) + Web pages (P5) |
| FR-022 | Project dir is run boundary; escapes rejected | `repository_path = WorkingDirectory` + base branch `DefaultBranch` + existing sandbox (P3) |
| FR-023 | Clone creds never leaked | Ephemeral credentials provider + extended redaction of clone/auth logs + API responses (P1/P2) |
| FR-024 | Named accountable owner (GitHub else local user) | `ProjectService` owner resolution; `owner` column (P0/P1) |
| FR-025 | Local + hosted-cloud; cloud storage = managed per-project persistent volume | `IProjectWorkspaceProvider` full lifecycle (local + persistent-volume) with documented infra boundary (P1) |
| FR-026 | Missing/inaccessible dir: list-but-unavailable; block runs; relink-or-remove | `IsAvailable`/`EnsureWorkspaceAsync` + `ProjectView.Available` + relink (separate non-empty validation) + run block (P1/P3) |

All FR-001 through FR-026 are covered. (Numbering note: the spec defines FR-001..FR-024, FR-026, then FR-025; all 26 are present and mapped above.)

---

## 9. Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| One device-flow token cannot authorize BOTH private clone and Copilot | Medium | Sign-in design rework late | EARLY validation spike at Phase 2 start (T008a, section 3.8); documented fallback (clone via token + Copilot via SDK `UseLoggedInUser`) keeps a single sign-in gesture and still satisfies FR-005 |
| GitHub device-flow / OAuth app misconfiguration | Medium | Blocks clone-private + Copilot | Configurable `Auth:GitHub:ClientId`/`BaseUrl`; clear non-leaking errors; config-token fallback allowed ONLY in the NeverSignedIn state (not after sign-out) |
| Copilot entitlement absent despite valid sign-in | Medium | Confusing failure | Distinct, non-leaking "no Copilot seat" error separate from auth failure (section 11 item 4) |
| Stale project-default model no longer available | Medium | Run-create would fail opaquely | Validate model at run-create; return 409 `model_unavailable` with the available list so the client re-picks (section 3.7 A, spec edge case) |
| Token leakage in clone output/logs/telemetry | Medium | Privacy violation (FR-023) | Ephemeral credentials provider (never in URL/.git/config) + central redaction extended to clone/auth logs and API responses + zero-occurrence test |
| `GitHubCopilotClientFactory` becomes async + scope-aware | Medium | Touches the run-execution hot path | Owner threaded via `AgentTurnInput` (already carries `SubmittingUser`); call site at `GitHubCopilotAgentRunner` updated to await; covered by Phase 7 tests |
| Working dir becomes unavailable after creation | Medium | Runs would break the boundary | `IsAvailable`/`EnsureWorkspaceAsync` marks unavailable, blocks runs, offers relink-or-remove (FR-026) |
| Blank project not runnable (unborn repo) | Medium | First run fails on a fresh blank project | Blank create makes an initial empty commit on a default branch so `WorktreeManager.AddWorktree` finds a branch tip (section 3.4/3.7 B) |
| Cloud persistent-volume provisioning is environment-supplied | Medium | Cloud create depends on operator-provisioned mount | Provider is a full, testable seam (resolve/ensure/availability/release) with an explicit infra boundary; missing mount fails closed with a clear error - not a stub (section 3.3) |
| Non-empty target directory data loss on CREATE | Low | Destructive | Hard reject on non-empty/existing for creation; never overwrite or adopt; relink (not create) is the path for an existing non-empty dir (FR-003, FR-004, FR-026) |
| Delete races with an active run | Low | Orphaned run | Run-create reserves its row via the atomic `TryCreateProjectRunAsync` (insert guarded by `state = 'active'` in one transaction) and `TryBeginDeleteAsync` CAS-flips Active -> Deleting as the serialization point, so a raced run-create either commits its reserved row (then the sweep cancels it to terminal; and if that reservation's own post-reservation side effects fail first, section 4.3 compensates it to terminal `Failed` immediately rather than leaving a stuck `Pending`) or fails because state is already `Deleting`; then cancel-to-terminal (`Abandon` + `TrySetTerminalStatusAsync`) + verify-requery before record removal; concurrency test (FR-019, section 3.4 C) |
| Multi-tenant token bleed in cloud | Low | Cross-tenant access | Token store + auth keyed by `GitHubTokenScope` (per-user/per-tenant in cloud); per-scope isolation test (section 11 item 5) |

---

## 10. Rollback and Feature-Flag Strategy

- **Workspace provider**: `Workspace:Provider` selects `local` (default) or `persistent-volume`; reverting to `local` restores exact current behavior.
- **Token store + scope**: `Auth:GitHub:TokenStore` selects `os` (default) or `secret-store`; `Auth:GitHub:ScopeProvider` selects `installation` (default) or `caller`.
- **GitHub sign-in optionality and fail-closed fallback (H)**: the config token `Providers:GitHubCopilot:GitHubToken` is honored ONLY in the NeverSignedIn state (a non-interactive install that never used the device flow), so removing the OAuth app does not break those installs. After an explicit sign-out the SignedOut tombstone SUPPRESSES the config fallback and Copilot fails closed - sign-out and config-fallback are no longer contradictory. This config fallback is confined to the LOCAL / installation token scope; under the cloud `caller` scope it is disabled entirely (cloud always requires an interactive sign-in, with no installation-wide config token in a multi-tenant deployment).
- **Schema additivity**: the `projects` table (with `state`, `default_branch`) and the `runs.project_id` / `runs.model_id` columns are additive (`CREATE TABLE IF NOT EXISTS` + `TryAlterAsync`); existing runs with `project_id = NULL` / `model_id = NULL` and any project defaulting to `state = Active` continue to work.
- **Endpoint isolation**: `/api/projects` (incl. `/{id}/runs`) and `/api/auth/github` are new routes; the existing `/api/runs` surface is unchanged, so the feature can be withheld by not exposing the new routes/clients without regressing runs. The added `string? modelId` runner parameter is optional and null-safe, so the existing direct run path is unaffected.
- **Foundry untouched (behavioral)**: `FoundryClientFactory` only gains an optional `deploymentOverride` parameter defaulting to the configured deployment, so Foundry behavior cannot regress.

---

## 11. Security Design

1. **OAuth scope minimization + early grant validation (FR-005)**: request the minimal scopes for the two granted capabilities only - `repo` (clone incl. private) and `read:user` (owner login); do not request `delete_repo`, `admin:*`, or org-management scopes. GitHub Copilot authorization is conferred by the OAuth app being Copilot-enabled, not by an extra write scope. The exact Copilot grant is pinned by an EARLY validation spike at the start of Phase 2 (T008a, section 3.8), with a documented fallback (clone via the OAuth token + Copilot via the SDK `UseLoggedInUser` device authorization) that keeps a single sign-in gesture if one token cannot do both - no broadening beyond clone + identity in either outcome.
2. **Secure, tenancy-keyed token storage (FR-016)**: tokens live ONLY in the scoped token store - Windows Credential Manager / DPAPI locally (`OsCredentialStoreGitHubTokenStore`), an encrypted server-side secret store in cloud (`EncryptedSecretStoreGitHubTokenStore`), each entry keyed by `GitHubTokenScope`. Never plaintext on disk, never in SQLite, never on the project record (no token column exists).
3. **Token lifecycle + sign-out fails closed (FR-005, H)**: persist refresh token + expiry; refresh proactively before expiry and on a 401; detect mid-run expiry/revocation and surface a clear re-sign-in prompt without leaking the token. The store is tri-state per scope: SignedIn (token used), SignedOut (a tombstone marker, NO token), NeverSignedIn (no entry). `SignOutAsync` purges the token AND writes the SignedOut tombstone so subsequent Copilot calls and clones fail closed - explicitly suppressing the `Providers:GitHubCopilot:*` config fallback, which is permitted ONLY in the NeverSignedIn state. That config fallback is additionally LOCAL / non-interactive-install ONLY (the `GitHubTokenScope.Installation` scope); it is explicitly disabled for the cloud `caller` scope, where an interactive device-flow sign-in is always required and no installation-wide config token exists (multi-tenant isolation, item 5). This removes the sign-out vs config-fallback contradiction.
4. **GitHub auth is not Copilot entitlement (FR-005, FR-013)**: a valid sign-in does not guarantee a Copilot seat. The Copilot path returns a distinct, non-leaking error ("GitHub sign-in succeeded but this account has no GitHub Copilot entitlement") that is clearly separate from an authentication failure, so users are not told to re-authenticate when the real issue is entitlement.
5. **Hosted-cloud multi-tenant isolation (FR-016, Principle VI)**: every token-store and auth operation takes a `GitHubTokenScope` resolved by `IGitHubTokenScopeProvider` - a fixed installation key locally (single OS user), a per-signed-in-user/per-tenant key in cloud (from `CallerContext.User` on the API path, from the run owner threaded via `AgentTurnInput` on the background runner path). One tenant's GitHub token can therefore never satisfy another tenant's clone or Copilot call. The `IGitHubTokenStore` contract is identical across both deployments; only the backing key scope differs, eliminating the prior global-state token-bleed and sign-out race.
6. **Centralized, extended token redaction (FR-016, FR-023, Principle XI)**: reuse the existing `SandboxOutputRedactor` pipeline and register GitHub token patterns (`ghp_`, `gho_`, `ghu_`, `ghs_`, `ghr_`, and OAuth `Bearer` values) so they are scrubbed from the run step stream, audit log, and telemetry. Redaction is extended beyond sandbox output to clone/auth EXCEPTION logging AND API error responses, so a credential can never surface in a 4xx/5xx body or a logged stack trace. A test asserts zero secret occurrences across all sinks for a clone-and-run scenario (SC-006).
7. **Clone transport hygiene (FR-023)**: pass the token via LibGit2Sharp's `CredentialsProvider` (in-memory `UsernamePasswordCredentials`) for the duration of the clone only. The token is never embedded in the remote URL, never written to `.git/config`, and the configured remote uses the clean HTTPS URL. No git credential helper is persisted to disk.
8. **Foundry credential separation (FR-013, SC-010)**: `FoundryClientFactory` reads ONLY `Providers:MicrosoftFoundry:*` (plus the optional per-run deployment override) and has no dependency on `IGitHubTokenStore`; there is no code path by which a GitHub token can authorize a Foundry call. A test asserts a Foundry run with a present GitHub sign-in but absent Foundry credentials still fails for lack of Foundry credentials.

---

## 12. Open Questions

None. FR-025 is resolved (hosted-cloud project storage is a managed per-project persistent volume mounted at the project's working-directory path, behind `IProjectWorkspaceProvider`; the local-directory model is unchanged and does not preclude cloud - Principle VI). All Session 2026-06-11 clarifications are incorporated. Neither the spec nor this plan contains any unresolved-clarification markers.

Advisory note for the coordinator (not blocking, no spec marker): whether one device-flow token can authorize BOTH private clone and GitHub Copilot is validated by an early spike at the start of Phase 2 (T008a, section 3.8). The plan constrains scopes to clone + identity and defines a concrete fallback (clone via the OAuth token + Copilot via the SDK logged-in-user device authorization) that still satisfies FR-005 with a single sign-in gesture, so either spike outcome is implementable without redesign. A second item the coordinator may wish to surface: `GitHubCopilotClientFactory` necessarily becomes asynchronous and scope-aware (it reads the per-caller token at run time, not at construction), which is a deliberate, test-covered change to the run-execution path noted by the reviewers.

---

## 13. Review Resolutions

This plan was gated by two independent GPT-5.5 reviews at each round: Seraph (architecture and security) and a logic/design rubber-duck reviewer. The history below records why this revision can be committed without a further review cycle.

### Round 1 (author: Tank)

Both reviewers REJECTED. Nine blocking issues were raised (A-I):

- A: per-run model plumbing (the stored project default model could not reach a run).
- B: blank-project run startup (an unborn `git init` repo has no branch tip for `WorktreeManager.AddWorktree`).
- C: delete/run-create race (TOCTOU between the project-Active check and run-row creation).
- D: relink validation (recovery to a moved/restored, possibly non-empty, working directory).
- E: creation rollback (git init/clone succeeds, then DB persistence fails, leaving a partial project).
- F: FR-025 cloud workspace seam (deployment-parity storage abstraction, not a stub).
- G: token-store tenancy (per-caller/per-tenant key isolation, no global token bleed).
- H: sign-out vs config-fallback (sign-out must fail closed and not silently fall back to a config token).
- I: Copilot OAuth grant (whether one device-flow token authorizes both private clone and Copilot).

### Round 2 (revision: Morpheus)

Seraph APPROVED WITH CHANGES: F, G, H, I and the model-unavailable recovery were resolved. The rubber-duck reviewer resolved A, B, D, and E but REJECTED on C, citing a residual TOCTOU between the project-Active check and the run-row insert.

### Round 3 (revision: Smith)

C's TOCTOU was closed by making run reservation atomic: `TryCreateProjectRunAsync` inserts a `Pending` row guarded by the project being Active in one SQLite transaction, `TryBeginDeleteAsync` serializes the Active -> Deleting flip, and the delete sweep enumerates `Pending` runs so a reservation that won the race is cancelled rather than orphaned. The rubber-duck reviewer confirmed the race was closed but flagged the reserve-before-side-effects failure path: if a side effect fails after the reservation commits, the reserved run can leak as a stuck `Pending` row.

### Round 4 (this resolution)

The reviewer's prescribed compensation was ACCEPTED as a documented review-resolution by the project owner, with no further review cycle. On ANY failure after `TryCreateProjectRunAsync` commits and before the run is registered/observable in the watch loop, the reserved run is terminalized to `Failed` via the conditional+idempotent `TrySetTerminalStatusAsync(runId, Failed, now, "run_start_failed")`, the existing terminal run-failed event (`EventTypes.RunFailed` = `run.failed`) is emitted, and any partially created worktree/workflow-registry/stream artifacts are undone in reverse order of creation (the `CreationScope` idiom from section 3.4 / issue E). The reserved `Pending` row is therefore self-healing on start failure rather than reliant on a later delete sweep. This is incorporated in sections 3.2, 4.3, and 7 (T015, T023b) and reflected in sections 8 (FR-019) and 9.

Two accepted NON-BLOCKING polish items were already folded in: (1) the `Providers:GitHubCopilot:*` config-token fallback is honored ONLY in the NeverSignedIn state, is local / non-interactive (the `GitHubTokenScope.Installation` scope) only, and is disabled entirely for the cloud `caller` scope, while the background runner's token lookup is keyed by the resolved `GitHubTokenScope` (sections 3.5, 3.6, 10, 11); and (2) Phase 1 codes interface-first against `IGitHubTokenStore`, with the concrete token store and device-flow auth delivered in Phase 2, keeping the phase dependency ordering coherent (section 7, Phase 1 dependency note).
