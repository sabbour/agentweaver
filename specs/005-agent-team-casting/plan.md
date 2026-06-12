# Implementation Plan: Agent Team Casting

**Branch**: `005-agent-team-casting` | **Date**: 2026-06-12T09:21:35-07:00 | **Spec**: `specs/005-agent-team-casting/spec.md`

**Input**: Feature specification from `specs/005-agent-team-casting/spec.md` (FR-001..FR-034, five user stories, edge cases, key entities, success criteria SC-001..SC-007, and the Session 2026-06-12 clarifications); the Scaffolder Constitution v1.4.0; feature 003 (`specs/003-projects/spec.md` and `plan.md`) which this builds on; and the squadboard reuse digest (`squadboard-casting-reuse.md`).

---

## 1. Summary and Approach

Casting assembles a team of specialized agents for a project (feature 003) and stores the team entirely inside the project's working directory under `.squad/`, following the established Squad convention already used by this repository: a roster in `team.md`, one charter per member at `.squad/agents/{name}/charter.md`, and casting bookkeeping under `.squad/casting/` (`policy.json`, `registry.json`, `history.json`). A team is therefore just files in the project's git working tree; it persists, travels with the repo, and is shared on clone.

A user casts a team three ways: (1) pick a well-known scenario grouping and get a deterministic, no-model team (US1, P1); (2) describe a goal in free text and have a model propose roles (US2, P2); (3) point the system at the existing project so it analyzes the working directory and proposes roles justified by detected signals (US3, P3). All three follow one discovery -> proposal -> confirmation -> creation flow: nothing is written under `.squad/` until the user confirms (FR-005, FR-007). Beyond casting, the user reads and edits charters, adds/removes/re-roles members (US4, P2), and syncs by an explicit, user-gated git commit of `.squad/` changes (US5, P3).

The chosen approach: a new `packages/Scaffolder.Squad` library holds host-free casting primitives (catalog reader, `.squad/` reader/writer, naming-universe allocator, charter compiler, git scribe). An `apps/Scaffolder.Api/Casting/CastingService` orchestrates them and is the single source of truth (Principle III); CLI and Web are thin over a new `/api/projects/{id}/casting` and `/api/projects/{id}/team` surface (Principle IV). Model-assisted modes (US2, US3) run on MAF through `Scaffolder.AgentRuntime` (Principle I — build on the framework, do not reimplement the loop), but through a DEDICATED read-only proposal-generation run mode (section 10.1): a no-write/no-shell tool policy, no worktree-commit step, and persisted/streamed run events. Proposal generation can therefore never mutate the working directory; every `.squad/` write happens exclusively in the `confirm` operation after the human gate (FR-005, FR-007). The run uses GitHub Copilot - the single, fixed provider (Principle II, FR-032) - with the relevant role's default model, overridable per run via an optional model id (Principles I, II, V; FR-033, FR-034); no parallel runtime, no provider selection, no new model source. All file access is rooted at the project's working directory through `Scaffolder.SandboxFs` (Principle X, FR-029). The app-bundled role/scenario catalog ships as embedded resources, kept schema-compatible with the Squad catalog, and is never written into a project (FR-011).

The design reuses, and does not duplicate, what feature 003 already established:

- A team is always cast inside an existing `Project`; the project's `WorkingDirectory` is the operation boundary, and `IProjectWorkspaceProvider` / `ProjectView.Available` provide the availability check and the relink-or-remove remedy (FR-031) - this plan adds no new project-unavailability handling, it consumes 003's.
- The per-run `string? ModelId` plumbing from 003 (section 3.7 of `003-projects/plan.md`) is exactly what model-assisted casting consumes for the optional runtime model override (FR-008, FR-034); GitHub Copilot is the single, fixed provider, is never selectable, and no new model source is introduced (Principle II, FR-032).
- Git operations reuse `LibGit2Sharp` (already referenced via `apps/Scaffolder.Api/Git/ProjectGitInitializer`).

**Approach in one line:** new `Scaffolder.Squad` package (catalog + `.squad` reader/writer + universe allocator + charter compiler + git scribe) -> `CastingService` over the project workspace -> deterministic scenario casting (US1) -> read/edit/add/remove/re-role (US4) -> model-assisted free-text (US2) and analysis (US3) casting as observable, read-only MAF proposal runs on the fixed GitHub Copilot provider with a per-run model override over each role's default model -> user-gated git sync of `.squad/` with a reviewed-change token and merge-safe append-only state (US5) -> `/api/projects/{id}/casting` + `/team` endpoints -> CLI parity -> Web parity -> docs -> tests + security/RAI review.

---

## 2. Technical Context

**Language/Version**: C# / .NET 10 (`net10.0`) for all backend, library, CLI, and domain code; TypeScript + React 19 (Fluent 2) for the Web UI. Edited `.ts`/`.tsx` files are normalized to LF on Windows.

**Primary Dependencies**: ASP.NET Core minimal APIs (`apps/Scaffolder.Api/Program.cs`); the Microsoft Agent Framework runners reused via `Scaffolder.AgentRuntime` (`IAgentRunner` -> `AgentRunnerDispatcher` -> `GitHubCopilotAgentRunner`, the fixed GitHub Copilot provider) for model-assisted casting, invoked through a dedicated read-only proposal-generation run mode that bypasses the standard `AgentTurnExecutor` worktree-commit path (section 10.1, Principles I, V); `LibGit2Sharp` v0.31.0 (already referenced) for sync staging/commit and `.squad/.gitattributes` setup; `System.Text.Json` for casting JSON; `Scaffolder.SandboxFs` (`SandboxPathValidator`) for the working-directory boundary; Spectre.Console (CLI); React 19.2 + `@fluentui/react-components` v9 + `react-router-dom` v7 (Web).

**Storage**: The team is files inside the project's working directory under `.squad/` (roster `team.md`, charters `agents/{name}/charter.md`, bookkeeping `casting/policy.json`, `casting/registry.json`, `casting/history.json`) - the single source of team state (FR-001, FR-012). No new app database table is required: casting reads/writes the project's `.squad/` on demand and identifies the project through 003's existing `projects` table and `ProjectId`. The app-bundled role/scenario catalog lives as embedded resources compiled into the `Scaffolder.Squad` assembly (section 8); it is never persisted into a project.

**Model selection**: GitHub Copilot is the single, fixed model provider for all model-assisted casting; the provider is never selectable per run, per role, or per project, and no provider control is exposed (Principle II, FR-032). What varies is the MODEL within GitHub Copilot: casting assigns each role a DEFAULT model recorded in its role definition, charter, and casting registry (FR-033), and that default is overridable at runtime. Each `free_text` and `analysis` proposal request carries an optional `model_id`; when present it OVERRIDES the role/agent default model for that run, when absent the default applies. Resolution order: request `model_id` override -> role/agent default model -> system default model. The effective model is observable in the run's steps (Principle V, FR-034). The model override is exposed at full parity in CLI (`--model`) and Web (a model picker in the casting wizard); the provider is not exposed because there is nothing to choose (Principle IV). Scenario casting (US1) makes no model call and exposes no model control (FR-009).

**Testing**: xUnit in `tests/Scaffolder.Tests` with `WebApplicationFactory<Program>` (a new `CastingWebApplicationFactory`, mirroring the existing `ProjectsWebApplicationFactory` under `tests/Scaffolder.Tests/Projects`); real on-disk fixtures - temp git repos initialized with LibGit2Sharp and sample projects carrying real detectable signals (a `package.json` with a web framework, a test directory). Model-assisted casting tests run against the real GitHub Copilot provider gated behind an environment variable, mirroring 003's `GITHUB_INTEGRATION_TESTS` gating; the deterministic proposal-shaping logic is fully unit-tested without a model. Web tests via Vitest + Testing Library. No mocks of the casting logic, catalog, allocator, or git layer (Principle VII).

Verified build/test commands: `dotnet build scaffolders.sln` and `dotnet test scaffolders.sln` (the test project is `tests/Scaffolder.Tests/Scaffolder.Tests.csproj`); Web `cd apps/web && npm run lint && npm run build && npx vitest run`.

**Target Platform**: Windows developer machine (primary) and hosted-cloud Linux service (same build) per Principle VI. No environment-specific code path; the deployment difference for the working directory is already isolated behind 003's `IProjectWorkspaceProvider`.

**Project Type**: Web application (authoritative ASP.NET Core API) with two thin clients (CLI, Web UI). The CLI and Web contain NO casting business logic; they call the API (Principles III, IV).

**Constraints**: GitHub Copilot is the single, fixed provider, never selectable (Principle II, FR-032), with each role's default model overridable per run (FR-033, FR-034); every capability API-first and reachable identically from CLI and Web (Principles III, IV, FR-028); all casting/analysis/edit/sync file access stays inside the project working directory (Principle X, FR-029); model-assisted runs are observable, bounded, and human-confirmed before any write (Principles V, X, FR-005, FR-008); sync never auto-commits and never touches files outside `.squad/` (FR-024, FR-025); RAI checks gate generated/edited content (Principle IX, FR-030); no emojis anywhere (Principle VIII).

---

## 3. Constitution Check

| Principle | Obligation | How this plan satisfies it |
|---|---|---|
| I -- Agent Runtime (MAF) | Build on MAF; no parallel runtime | Model-assisted casting (US2/US3) runs through MAF via the existing `IAgentRunner` in `Scaffolder.AgentRuntime`, but through a dedicated read-only proposal-generation run mode (section 10.1) that applies a no-write/no-shell tool allowlist and omits the standard `AgentTurnExecutor` worktree-commit step, so proposal generation is provably write-safe. The casting service shapes the task and consumes the run; it does not implement a new loop (FR-008). Scenario casting needs no model (FR-009). |
| II -- Model Sources | GitHub Copilot only, fixed provider | GitHub Copilot is the single, fixed model provider for all model-assisted casting and later agent execution; the provider is never selectable per run, per role, or per project, and no provider control is exposed (FR-032). The MODEL within GitHub Copilot may vary: each role carries a default model (FR-033) overridable per run via an optional `model_id`, resolved request override -> role/agent default -> system default; the effective model is observable in the run steps (FR-008, FR-034, Principle V). No new model source. |
| III -- API-First | Backend authoritative | Every casting and team-management capability is a `/api/projects/{id}/casting` or `/api/projects/{id}/team` endpoint (section 6); the catalog, allocator, charter compiler, RAI gate, and git scribe live server-side only (FR-028). |
| IV -- Two Front-Ends at Parity | CLI and Web equal | List scenarios, propose (3 modes, with a model-override picker for model-assisted modes; no provider control), amend, confirm, read team, read/edit charter, add/remove/re-role, detect-existing augment/recast, and sync are all reachable identically from CLI (`scaffolder team ...`) and Web (`TeamPage`/casting wizard) over the API (FR-028, SC-006). |
| V -- Observable Runs | Stream steps live | Free-text and analysis proposals execute as observable runs in the read-only proposal run mode (section 10.1) whose agent messages, tool calls, and results are persisted and stream like any other run (FR-008, US2 scenario 2). |
| VI -- Deployment Parity | Same build local + cloud | Team files live under the project working directory, which is identical across deployments via 003's `IProjectWorkspaceProvider`; git sync uses in-process LibGit2Sharp (no reliance on a `git` binary on PATH). No environment branch in casting code. |
| VII -- No Mocks/Fakes/Placeholders | Functional from commit one | Real embedded catalog, real allocator, real charter compiler, real LibGit2Sharp staging/commit, real model runs for US2/US3, real filesystem signal analysis for US3. Phases are sliced so each lands working behavior, never a stub (section 7). |
| VIII -- No Emojis | None in product | No emoji in any catalog content, generated charter, `team.md`, DTO, CLI string, Web string, commit message, doc, or log produced by this feature; a lint check on bundled catalog content enforces it. |
| IX -- Responsible AI | Privacy, accountability, content safety | RAI checks gate generated and edited names, charters, and proposals before any write; flagged content blocks the write and is reported, never silently persisted (FR-030, section 12). A named human (the project owner from 003) is accountable for every model-assisted run; runs are transparent through the step stream (Principles V, IX). Project contents read during analysis are sent only to GitHub Copilot (the fixed provider) and stay within the working directory. |
| X -- Safe Execution | Sandboxed, bounded, human-gated, auditable | All `.squad/` and analysis file access is validated through `Scaffolder.SandboxFs` rooted at the project working directory (FR-029); the proposal run mode is read-only/no-shell and never commits, so it cannot mutate the tree (section 10.1); model runs are bounded by the existing step/time limits and end in a visible terminal state; confirmation (a human gate) precedes every `.squad/` write (FR-005); sync (a destructive-ish git mutation) is explicit, user-gated, and verified against a reviewed change-set hash (FR-025, section 11); casting mutations are recorded as cast snapshots/registry entries in append-only history (FR-015, audit trail). |
| XI -- Agent Governance Toolkit | Governance centralized, observable | The fixed-provider rule (GitHub Copilot only, never selectable), sandbox boundary, approval/confirmation gate, and telemetry stay in the shared backend/runtime governance layer (reused from 003 and `Scaffolder.AgentRuntime.SandboxGovernance`), not in clients; casting adds no client-side policy. |

### Complexity Tracking

| Added Complexity | Why Needed | Simpler Alternative Rejected Because |
|---|---|---|
| New `packages/Scaffolder.Squad` library | Casting is a cohesive bounded context (catalog, allocator, charter compiler, `.squad` IO, git scribe) that must be unit-testable without the web host | Putting it all in `Scaffolder.Domain` bloats the domain with IO and git concerns; putting it in `AgentRuntime` conflates casting with the agent loop |
| Canonical/legacy dual-layout `.squad` reader (`casting/` subfolder is canonical; root-level `casting-*.json` is legacy) | FR-011/FR-019 require round-tripping teams created outside this app, and this repo's own `.squad/` uses root-level `casting-*.json` | A single-layout reader cannot read existing/external Squad teams, failing FR-019; the reader detects divergence between the two layouts and reports it rather than silently picking one (section 5.2) |
| Merge-safe append-only state (JSONL event-log sidecars union-merged; canonical JSON regenerated from them) for `registry`/`history` | FR-027 requires append-only state to merge across branches without losing entries, but raw `merge=union` on standard JSON produces invalid JSON | Plain union merge on standard JSON yields broken files; append-only JSONL sidecars union-merge line-by-line and a generated canonical JSON is rebuilt deterministically (section 11) |
| Read-only proposal-generation run mode + in-memory proposal until confirm for US2/US3 | FR-005/FR-007 require zero `.squad/` writes before confirm, and FR-008 requires observable, provider-backed generation | Reusing the standard write/shell-capable runner with its worktree-commit step would let proposal generation mutate files before confirmation, violating FR-007 (section 10.1) |

No principle is violated; the items above are justified complexity, not deviations.

---

## 4. Architecture and Component Design

### 4.1 Component map and ownership

```text
packages/Scaffolder.Squad/            NEW library (host-free, unit-testable)
  Catalog/    RoleCatalog, ScenarioGrouping, RoleArchetype, CatalogReader (embedded resources)
  Model/      Team, CastMember, Role, CastProposal, ProposedMember, CharterDocument,
              CastingPolicy, CastingRegistry, RegistryMember, CastHistory, CastSnapshot, SyncChange
  Squad/      SquadReader, SquadWriter (.squad <-> model; canonical `casting/` write, legacy-tolerant read with divergence detection), CharterCompiler
  Naming/     UniverseAllocator (single-universe-per-cast, uniqueness, retired reservation, overflow)
  Sync/       SquadGitScribe (per-file staging, commit, `.squad/.gitattributes` union-merge setup, change-set hashing)
  Analysis/   ProjectSignalScanner (frameworks/tests/docs/structure, sandbox-bounded, exclusion list, summary-only output)

apps/Scaffolder.Api/
  Casting/    CastingService (orchestrates the above; single source of truth),
              CastProposalStore (in-memory pending proposals keyed by ProjectId; TTL + ownership + concurrency rules, section 6.5),
              ProposalRunMode (read-only/no-shell tool policy + no-commit MAF run config, section 10.1),
              CastingPrompts (free-text/analysis task shaping for the MAF run),
              RaiContentGate (RAI checks on names/charters/proposals)
  Program.cs  endpoint registration + DI wiring (after AddAgentRuntime)
  Contracts/Dtos.cs  casting/team DTOs (snake_case [JsonPropertyName])

apps/Scaffolder.Cli/  TeamCommands.cs (thin HTTP over the API)
apps/web/             TeamPage + casting wizard (thin over the API)

Reused (unchanged contract):
  packages/Scaffolder.Domain         Project, ProjectId, ProviderSettings, ModelSource, IAgentRunner
  packages/Scaffolder.AgentRuntime   AgentRunnerDispatcher, AgentTurnExecutor, SandboxGovernance
  packages/Scaffolder.SandboxFs      SandboxPathValidator (FR-029 boundary)
  apps/Scaffolder.Api/Projects       ProjectService, ProjectView.Available, relink-or-remove (FR-031)
  apps/Scaffolder.Api/Git            LibGit2Sharp usage pattern from ProjectGitInitializer
```

`Scaffolder.Squad` references `Scaffolder.Domain` and `Scaffolder.SandboxFs` only; it does not reference the web host or `AgentRuntime`, keeping it pure (Principle VII testability). `CastingService` references `Scaffolder.Squad`, `Scaffolder.AgentRuntime` (for the run), and the 003 project layer.

### 4.2 Front-end mapping (Principle IV parity, FR-028)

| Capability | API | CLI (`scaffolder team ...`) | Web |
|---|---|---|---|
| List scenario groupings | `GET /api/casting/scenarios` | `team scenarios` | Casting wizard step 1 |
| Propose cast (scenario/free-text/analysis) | `POST /api/projects/{id}/casting/proposals` | `team cast --scenario|--goal|--analyze [--model]` | Casting wizard (model-override picker for model-assisted modes; no provider control) |
| Read proposal (poll model run) | `GET .../proposals/{pid}` | rendered inline / `team proposal show` | wizard live view (streamed steps) |
| Amend proposal | `PATCH .../proposals/{pid}` | `team proposal amend` | wizard edit controls |
| Confirm/create (augment/recast) | `POST .../proposals/{pid}/confirm` | `team proposal confirm` | wizard confirm |
| Reject proposal | `DELETE .../proposals/{pid}` | `team proposal reject` | wizard cancel |
| Read team | `GET /api/projects/{id}/team` | `team show` | `TeamPage` |
| Read charter | `GET .../team/members/{name}/charter` | `team charter show {name}` | member detail |
| Edit charter | `PUT .../team/members/{name}/charter` | `team charter edit {name}` | member editor |
| Add member | `POST .../team/members` | `team member add` | add-member dialog |
| Remove (retire) member | `DELETE .../team/members/{name}` | `team member remove {name}` | member action |
| Re-role member | `PATCH .../team/members/{name}` | `team member rerole {name}` | member action |
| List sync changes | `GET .../team/sync` | `team sync status` | sync panel (shows change-set hash) |
| Commit sync | `POST .../team/sync` (verifies `expected_change_set_hash`) | `team sync commit` | sync panel commit |

### 4.3 Data flow

```text
US1 scenario cast (deterministic, no model):
  client -> POST proposals {mode:scenario, grouping_id, universe?}
    CastingService: load catalog grouping -> UniverseAllocator picks universe + names
      (skip names reserved in existing registry; overflow -> unnamed) -> CharterCompiler
      builds starter charters -> CastProposal held in CastProposalStore (NOTHING written)
  client reviews -> PATCH (amend) -> POST confirm {intent: new|augment|recast}
    RaiContentGate checks names/charters -> SquadWriter writes team.md, agents/*/charter.md,
      casting/{policy,registry,history}.json (+ JSONL sidecars) under the project's .squad/
      (boundary-validated); recast retires dropped members (status->retired, charter ->
      agents/_alumni/{name}/charter.md) per section 6.5

US2 free-text / US3 analysis (model-assisted, observable, READ-ONLY run mode 10.1):
  client -> POST proposals {mode:free_text, goal, model_id?} | {mode:analysis, model_id?}
    provider is always GitHub Copilot (fixed); resolve model (request override -> role/agent default -> system default, FR-034)
    (analysis) ProjectSignalScanner scans working dir (sandbox-bounded, exclusion list) ->
      signal SUMMARY (not raw source)
    CastingService starts a MAF run via IAgentRunner in the read-only proposal run mode
      (no-write/no-shell tools, NO worktree commit, events persisted+streamed); only the
      catalog (read-only) and the signal summary feed the model; model returns a structured
      role selection (+ per-role justification for analysis, FR-010); steps stream live
    On success -> CastProposal (held in memory only). On failure/timeout -> NO proposal, NO writes.
  same amend/confirm path as US1; all .squad/ writes happen ONLY at confirm.

Sync (US5):
  client -> GET team/sync -> SquadGitScribe diffs .squad/ vs HEAD -> list adds/mods/dels
    + a change_set_hash over those paths/contents
  client -> POST team/sync {expected_change_set_hash} -> recompute hash; if mismatch, reject
    (state changed since review -> require re-review); else stage each changed .squad/ path
    individually -> commit -> report commit id
```

---

## 5. Data Model

All casting types live in `packages/Scaffolder.Squad/Model`. Names mirror the spec key entities.

### 5.1 .NET types (sketch)

```csharp
namespace Scaffolder.Squad.Model;

public enum CastMemberStatus { Active, Retired }
public enum CastMode { Scenario, FreeText, Analysis }
public enum CastIntent { New, Augment, Recast }

public sealed record Role(string Id, string Title, string Summary, string DefaultModel,
    IReadOnlyList<string> Capabilities, IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> Boundaries); // DefaultModel = role's default GitHub Copilot model (FR-033), overridable at runtime (FR-034)

public sealed record CastMember(string Name, Role Role, string CharterPath, CastMemberStatus Status,
    bool IsNamed); // IsNamed=false for generic overflow members (FR-017)

public sealed record Team(string ProjectName, string Universe,
    IReadOnlyList<CastMember> Members);

public sealed record ProposedMember(string ProposedName, Role Role, string CharterMarkdown,
    bool IsNamed, string DefaultModel, string? Justification); // DefaultModel from the role (FR-033); Justification set for analysis (FR-010)

public sealed record CastProposal(string ProposalId, CastMode Mode, string Universe,
    IReadOnlyList<ProposedMember> Members, bool ExistingTeamPresent, string? RunId,
    IReadOnlyList<string> Warnings);                      // e.g. universe-overflow, fell-back-to-defaults

public sealed record ScenarioGrouping(string Id, string Title, string Description,
    IReadOnlyList<Role> Roles);                           // catalog preset (FR-002)

public sealed record CastingPolicy(string Version, IReadOnlyList<string> AllowlistUniverses,
    IReadOnlyDictionary<string,int> UniverseCapacity);

public sealed record RegistryMember(string Name, string PersistentName, string Universe,
    string DefaultModel, CastMemberStatus Status, DateTimeOffset CreatedAt, string? PreviousName,
    string? SucceededBy, DateTimeOffset? RetiredAt, string? CharterPath);
    // DefaultModel = member's default GitHub Copilot model (FR-033), overridable at runtime (FR-034);
    // CharterPath -> agents/_alumni/{name}/charter.md when retired

public sealed record CastingRegistry(IReadOnlyDictionary<string, RegistryMember> Agents);

public sealed record CastSnapshot(string SnapshotId, string Universe, CastMode Mode, CastIntent Intent,
    IReadOnlyList<string> Members, IReadOnlyList<string> AddedMembers,
    IReadOnlyList<string> RetiredMembers, DateTimeOffset CreatedAt);

public sealed record CastHistory(IReadOnlyList<CastSnapshot> Snapshots,
    IReadOnlyList<string> UniverseUsageHistory);

public enum SyncChangeKind { Added, Modified, Removed }
public sealed record SyncChange(string RelativePath, SyncChangeKind Kind);
```

### 5.2 On-disk shapes (Squad convention, FR-012, FR-018)

`team.md` (matches this repo's existing `.squad/team.md` shape exactly):

```markdown
# Squad Team

> {project name}

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| {Name} | {Role Title} | .squad/agents/{name}/charter.md | active |

## Project Context

- **Project:** {project name}
- **Universe:** {universe}
- **Created:** {date}
- **Requested by:** {owner}
```

`.squad/agents/{name}/charter.md` (fixed section order, matching the repo's `agents/tank/charter.md`; FR-018 maps identity/role -> title+Role, owns -> Responsibilities, how it works -> Capabilities/Responsibilities, boundaries -> Boundaries):

```markdown
# {Name} — {Role Title}

{one-line summary}

## Role
{role description}

## Default Model
{default model} (GitHub Copilot; overridable at runtime)

## Capabilities
- {capability}: {level}

## Responsibilities
- {responsibility}

## Boundaries
- {what this member must not do}
```

`.squad/casting/policy.json` (FR-012; field names match the repo's `casting-policy.json` so external/round-trip teams load):

```json
{
  "casting_policy_version": "1.1",
  "allowlist_universes": ["The Matrix", "Star Wars", "Firefly", "..."],
  "universe_capacity": { "The Matrix": 10, "Star Wars": 12 }
}
```

`.squad/casting/registry.json` (FR-015, FR-016; retired names stay reserved). This is the CANONICAL, generated, valid-JSON view; it is rebuilt deterministically from the append-only sidecar (section 11) and is itself NOT union-merged:

```json
{
  "agents": {
    "Trinity": {
      "persistent_name": "Trinity",
      "universe": "The Matrix",
      "default_model": "gpt-5",
      "status": "active",
      "created_at": "2026-06-12T16:21:35Z",
      "retired_at": null,
      "previous_name": null,
      "succeeded_by": null,
      "charter_path": ".squad/agents/trinity/charter.md"
    }
  }
}
```

`.squad/casting/registry.events.jsonl` (FR-027; the MERGE-SENSITIVE append-only source of truth - one JSON object per line, union-merges cleanly):

```text
{"event":"member_cast","name":"Trinity","universe":"The Matrix","default_model":"gpt-5","created_at":"2026-06-12T16:21:35Z"}
{"event":"member_retired","name":"Tank","retired_at":"2026-06-13T10:00:00Z","charter_path":".squad/agents/_alumni/tank/charter.md"}
```

`.squad/casting/history.json` (FR-015; CANONICAL generated valid-JSON view, rebuilt from the sidecar in section 11, NOT union-merged):

```json
{
  "assignment_cast_snapshots": [
    { "snapshot_id": "...", "universe": "The Matrix", "mode": "scenario", "intent": "new",
      "members": ["Trinity", "Tank"], "added_members": ["Trinity", "Tank"],
      "retired_members": [], "created_at": "2026-06-12T16:21:35Z" }
  ],
  "universe_usage_history": ["The Matrix"]
}
```

`.squad/casting/history.events.jsonl` (FR-027; the MERGE-SENSITIVE append-only source of truth - one snapshot JSON object per line, union-merges cleanly):

```text
{"snapshot_id":"...","universe":"The Matrix","mode":"scenario","intent":"new","members":["Trinity","Tank"],"added_members":["Trinity","Tank"],"retired_members":[],"created_at":"2026-06-12T16:21:35Z"}
```

#### Canonical vs legacy layout precedence (FR-011, FR-019, FR-023 - resolves the former dual-layout open question)

`.squad/casting/` (the subfolder layout mandated by FR-012) is the CANONICAL, authoritative layout. Root-level `casting-policy.json` / `casting-registry.json` / `casting-history.json` are treated as LEGACY. Precedence and behavior on read:

1. **Canonical present:** read `.squad/casting/`; legacy root files (if any) are ignored for state but their presence is noted.
2. **Only legacy present:** read the legacy root files AND flag a migration (the team is loadable and editable; the API surfaces a `migration_available` indicator on `GET /team`).
3. **Both present and divergent:** DETECT the conflict (canonical and legacy disagree on roster/registry/history) and REPORT it as a structured `layout_conflict` on `GET /team` (do NOT silently pick one); the API offers a migration/remediation action that adopts the canonical layout. Until remediated, mutating operations are blocked with a clear reason (FR-023, no silent corruption).
4. **Both present and identical:** treat as canonical; offer a no-op cleanup that removes the redundant legacy files on the next sync.

`SquadWriter` ALWAYS writes the canonical `.squad/casting/` layout (FR-012) and never writes legacy root files. The migration action reads legacy, writes canonical, and stages the legacy-file removals into the same reviewed `.squad/` change set. Malformed JSON or non-conforming structure is reported with a precise, file-scoped error and never silently rewritten (FR-023).

---

## 6. API Contracts

All under `/api`, behind the existing `ApiKeyAuthMiddleware`, with snake_case DTOs in `apps/Scaffolder.Api/Contracts/Dtos.cs` (matching the existing `[JsonPropertyName]` convention), registered inline in `Program.cs` following the existing `MapGet`/`MapPost` style. Every project-scoped endpoint returns `409 project_unavailable` with the relink-or-remove remedy when `ProjectView.Available` is false (FR-031, reusing 003), and rejects any path escaping the working directory (FR-029).

### 6.1 Catalog and proposal

| Method + Route | Purpose | FRs |
|---|---|---|
| `GET /api/casting/scenarios` | List scenario groupings and the roles each provides (catalog-sourced, deterministic) | FR-002, FR-011 |
| `POST /api/projects/{id}/casting/proposals` | Propose a cast; body discriminated by `mode` (`scenario` / `free_text` / `analysis`); optional `universe` override; model-assisted modes accept an optional `model_id` that overrides the role/agent default model for the run (the provider is always GitHub Copilot and is not part of the request), and start an observable read-only run (section 10.1) returning its `run_id` | FR-003, FR-004, FR-005, FR-008, FR-009, FR-010, FR-013, FR-014 |
| `GET /api/projects/{id}/casting/proposals/{pid}` | Read the current proposal (poll while a model run completes) | FR-005, FR-008 |
| `PATCH /api/projects/{id}/casting/proposals/{pid}` | Amend before confirm: add role, remove role, rename member, change a member's role | FR-006 |
| `POST /api/projects/{id}/casting/proposals/{pid}/confirm` | Create the team on disk; body carries `intent` (`new` / `augment` / `recast`) when a team already exists. `augment` adds members and retires none; `recast` re-derives the roster and retires dropped members (section 6.5) | FR-005, FR-012, FR-015, FR-021, FR-022, FR-030 |
| `DELETE /api/projects/{id}/casting/proposals/{pid}` | Reject the proposal; writes no `.squad/` files | FR-007 |

### 6.2 Team read/update

| Method + Route | Purpose | FRs |
|---|---|---|
| `GET /api/projects/{id}/team` | Read roster + members (name, role, status); reports `exists`, validity, `requires_choice` when casting into an existing team, plus `migration_available` / `layout_conflict` for legacy-vs-canonical layout (section 5.2) | FR-019, FR-022, FR-023 |
| `GET /api/projects/{id}/team/members/{name}/charter` | Read a member's charter | FR-019 |
| `PUT /api/projects/{id}/team/members/{name}/charter` | Edit/persist a charter (RAI-checked); no other member's files touched | FR-020, FR-030 |
| `POST /api/projects/{id}/team/members` | Add a member (allocate name from the team's universe, update roster + registry) | FR-021, FR-013, FR-016, FR-017 |
| `DELETE /api/projects/{id}/team/members/{name}` | Retire a member: set registry `status` to `retired`, move charter to `.squad/agents/_alumni/{name}/charter.md`, reserve the name (not destroyed) | FR-021, FR-016 |
| `PATCH /api/projects/{id}/team/members/{name}` | Change a member's role (update roster + charter; preserve name) | FR-021 |

### 6.3 Sync

| Method + Route | Purpose | FRs |
|---|---|---|
| `GET /api/projects/{id}/team/sync` | List added/modified/removed `.squad/` files vs HEAD plus a `change_set_hash` over those paths and contents; reports "nothing to sync" when clean | FR-024, FR-026 |
| `POST /api/projects/{id}/team/sync` | Verify the supplied `expected_change_set_hash` against the live state; if it mismatches (state changed since review) reject and require re-review; otherwise stage exactly those `.squad/` files individually and commit; return the commit id; never auto-invoked | FR-024, FR-025 |

### 6.4 Representative DTOs

```csharp
public sealed record CreateProposalRequest
{
    [JsonPropertyName("mode")] public string? Mode { get; init; }              // scenario | free_text | analysis
    [JsonPropertyName("grouping_id")] public string? GroupingId { get; init; } // required when mode == scenario
    [JsonPropertyName("goal")] public string? Goal { get; init; }              // required when mode == free_text
    [JsonPropertyName("universe")] public string? Universe { get; init; }      // optional override (FR-014)
    [JsonPropertyName("model_id")] public string? ModelId { get; init; }       // free_text/analysis only; OPTIONAL runtime override of the role/agent default model; resolved request override -> role/agent default -> system default (FR-034). Provider is always GitHub Copilot and is never part of the request (FR-032).
}

public sealed record ProposedMemberDto
{
    [JsonPropertyName("proposed_name")] public required string ProposedName { get; init; }
    [JsonPropertyName("role_id")] public required string RoleId { get; init; }
    [JsonPropertyName("role_title")] public required string RoleTitle { get; init; }
    [JsonPropertyName("charter_preview")] public required string CharterPreview { get; init; }
    [JsonPropertyName("is_named")] public required bool IsNamed { get; init; } // false = generic overflow (FR-017)
    [JsonPropertyName("justification")] public string? Justification { get; init; } // analysis only (FR-010)
}

public sealed record CastProposalResponse
{
    [JsonPropertyName("proposal_id")] public required string ProposalId { get; init; }
    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("universe")] public required string Universe { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<ProposedMemberDto> Members { get; init; }
    [JsonPropertyName("existing_team_present")] public required bool ExistingTeamPresent { get; init; }
    [JsonPropertyName("run_id")] public string? RunId { get; init; }           // set for model-assisted modes
    [JsonPropertyName("warnings")] public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record ConfirmProposalRequest
{
    [JsonPropertyName("intent")] public string? Intent { get; init; }          // new | augment | recast (FR-022)
}

public sealed record SyncChangeDto
{
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }      // added | modified | removed
}

public sealed record SyncStatusResponse
{
    [JsonPropertyName("changes")] public required IReadOnlyList<SyncChangeDto> Changes { get; init; }
    [JsonPropertyName("change_set_hash")] public required string ChangeSetHash { get; init; } // reviewed token (FR-024)
    [JsonPropertyName("nothing_to_sync")] public required bool NothingToSync { get; init; }   // FR-026
}

public sealed record SyncCommitRequest
{
    [JsonPropertyName("message")] public string? Message { get; init; }                 // commit message; default generated
    [JsonPropertyName("expected_change_set_hash")] public required string ExpectedChangeSetHash { get; init; } // from GET /sync; verified before staging
}
```

Error behavior mirrors existing endpoints: `400 { "error": ... }` on bad input (empty/whitespace goal -> rejected with a clear reason, FR-003/US2 scenario 4; an unrecognized `model_id` that GitHub Copilot does not offer -> rejected before any run, FR-034); `404` on missing project/proposal/member; `409 requires_choice` when a casting confirm targets an existing team without `intent` (FR-022); `409 layout_conflict` when canonical and legacy `.squad/casting/` layouts diverge (section 5.2, FR-023); `409 sync_state_changed` when `expected_change_set_hash` does not match the live state (re-review required, FR-024); `409 project_unavailable` with relink-or-remove (FR-031); `422 rai_flagged` with the flagged reason when RAI gating blocks a write (FR-030); `409 model_run_failed` when a model-assisted proposal fails or times out, with no `.squad/` writes (US2/US3 failure edge case). Credential-free, emoji-free messages throughout (Principles VIII, IX).

### 6.5 Augment vs recast semantics and proposal lifecycle

**Augment vs recast (FR-021, FR-022; resolves the former recast open question).** When a project already has a team, `confirm` requires an explicit `intent`, and the two write paths differ precisely:

- **`augment`** - ADD the proposal's new members via the casting/allocation algorithm (section 9) and retire NONE. Existing members keep their identity (name, role, charter) untouched. New members get fresh names from the team's existing universe. The cast snapshot records `intent: augment`, `added_members`, and an empty `retired_members`.
- **`recast`** - RE-DERIVE the roster. Members present in both the current team and the new roster are RETAINED with their existing identity (name reserved, charter preserved). Members in the new roster but not the current team are ADDED via the casting algorithm. Members in the current team but NOT in the new roster are RETIRED, never overwritten or renamed:
  1. set their `registry.json` entry `status` to `"retired"` and stamp `retired_at`; the name stays reserved forever and is never reused (FR-016);
  2. move their charter to the archive path `.squad/agents/_alumni/{name}/charter.md` (the active `agents/{name}/charter.md` is removed by the move); `charter_path` in the registry is updated to the alumni path;
  3. append a `member_retired` event to `registry.events.jsonl` and a recast snapshot to `history.events.jsonl` listing `added_members` and `retired_members`.

A recast NEVER silently overwrites or renames an existing agent (FR-022). Both intents are surfaced identically in the proposal preview (which members would be added vs retained vs retired) and in CLI and Web before confirm (Principle IV).

**Pending-proposal lifecycle and concurrency (`CastProposalStore`).** Proposals are held in memory only, keyed by `ProjectId` and `proposal_id`, and never touch disk before confirm (FR-005, FR-007). At most ONE active proposal may exist per project; creating a new proposal supersedes (discards) any prior pending proposal for that project, so there is no split-brain between competing drafts. Each proposal has a TTL (default 30 minutes) after which it expires and is evicted; `confirm`/`amend`/`GET` on an expired or unknown proposal returns `404`. The store is process-local and NOT durable: on service restart all pending proposals are lost (no `.squad/` was written, so the project is unchanged), and the user simply re-proposes. Ownership is the project owner from 003; the proposal records the requesting identity for accountability (Principle IX).

---

## 7. Implementation Phases

Phases are dependency-ordered and mapped to the spec's user-story priorities (P1 scenario -> P2 free-text and read/update -> P3 analysis and sync). Each story phase delivers the API capability plus the CLI thin client so it is independently demoable end-to-end (the spec's "Independent Test" is single-client); the Web parity phase then closes Principle IV before Definition of Done.

### Phase 0: Squad library foundation (no user-facing behavior alone)

| ID | Task | Components | FRs |
|---|---|---|---|
| T001 | Scaffold `packages/Scaffolder.Squad`; add to `scaffolders.sln`; reference `Domain` + `SandboxFs` | new csproj | - |
| T002 | Casting model types (section 5.1) | `Squad/Model/*` | FR-012, FR-015, FR-016 |
| T003 | Embedded catalog + `CatalogReader` (section 8) | `Squad/Catalog/*` | FR-002, FR-011 |
| T004 | `SquadReader`/`SquadWriter` (canonical `casting/` write; legacy-tolerant read with divergence detection + migration; merge-safe JSONL sidecars + canonical JSON regeneration; boundary-validated; malformed-reported) | `Squad/Squad/*` | FR-012, FR-019, FR-023, FR-027, FR-029 |
| T005 | `CharterCompiler` (fixed-section charter, matches repo charter shape) | `Squad/Squad/CharterCompiler` | FR-018 |
| T006 | `UniverseAllocator` (single-universe, uniqueness, retired reservation, overflow) (section 9) | `Squad/Naming/*` | FR-013, FR-014, FR-016, FR-017 |

### Phase 1: Scenario casting (US1, P1) - end-to-end via API + CLI

| ID | Task | Components | FRs / Acceptance |
|---|---|---|---|
| T007 | `CastingService` scenario propose (deterministic) + `CastProposalStore` | `Casting/*` | FR-002, FR-005, FR-009; US1 sc.1,5 |
| T008 | Amend + reject (no-write) | `CastingService` | FR-006, FR-007; US1 sc.3,4 |
| T009 | Confirm/create -> write `team.md`, charters, `casting/*` + record snapshot/registry | `CastingService`, `SquadWriter` | FR-012, FR-015; US1 sc.2 |
| T010 | Endpoints `GET /casting/scenarios`, `POST/GET/PATCH/DELETE proposals`, `POST confirm`; DTOs; DI | `Program.cs`, `Dtos.cs` | FR-028 |
| T011 | CLI `team scenarios` / `team cast --scenario` / `team proposal amend|confirm|reject` | `TeamCommands.cs` | FR-028 |

### Phase 2: Read and update existing definitions (US4, P2)

| ID | Task | Components | FRs / Acceptance |
|---|---|---|---|
| T012 | Read team + member charters; detect-existing + validity (`requires_choice`); legacy/canonical layout precedence, `migration_available`, `layout_conflict` reporting (section 5.2) | `CastingService`, `SquadReader` | FR-019, FR-022, FR-023; US4 sc.1,6 |
| T013 | Edit charter (RAI-gated, isolated write) | `CastingService`, `RaiContentGate` | FR-020, FR-030; US4 sc.2 |
| T014 | Add / retire / re-role member: add allocates a name; retire sets registry `status=retired`, moves charter to `.squad/agents/_alumni/{name}/charter.md`, reserves the name; re-role preserves name (registry + roster maintenance) | `CastingService`, `UniverseAllocator` | FR-021, FR-016; US4 sc.3,4,5 |
| T015 | Augment vs recast on confirm into existing team (section 6.5): `augment` adds only; `recast` re-derives roster, retains overlap, adds new, retires dropped members (status transition + alumni archive + recast snapshot) | `CastingService` | FR-021, FR-022; clarifications |
| T016 | Endpoints `GET /team`, charter read/update, member add/remove/rerole; CLI `team show|charter|member` | `Program.cs`, `TeamCommands.cs` | FR-028 |

### Phase 3: Free-text casting (US2, P2)

| ID | Task | Components | FRs / Acceptance |
|---|---|---|---|
| T017 | `CastingPrompts` free-text task shaping (catalog archetypes as the menu) | `Casting/CastingPrompts` | FR-003 |
| T018 | Run free-text proposal as observable MAF run via `IAgentRunner`; parse structured role selection; in-memory until confirm; empty-goal rejection | `CastingService` | FR-005, FR-008; US2 sc.1,2,4 |
| T019 | Failure/timeout -> no writes; `409 model_run_failed` | `CastingService` | US2 failure edge |
| T020 | Endpoint mode `free_text`; CLI `team cast --goal` | `Program.cs`, `TeamCommands.cs` | FR-028 |

### Phase 4: Analysis casting (US3, P3)

| ID | Task | Components | FRs / Acceptance |
|---|---|---|---|
| T021 | `ProjectSignalScanner` (frameworks/tests/docs/structure, sandbox-bounded, size-bounded, exclusion list for `.git`/build outputs/dependency folders/binaries/secrets, summary-only output) | `Squad/Analysis/*` | FR-004, FR-029; US3 sc.5, large-project edge |
| T022 | Analysis task shaping + per-role justification; default fallback on no signals | `CastingPrompts`, `CastingService` | FR-010; US3 sc.1,2,4 |
| T023 | Endpoint mode `analysis`; CLI `team cast --analyze` | `Program.cs`, `TeamCommands.cs` | FR-028 |

### Phase 5: Git sync (US5, P3)

| ID | Task | Components | FRs / Acceptance |
|---|---|---|---|
| T024 | `SquadGitScribe`: diff `.squad/` vs HEAD; `change_set_hash` over the change set; verify `expected_change_set_hash` before staging (reject on mismatch); per-file staging; commit; never auto-commit | `Squad/Sync/*` | FR-024, FR-025; US5 sc.1,2,4 |
| T025 | Nothing-to-sync path (no commit) | `SquadGitScribe` | FR-026; US5 sc.3 |
| T026 | `.squad/.gitattributes` union-merge setup for the append-only JSONL sidecars (relative paths) + canonical JSON regeneration after merge (section 11) | `SquadGitScribe`, `SquadReader`/`SquadWriter` | FR-027 |
| T027 | Endpoints `GET/POST /team/sync`; CLI `team sync status|commit` | `Program.cs`, `TeamCommands.cs` | FR-028 |

### Phase 6: Web parity (Principle IV)

| ID | Task | Components | FRs |
|---|---|---|---|
| T028 | `TeamPage` (roster, member detail, charter editor) + add/remove/rerole | `apps/web` | FR-028 |
| T029 | Casting wizard (scenario / free-text / analysis; live streamed run; amend; augment/recast confirm) | `apps/web` | FR-028 |
| T030 | Sync panel (changed files + commit) | `apps/web` | FR-028 |

### Phase 7: Documentation

| ID | Task | Components | FRs |
|---|---|---|---|
| T031 | Update `docs/reference/api.md`, `cli.md`, `web.md`, `events.md` (section 15) | `docs/reference/*` | FR-028 |

### Phase 8: Tests and security/RAI review

| ID | Task | Components | FRs / SCs |
|---|---|---|---|
| T032 | Backend unit/integration (section 13) | `tests/Scaffolder.Tests/Casting` | SC-001..SC-005, SC-007 |
| T033 | Web tests (Vitest) | `apps/web` | SC-006 |
| T034 | Security review (Seraph): boundary, per-file staging, no writes outside `.squad/` | review | FR-024, FR-029 |
| T035 | RAI review (Rai): content gate on names/charters/proposals, provider content-safety | review | FR-030 |

**Phase list:** 0, 1, 2, 3, 4, 5, 6, 7, 8.

---

## 8. Catalog Design

The role/scenario catalog is app-bundled, app-maintained, versioned with the code, and never written into a project (FR-011). **Decision: ship it as embedded resources compiled into the `Scaffolder.Squad` assembly** - scenario bundles as JSON and charter templates as markdown, loaded by `CatalogReader` from the assembly manifest. Justification: embedding keeps the catalog inside the single deployable artifact (deployment parity, Principle VI), makes it impossible for a project's files to shadow or tamper with it, and removes any runtime file-path dependency that content files under a working directory would introduce. The catalog is read-only at runtime and consulted only when generating a cast or adding a member.

Format (kept schema-compatible with the Squad bundle/role catalog per FR-011, porting the squadboard `SquadboardBundle` / `BundleTeamMember` interfaces to .NET):

```text
Scaffolder.Squad/Catalog/Resources/
  catalog.manifest.json          # version + list of groupings
  groupings/{id}.json            # one scenario grouping: id, title, description, roles[]
  roles/{role-id}.json           # role archetype: id, title, summary, default_model, capabilities[], responsibilities[], boundaries[] (default_model = the role's default GitHub Copilot model, FR-033)
  charters/{role-id}.md          # starter charter template with {Name}/{Role} placeholders
```

A grouping JSON references role-archetype ids; `CatalogReader` resolves them and `CharterCompiler` fills the charter template with the allocated name. The catalog ships at least these nine groupings (FR-002), derived from the squadboard bundle set:

1. software-development - Lead Architect, Backend, Frontend, QA Engineer
2. content-authoring - Writer, Editor
3. ai-agent-development - Agent Architect, Prompt Engineer, AI Safety Reviewer, Agent Evaluator
4. open-source-maintenance - Maintainer, Contributor, Code Reviewer, Issue Triager
5. research-spike - Lead Researcher, Findings Reviewer
6. library-or-sdk - Library Lead, Core Implementer, Docs Writer, QA/Compat Tester
7. ops-incident-runbook - On-Call Lead, Escalation Lead
8. bug-bash - Triage Lead, Bug Fixer (x3)
9. product-feature-delivery - Lead PM, Customer Researcher, Prototype Designer, Feature Docs Writer, Quality Reviewer

The "software-development" and "content-authoring" groupings are mandatory per FR-002. The always-present operational roles (Scribe, Work Monitor) are appended to every cast as a catalog-defined base set, matching the repo's own team. A catalog-content lint (run in tests) asserts no emojis anywhere in bundled content (Principle VIII).

---

## 9. Universe and Name Allocation

`UniverseAllocator` in `packages/Scaffolder.Squad/Naming` enforces the naming rules. Universes and per-universe capacities come from `CastingPolicy` (the catalog seeds a default policy whose `allowlist_universes` and `universe_capacity` match the repo's `casting-policy.json`, e.g. "The Matrix": 10, "Star Wars": 12). Each universe has a catalog-shipped ordered name pool.

Algorithm:

1. **Auto-propose universe (FR-014).** Choose deterministically from `allowlist_universes`, preferring a universe not present in `history.universe_usage_history` (to vary across casts), else the first allowed. The user may override before confirm; the override must be in the allowlist.
2. **Single universe per cast (FR-013).** All names in one cast assignment come from the chosen universe's pool; mixing universes within a cast is rejected.
3. **Uniqueness + retired reservation (FR-016).** Build a reserved set = every name in the registry (active AND retired). Iterate the chosen universe's name pool in catalog order, skipping reserved names, assigning the next free name to each proposed member.
4. **Overflow (FR-017).** When the free pool is exhausted before all members are named, remaining members get generic, unnamed identifiers (`member-1`, `member-2`, ... unique within the cast), flagged `is_named = false`, never duplicate/empty/out-of-universe. The cast is NOT rejected for exceeding capacity; a warning is added to the proposal.
5. **Add-member (US4).** Adding to an existing team allocates from the team's already-chosen universe using the same reserved-set logic, preserving single-universe-per-cast.

Capacity for a universe is `min(universe_capacity[universe], poolSize)`. Allocation is pure and deterministic given (universe, registry, member count), making it unit-testable without a model and guaranteeing SC-005 (no duplicate/out-of-universe names, no retired-name reuse).

---

## 10. Model-Assisted Casting (US2, US3)

Free-text (US2) and analysis (US3) casting run as observable MAF runs (Principles I, V; FR-008), reusing `Scaffolder.AgentRuntime` exactly as feature 001/002/003 runs do - no parallel runtime - but through a dedicated read-only proposal-generation run mode (section 10.1) that guarantees the run cannot mutate the working directory before the human confirms.

### 10.1 Read-only proposal-generation run mode (write-safety guarantee, FR-005, FR-007, FR-008; Principles I, V, X)

The standard agent path (`AgentTurnExecutor`) is designed for file-editing agents: it grants write/shell-capable tools and commits worktree changes after execution. Reusing it for proposal generation would let a proposal mutate `.squad/` (or anything else) before the user confirms, violating the zero-write guarantee. Casting therefore defines a DEDICATED proposal-generation run mode built ON MAF (Principle I) but configured for read-only generation. It differs from the standard runner in exactly three ways:

1. **Tool allowlist (read-only, no shell).** The run is started with a restricted tool policy that exposes ONLY a read-only casting toolset: the read-only catalog context (role archetypes) and, for analysis, the precomputed signal summary from `ProjectSignalScanner`, plus the structured-output "submit role selection" tool. No file-write, no file-delete, no shell/exec, and no arbitrary file-read tool is registered. The allowlist is enforced through the shared `Scaffolder.AgentRuntime.SandboxGovernance` layer (Principle XI), not in the client. Only `ProjectSignalScanner` output and the read-only catalog may feed the model; the model never reads the working tree directly.
2. **No worktree commit step.** The run mode skips the `AgentTurnExecutor` commit/finalize stage entirely; there is no staging, no commit, and no working-tree write at any point of proposal generation. The run's only output is the structured role selection, which `CastingService` maps to an in-memory `CastProposal`. Every `.squad/` write happens EXCLUSIVELY in the `confirm` operation (sections 4.3, 6.5), after the human gate and the RAI gate.
3. **Persisted and streamed run events (Principle V).** Like any other run, the proposal run persists its events (agent messages, tool calls, results, terminal state) and streams them live; the proposal response carries the `run_id` so CLI/Web can attach. Observability is identical to a normal run even though the run is read-only.

This makes proposal generation provably write-safe: with no write/shell tools and no commit stage, the run has no mechanism to alter the project, so FR-005/FR-007 hold by construction rather than by convention.

- **Model override (per run, FR-034).** GitHub Copilot is the single, fixed provider for every model-assisted run; it is never selectable and no provider field exists on the request (FR-032). Each `free_text`/`analysis` request may set an optional `model_id` that OVERRIDES the role/agent default model for that run; when omitted, the role/agent default model applies (FR-033). The effective model resolves in order: request `model_id` override -> role/agent default model -> system default model. The model actually used is recorded and observable in the run's steps (Principle V, FR-034). No new model source is introduced (Principle II). The model override is exposed at parity in CLI (`--model`) and Web (a wizard model picker); the provider is not exposed because there is nothing to choose (Principle IV). Scenario casting (US1) takes no model override and makes no model call (FR-009).
- **Observability.** The proposal-generation run streams agent messages, tool calls, and results to any client like any other run; the proposal response carries the `run_id` so the CLI/Web can attach to the live stream (US2 scenario 2, Principle V).
- **Grounding.** `CastingPrompts` passes the catalog role archetypes as the menu the model selects from and adapts (rather than inventing free-form roles), so generated rosters stay Squad-schema-compatible and charters keep the fixed section structure (FR-011, FR-018). The model returns a structured role selection (JSON via the run's structured output / a casting tool), which `CastingService` maps to a `CastProposal`.
- **Analysis signals (US3, FR-004, FR-010).** `ProjectSignalScanner` reads the project working directory through `SandboxFs` (FR-029) and detects: languages/frameworks (`package.json` + framework deps such as React/Next/Vue/Angular, `*.csproj`/`*.sln`, `requirements.txt`/`pyproject.toml`, `go.mod`, `pom.xml`/`build.gradle`), tests (test directories, `*Tests`, `__tests__`, `*.spec.*`), documentation (`README*`, `docs/`), CI/ops (`.github/workflows`, Dockerfiles), and overall structure (monorepo/app/lib). The scan is bounded and privacy-preserving: it EXCLUDES `.git/`, build outputs (`bin/`, `obj/`, `dist/`, `build/`, `out/`, `target/`), dependency folders (`node_modules/`, `.venv/`, `vendor/`, `packages/`), binary/large files, and likely-secret files (`.env*`, `*.pem`, `*.key`, credential files), and is size-bounded (file-count and depth caps) so large projects stay bounded (large-project edge case). Only a derived SIGNAL SUMMARY (detected frameworks/test presence/structure facts) is sent to the model - never raw source files. The summary is fed to the model, which proposes roles and, for each, the signal that justified it (e.g. "Frontend - detected React in package.json"; FR-010). With no recognizable signals, the system returns a usable default proposal (the software-development grouping) and marks `fell_back_to_defaults` in `warnings` (US3 scenario 4, SC-007).
- **No partial writes.** The proposal is held only in `CastProposalStore` (in memory) until the user confirms. If the model run fails or times out, the service surfaces the failure (`409 model_run_failed`) and writes zero `.squad/` files, leaving the project unchanged (US2/US3 failure edge, FR-007). Runs are bounded by the existing step/time limits and end in a visible terminal state (Principle X).

---

## 11. Git Sync

Sync is an explicit, user-gated git commit of the project's `.squad/` changes (FR-024, FR-025). `SquadGitScribe` (in `packages/Scaffolder.Squad/Sync`) uses **LibGit2Sharp** (already a dependency via `ProjectGitInitializer`), adapting the squadboard scribe per-file-staging pattern to .NET. **Decision: LibGit2Sharp over shelling to `git`** - it avoids depending on a `git` binary being on PATH in the cloud (deployment parity, Principle VI), gives precise index control, and is consistent with the existing project-git layer.

Mechanism:

1. `GET /team/sync` opens the project repo, diffs the working tree under `.squad/` against HEAD, and returns the list of added/modified/removed `.squad/` files (`SyncChange[]`) plus a `change_set_hash` - a stable hash computed over the ordered set of changed `.squad/` relative paths and their content (and HEAD blob ids). This hash is the reviewed-change token the user implicitly approves when they look at the change list. When the list is empty, it reports "nothing to sync" and no commit is created (FR-026).
2. `POST /team/sync` carries `expected_change_set_hash` (from the `GET`). The scribe RE-COMPUTES the current `change_set_hash` and compares; if they differ - meaning `.squad/` changed since the user reviewed (for example a new cast or charter edit landed) - it rejects with `409 sync_state_changed` and commits nothing, forcing a re-review. Only on a match does it stage **each changed `.squad/` path individually** with `Commands.Stage(repo, path)` - never `git add` of the working tree - so the commit can only ever contain `.squad/` files (FR-024). A guard asserts every staged path is under `.squad/` and rejects the commit if anything outside `.squad/` would be included.
3. The commit is created with the project owner as author (accountability, Principle IX) and a generated-or-supplied message; the resulting commit id is returned. The system never commits automatically at any other point (FR-025).

**Merge-safe append-only state (FR-027).** Raw `merge=union` on standard JSON can interleave object/array fragments into invalid JSON, so relying on a tolerant reader to "fix" it would already mean FR-027 ("merge without losing entries") has failed. Instead, the merge-sensitive state is stored append-only as **JSONL event-log sidecars** that union-merge cleanly line-by-line:

- `.squad/casting/registry.events.jsonl` - one event object per line (`member_cast`, `member_retired`, `member_reroled`, ...).
- `.squad/casting/history.events.jsonl` - one cast snapshot object per line.

Each line is a complete, self-contained JSON object terminated by a newline, so `git`'s union merge concatenates lines from both branches without ever splitting a record - the merged sidecar is always valid JSONL and never loses an entry. The merge attributes live in **`.squad/.gitattributes`** (inside `.squad/`, so sync's `.squad/`-only staging includes it; FR-024), with paths RELATIVE to `.squad/`:

```text
casting/registry.events.jsonl merge=union
casting/history.events.jsonl  merge=union
```

`.squad/.gitattributes` is itself part of the reviewed `.squad/` change set and is created/staged on first write. The canonical `registry.json` and `history.json` are GENERATED, valid-JSON projections rebuilt deterministically from the sidecars (latest-event-wins per primary key: registry member name; snapshot id) on every read/write; they are NOT union-merged (and may carry `merge=binary`/regenerate-on-conflict so a textual conflict there is resolved by regeneration from the sidecars). After a branch merge, the next read rebuilds the canonical JSON from the merged sidecars, preserving every roster entry and history snapshot from both branches. `team.md` and individual `charter.md` files are NOT union-merged (they are authored documents where a textual conflict is the correct signal). A merge test (section 13) creates two-branch edits of the sidecars, merges, and asserts the result parses and that no roster/history entry is lost (FR-027), satisfying FR-027 while keeping the canonical JSON valid Squad state (FR-012).

---

## 12. Security and RAI

- **Working-directory boundary (FR-029, Principle X).** All `.squad/` reads/writes, analysis scans, and sync diffs go through `Scaffolder.SandboxFs.SandboxPathValidator.ValidateAndResolve` rooted at the project's working directory. Absolute paths, `..` traversal, and symlink/junction escapes are rejected, reusing the exact validator that guards run sandboxes. `SquadReader`/`SquadWriter`/`ProjectSignalScanner`/`SquadGitScribe` never accept an unvalidated path. The proposal-generation run additionally has no write/shell/arbitrary-read tools (section 10.1), so it cannot reach the filesystem at all. Seraph reviews this boundary plus the sync per-file-staging guard (no writes outside `.squad/`).
- **RAI gate (FR-030, Principle IX).** `RaiContentGate` runs RAI checks on generated and edited content - proposed/edited member names, charter text, and free-text/analysis proposals - before any write (on proposal confirm and on charter PUT). It applies content-safety appropriate to the GitHub Copilot provider plus deterministic checks (disallowed/offensive names, harmful or infringing charter content). Flagged content blocks the write and returns `422 rai_flagged` with the reason; content that violates expectations is never silently persisted. Rai reviews this gate and the GitHub Copilot content-safety wiring.
- **Malformed and divergent `.squad/` handling (FR-023).** `SquadReader` reports precisely what is invalid (which file, what failed) and never silently discards or rewrites existing files; reading a malformed team yields a structured error, not data loss. A canonical-vs-legacy layout divergence (section 5.2) is reported as a `layout_conflict` and blocks mutation until remediated rather than silently choosing a layout.
- **Privacy (Principle IX).** Analysis sends only a derived signal SUMMARY to GitHub Copilot (the fixed provider) - never raw source files - and the scanner excludes `.git/`, build outputs, dependency folders, binaries, and likely-secret files (`.env*`, keys, credential files) (section 10). No secrets are written into charters, the registry, sidecars, logs, or telemetry. Sync commit messages and error responses are credential-free and emoji-free (Principles VIII, IX).
- **FR numbering check.** Verified against the spec: FR-029 is the working-directory boundary, FR-030 is the RAI gate, FR-031 is the project-unavailable relink-or-remove remedy, and FR-032/FR-033/FR-034 are the fixed GitHub Copilot provider, the per-role default model, and the runtime model override respectively.

---

## 13. Testing Strategy

Real fixtures only, no mocks/fakes/stubs of casting logic, catalog, allocator, or git (Principle VII). Backend tests live in `tests/Scaffolder.Tests/Casting` with a `CastingWebApplicationFactory` mirroring `ProjectsWebApplicationFactory`; fixtures are temp git repos created with LibGit2Sharp and sample project trees with real detectable signals. Model-assisted tests run against the real provider gated behind an environment variable (mirroring 003's `GITHUB_INTEGRATION_TESTS`); deterministic proposal shaping, allocation, catalog, reader/writer, and sync are fully tested without a model.

| Success Criterion | Tests |
|---|---|
| SC-001 (scenario cast -> valid `.squad/`, <2 min, no manual edit) | Integration: scenario propose -> confirm; assert `team.md`, a charter per member, and `casting/*` exist and parse; from CLI and Web |
| SC-002 (all 3 modes: confirmed -> complete files; rejected -> zero files) | Integration per mode: confirm produces complete bookkeeping; reject, model-failure, and pre-confirm proposal-run all assert zero `.squad/` writes (file-system diff), proving the read-only run mode (section 10.1) |
| SC-003 (read team + charter; one edit never alters another member's files) | Integration: edit one charter, assert byte-level diff touches only that file |
| SC-004 (sync commit contains exactly `.squad/`; clean -> no commit) | Integration: real repo, sync after a cast, assert commit tree is entirely under `.squad/` (including `.squad/.gitattributes`); stale `change_set_hash` rejected with no commit; clean repo -> no commit |
| SC-005 (no duplicate/out-of-universe names; retired never reused) | Unit: `UniverseAllocator` across repeated casts, overflow, and retire-then-add; property-style uniqueness assertions |
| SC-006 (CLI and Web expose identical capabilities) | Contract: enumerate API capabilities; assert a CLI command and a Web path for each; Vitest for Web flows |
| SC-007 (analysis justifies >=1 role by a signal; signal-less -> usable default) | Integration: sample repo with React + tests -> proposal cites those signals; empty repo -> default proposal with `fell_back_to_defaults` |

Contract tests assert the legacy/canonical layout precedence: the reader round-trips the repo's own `.squad/` (root-level `casting-*.json`) as LEGACY and flags migration, round-trips the canonical `.squad/casting/` subfolder layout, and DETECTS+reports a `layout_conflict` when the two diverge (FR-011, FR-019, FR-023). Additional targeted tests: (a) **write-safety** - a free-text/analysis proposal run leaves the working tree byte-identical (no `.squad/` writes) before confirm, and the proposal run's tool policy registers no write/shell tools (section 10.1, FR-005/FR-007); (b) **model override** - a `free_text`/`analysis` request with an explicit `model_id` overrides the role/agent default model and the override is observable in the run steps, an omitted `model_id` resolves the role/agent default then the system default, an unrecognized `model_id` is rejected `400` with no run, and no request carries or accepts a `provider` field (the provider is always GitHub Copilot) (FR-008, FR-032, FR-033, FR-034); (c) **sync token** - `POST /sync` with a stale `expected_change_set_hash` is rejected `409 sync_state_changed` with no commit, and a matching hash commits exactly `.squad/` including `.squad/.gitattributes` (FR-024); (d) **merge-safe state** - two-branch edits of `registry.events.jsonl`/`history.events.jsonl` merge with `merge=union`, the merged sidecars parse as valid JSONL, the regenerated canonical JSON is valid and loses no roster/history entry (FR-027); (e) **recast** - a recast retires dropped members (registry `status=retired`, charter moved to `.squad/agents/_alumni/{name}/charter.md`, name reserved, recast snapshot recorded), retains overlap, adds new, and never overwrites/renames; augment retires none (FR-021, FR-022).

---

## 14. Risks and Open Questions

**Resolved during plan revision (rubber-duck review fixes incorporated):**

- **Issue 1 - Write-safe model-assisted casting (RESOLVED).** Proposal generation runs in a dedicated read-only MAF run mode (section 10.1): no write/shell/arbitrary-read tools, no `AgentTurnExecutor` worktree-commit step, persisted+streamed events. All `.squad/` writes are confined to `confirm`. Write-safety is structural, not conventional (FR-005, FR-007, FR-008; Principles I, V, X).
- **Issue 2 - Per-run model override on the fixed provider (RESOLVED, reconciled to constitution v1.4.0).** GitHub Copilot is the single, fixed provider and is never selectable; no provider field exists on any request (FR-032). `free_text` and `analysis` proposals accept an optional `model_id` that overrides each role's default model for that run, resolved request override -> role/agent default model -> system default; the effective model is observable in the run steps. The model override is exposed at CLI/Web parity while the provider is not exposed (sections 2, 6.4, 10; FR-008, FR-032, FR-033, FR-034; Principles II, IV, V).
- **Issue 3 - Sync stays inside `.squad/` + reviewed-change token (RESOLVED).** Merge attributes live in `.squad/.gitattributes` with paths relative to `.squad/` and are part of the reviewed change set; `POST /sync` verifies an `expected_change_set_hash` from `GET /sync` and rejects on mismatch (sections 6.3, 11; FR-024, FR-027).
- **Issue 4 - Merge-safe state (RESOLVED).** Merge-sensitive state is stored as append-only JSONL event-log sidecars (`registry.events.jsonl`, `history.events.jsonl`) that union-merge cleanly line-by-line; the canonical `registry.json`/`history.json` are regenerated from them. Tests prove two-branch merges remain parseable and lossless (sections 5.2, 11, 13; FR-027, FR-023).
- **Issue 5 - Canonical vs legacy casting layout (RESOLVED).** `.squad/casting/` is canonical and authoritative; root-level `casting-*.json` is legacy. On read: only legacy -> read + flag migration; both divergent -> detect + report `layout_conflict` and offer migration (never silently pick one); writes always go to `.squad/casting/` (section 5.2; FR-011, FR-019, FR-023). No longer an open question.
- **Issue 6 - Recast semantics (RESOLVED).** Augment adds members and retires none; recast re-derives the roster, retains overlap, adds new members, and RETIRES dropped members (registry `status=retired`, charter moved to `.squad/agents/_alumni/{name}/charter.md`, name reserved forever, recast snapshot recorded). A recast never silently overwrites or renames an existing agent (section 6.5; FR-021, FR-022). No longer an open question.

**Remaining risks (mitigated):**

- **Structured model output reliability (risk).** US2/US3 depend on the model returning a parseable role selection. Mitigation: ground the model in the catalog menu, validate the structured output, and on parse failure surface `model_run_failed` with zero writes rather than guessing.
- **Catalog completeness (risk).** Role archetypes and per-universe name pools must be broad enough to avoid frequent overflow. Mitigation: ship the squadboard-derived ~40 archetypes and the repo's universe capacities; overflow degrades gracefully to unnamed members (FR-017).
- **Sidecar/canonical drift (risk).** A hand-edited canonical `registry.json`/`history.json` could drift from the sidecars. Mitigation: the sidecars are the source of truth and the canonical JSON is always regenerated from them on read/write; a divergence is reported (FR-023), never silently trusted.
- **Migration/compat (note).** Teams created outside this app may use the legacy root layout, omit `policy.json`, or use legacy fields; the reader seeds a default policy from the catalog when absent, maps legacy field names, and offers migration to the canonical layout (section 5.2, FR-019). Retired members in an external registry are honored as reserved names.

---

## 15. Definition of Done

- All 34 functional requirements (FR-001..FR-034), the five user stories' acceptance scenarios, and success criteria SC-001..SC-007 are implemented and verified by the tests in section 13.
- `dotnet build scaffolders.sln` and `dotnet test scaffolders.sln` pass; `cd apps/web && npm run lint && npm run build && npx vitest run` passes.
- Every casting and team-management capability is reachable identically from the CLI (`scaffolder team ...`) and the Web UI (`TeamPage` + casting wizard + sync panel), with the API as the single source of truth (Principle IV, FR-028, SC-006).
- **Documentation updated (constitution Development Workflow gate):**
  - `docs/reference/api.md`: new "Team casting" and "Team management" endpoint sections (catalog, proposals, team read/update, sync) plus any new configuration keys.
  - `docs/reference/cli.md`: the `scaffolder team ...` command tree (`scenarios`, `cast`, `proposal`, `show`, `charter`, `member`, `sync`).
  - `docs/reference/web.md`: the `TeamPage`, casting wizard, and sync panel routes/flows.
  - `docs/reference/events.md`: that model-assisted casting (free-text/analysis) emits observable run steps under an existing run, and any casting-specific event types added.
- No emojis in any shipped surface (Principle VIII); no mocks/fakes/placeholders at any layer (Principle VII); RAI gate and working-directory boundary verified by Rai and Seraph reviews (Principles IX, X).
- The plan passes the single rubber-duck review gate (constitution "Plan and Spec Review Process") before commit.
