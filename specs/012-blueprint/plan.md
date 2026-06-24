# Implementation Plan: Blueprints (Feature 012)

**Spec**: `specs/012-blueprint/spec.md` (33 firm FRs, zero clarification markers).
**Status**: Plan only. No production code written, no branch switch, no commit.
**Authored by**: Tank (Backend/Runtime), grounded against the current `009-backlog-kanban-board` tree.

This plan grounds every step in actual files and types. Where the spec text references a
path that the codebase has since renamed, the plan calls it out and uses the current path.

---

## 0. Grounded reality and corrections to spec assumptions

These facts were verified in the current tree and shape the plan:

1. **Project-state directory is `.agentweaver/`, not `.scaffolders/`.** The spec text (FR-011,
   US1) says `.scaffolders/workflows/`. The `workflow-folder-rename` change moved workflow and
   review-policy discovery/materialization to `.agentweaver/workflows/` and
   `.agentweaver/review-policies/`. Verified: `WorkflowRegistry.WorkflowsRelativePath` =
   `.agentweaver/workflows`, `DefaultWorkflowTemplate.RelativeFilePath` =
   `.agentweaver/workflows/default.yaml`; same for review policies. **The plan uses
   `.agentweaver/` throughout.**

2. **Casting writes to `.squad/`, a separate tree.** `CastingService.ConfirmProposalAsync`
   (`apps/Agentweaver.Api/Casting/CastingService.cs`) persists the cast as files under the
   project working directory `.squad/` (team.md, `.squad/agents/{name}/charter.md`, routing,
   registry/history canonical JSON) via `SquadWriter`. Cast members are NOT stored in SQLite.

3. **Project creation does not invoke casting today.** `ProjectService.CreateBlankAsync` and
   `CreateFromGitHubAsync` create the project record, init/clone git, and call
   `TryMaterializeDefaultWorkflow` + `TryMaterializeDefaultReviewPolicy`. They never call
   `CastingService`. Blueprint instantiation must explicitly drive casting after project
   creation, inside a new orchestration method.

4. **Casting input is role ids resolved against the catalog.** `CastingService.ProposeManualCastAsync(projectId, roleIds, universeOverride, ct)`
   resolves each role id against `CatalogReader.LoadAllRoles()`. Per the v1 governing rule (FR-040),
   every Blueprint roster — predefined, authored/forked, AND file-sourced — references ONLY catalog
   role ids and introduces no bespoke roles, so the roster is catalog-resolved before casting; a
   bespoke/unknown role id is rejected at validation (FR-037). The blueprint casting entrypoint
   (`ProposeBlueprintCastAsync`, D1) receives the catalog-resolved `Role` records; no inline/bespoke
   role is cast in v1 (a new role must be added to the catalog first, FR-040).

5. **Team templates are embedded JSON catalog resources.** `packages/Agentweaver.Squad/Catalog/CatalogReader.cs`
   loads `catalog.manifest.json`, `groupings/*.json`, `roles/*.json`, `charters/*.md` from
   embedded resources under `packages/Agentweaver.Squad/Catalog/Resources/`. The model types are
   `TeamTemplate(Id, Title, Description, IReadOnlyList<Role> Roles)` and
   `Role(Id, Title, Summary, DefaultModel, Capabilities, Responsibilities, Boundaries)` in
   `packages/Agentweaver.Squad/Model/CastingModels.cs`. Four templates ship today:
   `product-feature-delivery`, `quick-software-development`, `content-authoring-and-research`,
   `azure-feature-delivery`. The Blueprint cast facet generalizes `TeamTemplate` (FR-003).

6. **Dual-source pattern reference.** Feature 010 review policies merge in-code built-ins
   (`BuiltInReviewPolicies` / `DefaultReviewPolicyTemplate`) with per-project files in
   `.agentweaver/review-policies/` via `ReviewPolicyRegistry` (load-once + explicit `Sync`).
   `WorkflowRegistry` mirrors this for workflows. Blueprint dual-source (FR-031) is richer: it
   needs predefined catalog files plus a SQLite-backed store for authored/forked blueprints (not
   per-project files), because a Blueprint is a global, instantiable construct, not project-local
   state.

7. **MCP parity pattern.** `apps/Agentweaver.Mcp/Tools/WorkflowTools.cs` ( `[McpServerToolType]`,
   tools `workflows_list` / `workflow_get` / `workflows_sync`) calls the API over HTTP loopback
   via an injected `AgentweaverApiClient`. Tools are assembly-discovered
   (`.WithToolsFromAssembly()` in the MCP `Program.cs`). New Blueprint tools follow this exactly.

8. **Schema migration pattern.** `apps/Agentweaver.Api/Infrastructure/SqliteDb.cs` uses
   `TryAlterAsync(connection, "ALTER TABLE ... ADD COLUMN ...", ct)` (swallows the "column
   exists" `SqliteException`) for additive columns, and `CREATE TABLE IF NOT EXISTS` in
   `SchemaSql` for new tables. Feature 010 added `projects.default_workflow_id` and
   `projects.active_review_policy_name` this way.

---

## 1. Architecture overview

```
Predefined catalog (files)   Authored/forked (SQLite store)   Supplied file (import / one-shot)
        |                              |                                  |
        |                              |        BlueprintSerializer + validate -> upsert into store
        |                              |                                  |
        +---------- BlueprintRegistry (load-once + Sync, merge + validate) ----------+
                                       |
                          BlueprintValidator (refs resolve, exactly-one-default,
                                       roles>=1, no persona names)
                                       |
        Web (BlueprintsPage + Sync)    BlueprintEndpoints (API-first)    MCP (BlueprintTools)
                                       |
                          BlueprintInstantiationService
              (ProjectService.CreateFromBlueprintAsync orchestration)
                 |                 |                    |                     |
           create project    cast roles ->        materialize default    bind review +
           record +          named members        workflow into          sandbox policy
           provenance        (CastingService)     .agentweaver/workflows  (project columns +
           (new columns)                                                   sandbox store)
```

New owned namespace: `apps/Agentweaver.Api/Blueprints/**`. Reuses Feature 010 Workflows and
ReviewPolicies, the Casting layer, `ProjectService`, `SqliteDb`, and `IProjectStore`.

---

## 2. Phase 1 — Domain model and persistence

**New files** under `apps/Agentweaver.Api/Blueprints/`:

- `Blueprint.cs` — the domain records (API-layer, mirroring how `WorkflowDefinition.cs` lives in
  `apps/Agentweaver.Api/Workflows/`):
  - `Blueprint` { `string Id`, `string Name`, `string? Description`, `string Version`,
    `BlueprintSource Source` (enum `Predefined` | `Authored`), `BlueprintCast Cast`,
    `BlueprintWorkflows Workflows`, `BlueprintPolicies Policies` }.
  - `BlueprintCast` { `IReadOnlyList<BlueprintRole> Roles` } — FR-002/FR-006.
  - `BlueprintRole` { `string Name`, `string Responsibilities` (or `IReadOnlyList<string>` to
    match `Role.Responsibilities`), `string? ModelPreference` } — FR-006. No persona/universe
    name field exists, which structurally enforces FR-008 at the type level.
  - `BlueprintWorkflows` { `IReadOnlyList<string> WorkflowIds`, `string DefaultWorkflowId` } —
    FR-010. The ids reference Feature 010 workflow definition ids (e.g. `default`).
  - `BlueprintPolicies` { `string ReviewPolicyName`, `string SandboxPolicyName` } — FR-013/FR-014.
    Review policy is referenced by name (Feature 010 binding model). Sandbox policy reference:
    see design decision D2.
- `BlueprintLoadResult.cs` — `{ bool IsValid, Blueprint? Definition, string? Error, string Source }`,
  mirroring `WorkflowLoadResult.cs` / `ReviewPolicyLoadResult.cs`.
- `BlueprintValidator.cs` — pure validation (FR-008/FR-009/FR-010/FR-016/FR-023): at least one
  role; exactly one default workflow and it is within `WorkflowIds`; reject any role carrying a
  persona/universe name (validate against `UniversePools.Pools` value sets to catch leakage,
  FR-008); reject declared Ceremonies/Seed-memory facets (FR-020). Reference resolution (workflow
  ids resolve in the project/registry, review policy resolves, sandbox policy resolves) runs at
  instantiation against the concrete project context (FR-016).

**Persistence — authored/forked store (FR-031):**

- New SQLite table via `SchemaSql` `CREATE TABLE IF NOT EXISTS blueprints (...)` in
  `apps/Agentweaver.Api/Infrastructure/SqliteDb.cs`:
  ```sql
  CREATE TABLE IF NOT EXISTS blueprints (
      blueprint_id   TEXT PRIMARY KEY,
      name           TEXT NOT NULL,
      description     TEXT,
      version        TEXT NOT NULL,
      forked_from     TEXT,            -- source blueprint id when forked (FR-005)
      definition_json TEXT NOT NULL,   -- serialized facets (cast/workflows/policies)
      owner           TEXT NOT NULL,
      created_at      TEXT NOT NULL,
      updated_at      TEXT NOT NULL
  );
  ```
  Authored blueprints serialize their three facets to `definition_json` (System.Text.Json,
  snake_case, same convention as the DTOs). This keeps the column set stable as facets evolve.
- New `IBlueprintStore` (in `packages/Agentweaver.Domain/`) + `SqliteBlueprintStore`
  (`apps/Agentweaver.Api/Infrastructure/SqliteBlueprintStore.cs`), mirroring
  `IProjectStore`/`SqliteProjectStore`: `InsertAsync`, `GetAsync`, `ListAsync`, `UpdateAsync`,
  `DeleteAsync`.

**Persistence — project provenance (FR-018/FR-030):**

- Additive columns on `projects` via `TryAlterAsync` in `SqliteDb.cs`:
  - `ALTER TABLE projects ADD COLUMN source_blueprint_id TEXT;`
  - `ALTER TABLE projects ADD COLUMN source_blueprint_version TEXT;`
- Add `string? SourceBlueprintId` and `string? SourceBlueprintVersion` to
  `packages/Agentweaver.Domain/Project.cs` (alongside `DefaultWorkflowId` /
  `ActiveReviewPolicyName`).
- Extend `SqliteProjectStore` insert/select mapping for the two new ordinals (the existing
  pattern: write `DBNull.Value` when null; read `r.IsDBNull(n) ? null : r.GetString(n)`), and add
  `IProjectStore.SetSourceBlueprintAsync(ProjectId, string id, string version, DateTimeOffset, ct)`
  following `UpdateDefaultWorkflowAsync`. Provenance is write-once at instantiation; no drift
  re-materialization (FR-030).

---

## 3. Phase 2 — Catalog and dual-source discovery (FR-031, FR-005, FR-022)

**Predefined catalog files.** Ship the four v1 blueprints as embedded resources, mirroring
`CatalogReader`'s resource model so they live with the catalog they generalize:

- Directory: `packages/Agentweaver.Squad/Catalog/Resources/blueprints/*.json` (embedded), plus a
  `blueprints.manifest.json` listing their ids. Each file declares the three facets (cast roles,
  workflow ids + default, policy names) and a `version`. JSON matches the catalog's existing
  format choice (the catalog is JSON, not YAML).
- Extend `CatalogReader` with `LoadBlueprints()` / `LoadBlueprint(id)` returning the new
  `Blueprint` records, reusing its embedded-resource read path. The cast facet of each predefined
  blueprint reuses existing `roles/*.json` content (Phase 7) so FR-003 holds by construction.

**Registry (load-once + Sync).** New `apps/Agentweaver.Api/Blueprints/BlueprintRegistry.cs`,
modeled on `WorkflowRegistry` / `ReviewPolicyRegistry`:

- Load-once: on first access, read predefined (via `CatalogReader.LoadBlueprints()`) plus authored
  (via `IBlueprintStore.ListAsync`), validate each with `BlueprintValidator`, cache the merged set
  with per-blueprint validation status.
- `SyncAsync()`: re-read both sources and replace the cache (FR-022; load-on-start / explicit-sync,
  not file-watch, not per-heartbeat). An in-flight instantiation holds the `Blueprint` value it
  resolved at start, so a concurrent Sync does not affect it (FR-022/SC-007).
- Merge rule: predefined and authored are distinct id-spaces; on id collision, authored shadows
  predefined only if the product later allows it — for v1, ids are unique and a collision is a
  validation error surfaced per-blueprint (FR-023).
- Predefined are read-only (FR-005): the store only ever holds authored/forked rows, so "edit in
  place" of a predefined is structurally impossible; the only mutation path for a predefined is
  fork (Phase 4).

**Fork-to-author flow (FR-005).** `BlueprintRegistry.Fork(sourceId, newName, owner)` materializes
the resolved source `Blueprint` (predefined or authored) into a new authored row with a fresh id,
`forked_from = sourceId`, `version` reset to an initial value, leaving the source untouched
(SC-003).

---

## 3b. Phase 2.5 — File-based extensibility: one schema, three sources (FR-034..FR-041)

This phase makes Blueprints portable: a Blueprint becomes a self-contained file that can be
exported, shared, version-controlled, handed to another user, and imported or instantiated
directly. The same schema backs all three sources (embedded catalog, authored store, supplied
file), so export/import round-trips (FR-034).

**One schema, one serializer.** The on-disk file is the serialized `Blueprint` facets, identical to
the embedded catalog format and to `blueprints.definition_json`. Concretely:

- Add `apps/Agentweaver.Api/Blueprints/BlueprintDocument.cs` — the wire/file shape (`id`, `name`,
  `description`, `version`, `cast.roles[]`, `workflows{ ids[], default }`, `policies{ review,
  sandbox }`) with snake_case `[JsonPropertyName]`, the same record used by `BlueprintDtos`. The
  JSON shape is consistent with the catalog grouping/role JSON (`groupings/*.json` has `id`,
  `title`, `roles[]`); `cast.roles[]` entries are **catalog role id strings** (e.g.
  `"backend-engineer"`), optionally an object `{ "role": "<catalog-role-id>", "model_preference":
  "<model>" }` to carry the optional per-role model preference (FR-027/FR-039).
- Add `apps/Agentweaver.Api/Blueprints/BlueprintSerializer.cs` — `Serialize(Blueprint) -> string`
  and `TryParse(string) -> BlueprintLoadResult`. Primary format is **JSON** (the catalog is JSON,
  not YAML, per Phase 2). YAML input MAY be accepted as a convenience by sniffing the leading token
  and routing to YamlDotNet (already referenced via `YamlSandboxPolicyStore`); export always emits
  JSON to keep one canonical round-trip artifact. A predefined blueprint read via
  `CatalogReader.LoadBlueprint(id)` serializes to a file and parses back to an equal `Blueprint`
  (FR-034 round-trip), because all three paths share `BlueprintDocument`.
- Required fields: `id`, `name`, `version`, `cast.roles` (>=1), `workflows.ids` (>=1) +
  `workflows.default` (member of ids), `policies.review`, `policies.sandbox`. Optional:
  `description`, per-role `model_preference`. Validation is `BlueprintValidator` (Phase 1) plus
  reference resolution (below); the file path adds no new validation rules, only a new input surface.

**Three sources, one pipeline.** `BlueprintRegistry` (Phase 2) already merges embedded + store. The
file is not a fourth cache tier; it is an input that resolves into the store. Two flows:

- (a) **Import-from-file (durable, FR-035).** `BlueprintRegistry.ImportAsync(document, owner, ct)`:
  parse via `BlueprintSerializer`, validate + resolve refs, then persist as an authored row via
  `IBlueprintStore.InsertAsync` (or upsert on id collision with the caller's intent). After import it
  is an ordinary authored blueprint: discoverable, forkable, instantiable. This is the extend path.
- (b) **Instantiate-directly-from-document (one-shot, FR-036).** Accept an inline document on the
  create-from-blueprint call, then parse + validate + resolve + instantiate.

**Design decision D3 — does one-shot persist?** Recommendation: **yes, one-shot upserts a store copy
as part of instantiation.** `CreateFromBlueprintAsync` (Phase 3) records provenance as
`source_blueprint_id` + `source_blueprint_version` (FR-018). For a project to remain traceable and to
honor one-time-copy / no-drift (FR-030), `source_blueprint_id` must resolve to an existing record.
So a one-shot document is upserted into the authored store (idempotent on `{id, version}`) before
provenance is written, and a project never points at a non-existent blueprint. This makes import (a)
and one-shot (b) converge: both end with a stored blueprint and a project that traces to it; the only
difference is whether the user explicitly asked to keep it (import) or it was persisted as a side
effect of instantiate (one-shot). Flagged as **D3** for @sabbour to confirm; the alternative (store
only `{id, version}` + a content hash and do not persist the body) is rejected because it leaves
provenance pointing at a body that exists nowhere, weakening FR-018/FR-030.

**Validation / fail-closed (FR-037).** On import or instantiate-from-file, in order:
1. Schema parse (`BlueprintSerializer.TryParse`) — a malformed document fails with a parse-scoped
   message.
2. `BlueprintValidator` structural checks (>=1 role, exactly one default within ids, no persona
   names, no Ceremonies/Seed facets).
3. Reference resolution **in this environment**: every `workflows.ids` entry resolves in the
   workflow source / `WorkflowRegistry`; `policies.review` resolves in `ReviewPolicyRegistry` /
   `DefaultReviewPolicyTemplate`; `policies.sandbox` resolves in `SandboxPolicyPresets` (D2); and
   **every `cast.roles` entry resolves to a known, castable catalog role id** via
   `CatalogReader.LoadAllRoles()` (FR-040). A roster role id that is not present in the catalog
   groupings (a bespoke/unknown role) is rejected with a role-scoped error; the file path introduces
   no inline/bespoke roles. Adding a new role is out of band: it must be added to the catalog first
   (so it is castable by `CastingService` and usable by Blueprints) before any Blueprint may
   reference it.
Any failure aborts with a single error listing every unresolved reference; no store row is written
and no project is created (all-or-nothing, consistent with FR-016/FR-019 and the
`CreateFromBlueprintAsync` rollback span in Phase 3).

---

## 4. Phase 3 — Instantiation (Blueprint -> Project)

**New orchestration**: add `CreateFromBlueprintAsync` to
`apps/Agentweaver.Api/Projects/ProjectService.cs` (this plan assigns `ProjectService.cs` to the
Blueprint owner for this method; coordinate so no other change collides):

```
Task<Project> CreateFromBlueprintAsync(
    string name, string blueprintId, string requestedPath,
    string? defaultProvider, string? defaultModelCopilot, string? defaultModelFoundry,
    string owner, CancellationToken ct)
```

Sequence (parity with the existing create paths plus casting and provenance, all-or-nothing):

1. Resolve and re-validate the blueprint from `BlueprintRegistry` (capture the value now so a
   later Sync cannot change this instantiation — FR-022). Fail fast with a reference-scoped
   message if invalid (FR-016/FR-023).
2. Create the base project exactly as `CreateBlankAsync` does (workspace, `_gitInit.InitBlank`,
   store insert), reusing the existing compensation/rollback (delete the app-created working dir
   on any failure). This yields the `Project` and its `WorkingDirectory`.
3. Materialize the blueprint's default workflow into `<workingDir>/.agentweaver/workflows/`
   (FR-011). For the predefined `default` workflow this is the existing
   `DefaultWorkflowTemplate.TryMaterialize`. For a non-default-named workflow, resolve its YAML
   via the workflow source and write it through the same materialize path. Set
   `projects.default_workflow_id` via `IProjectStore.UpdateDefaultWorkflowAsync` to the
   blueprint's designated default.
4. Bind the review policy by name: `IProjectStore.UpdateActiveReviewPolicyAsync(project.Id,
   blueprint.Policies.ReviewPolicyName, ...)` (FR-013). Optionally materialize the named policy
   file into `.agentweaver/review-policies/` if it is a predefined-template policy, reusing
   `DefaultReviewPolicyTemplate.TryMaterialize` for the `default` policy.
5. Bind the sandbox policy (FR-014): write the referenced sandbox policy for the project's
   repository path through `YamlSandboxPolicyStore.SetPolicyAsync` (it writes
   `<repo>/.agentweaver/settings.yml`). Per FR-025 the bound policy may only constrain within the
   runtime guarantees; the store/runtime remain the enforcement point (the Blueprint cannot relax
   a mandatory boundary). See design decision D2 for how a sandbox policy is named/referenced.
6. Cast the roles (FR-007): drive `CastingService` over the blueprint's roles to produce one named
   member per role, then confirm so `.squad/` is written. See design decision D1.
7. Record provenance (FR-018): `IProjectStore.SetSourceBlueprintAsync(project.Id, blueprint.Id,
   blueprint.Version, ...)`.
8. On any failure in steps 3-7, run cleanup: delete the app-created working dir and the project
   row, so no half-created project remains (FR-019/SC edge cases). This extends the existing
   rollback to cover the casting/materialization span.

Single blueprint per project, no switch or compose (FR-032): there is no "change blueprint"
endpoint and provenance columns are write-once.

**Design decision D1 — casting blueprint roster roles (catalog-only in v1).** Per FR-040, every v1
Blueprint roster (predefined, authored/forked, file-sourced) references ONLY catalog role ids. The
casting entrypoint `CastingService.ProposeBlueprintCastAsync(projectId, IReadOnlyList<Role> roles,
universeOverride, ct)` (confirmed below) is still added and called by `CreateFromBlueprintAsync`,
but in v1 the `roles` it receives are always **catalog-resolved**: `CreateFromBlueprintAsync`
resolves each roster role id against `CatalogReader.LoadAllRoles()` into the catalog `Role` record
(carrying `Title`/`Summary`/`Responsibilities`/`DefaultModel`) and passes those in, applying the
blueprint's optional per-role `model_preference` override where present. It then allocates names via
the existing `UniverseAllocator.AllocateNames(universe, reservedNames, count)` / `UniversePools.Pools`
and confirms via `ConfirmProposalAsync`, writing `.squad/` identically to today (SC-002). A roster
role id absent from the catalog is rejected at validation (FR-037/FR-040) before casting, never cast
as a bespoke role. If a role's model preference is absent, casting uses the catalog `DefaultModel`
or the system default with no blueprint-level failure (FR-027/Acceptance US1.5). The method takes
`IReadOnlyList<Role>` (the catalog-resolved roster) so v1 enforces catalog-only; expanding the role
set is an out-of-band catalog change, never a Blueprint operation (FR-040/FR-049).

**Design decision D2 — sandbox policy reference.** `YamlSandboxPolicyStore` keys policies by
repository path, not by name; there is no named-sandbox-policy catalog today. Two options:
(a) v1 references a small set of in-code named sandbox presets (e.g. `default`, `restricted`) that
map to concrete `SandboxPolicy` values written at instantiation; or (b) the blueprint carries an
inline sandbox policy reference resolved to `SandboxPolicy.Default(repoPath)` for v1 with the name
recorded for provenance. Recommend (a): add a tiny `apps/Agentweaver.Api/Blueprints/
SandboxPolicyPresets.cs` mapping a preset name to a `SandboxPolicy` factory, so FR-014/FR-025 are
satisfied with named, auditable presets that only constrain within the runtime guarantees. Flag
for @sabbour: this is the one place the spec's "reference, do not redefine" meets a store that has
no name concept yet.

---

## 5. Phase 4 — API endpoints (API-first, FR-024)

New `apps/Agentweaver.Api/Blueprints/BlueprintEndpoints.cs` with `MapBlueprintEndpoints`
(extension method registered in `Program.cs` by the integrator — see Program.cs report).
Owner-authorized consistently with `WorkflowDefinitionEndpoints` / `ProjectEndpoints` (the
`IsOwner` pattern). DTOs in `apps/Agentweaver.Api/Blueprints/BlueprintDtos.cs` (snake_case
`[JsonPropertyName]`, mirroring `WorkflowDtos.cs`).

Routes:

- `GET  /api/blueprints` — list predefined + authored with validation status (FR-021/FR-023).
- `GET  /api/blueprints/{id}` — single blueprint with facets (404 missing).
- `POST /api/blueprints` — author a new blueprint (validate, persist to store) (FR-005).
- `POST /api/blueprints/{id}/fork` body `{ name }` — fork (FR-005); 403 if not owner of an
  authored source; predefined are forkable by anyone.
- `PUT  /api/blueprints/{id}` — update an authored blueprint (403/409 for predefined; predefined
  are read-only, FR-005).
- `DELETE /api/blueprints/{id}` — delete an authored blueprint (predefined cannot be deleted).
- `POST /api/blueprints/sync` — re-read both sources, refresh cache + status (FR-022).
- `POST /api/blueprints/{id}/instantiate` body `{ name, requested_path, provider/model fields }` —
  calls `CreateFromBlueprintAsync`; returns the created project DTO with provenance (FR-017/FR-018).
- `POST /api/blueprints/import` body = a Blueprint document (the file contents, JSON; YAML accepted) —
  parse + validate + resolve, persist to the authored store, return the stored blueprint DTO
  (FR-035). 422 with a reference-scoped error list on unresolved refs (FR-037).
- `GET  /api/blueprints/{id}/export` — returns the blueprint serialized as a downloadable file
  (`BlueprintSerializer.Serialize`, `application/json`, `Content-Disposition` attachment). Works for
  predefined and authored, enabling the export->import round-trip (FR-034).
- Create-from-document: the instantiate route accepts EITHER a stored id (the `{id}/instantiate`
  route above) OR an inline document. Add `POST /api/blueprints/instantiate` (no id segment) whose
  body carries `{ blueprint_document, name, requested_path, provider/model fields }`; it upserts the
  document into the store (D3), then calls `CreateFromBlueprintAsync` (FR-036). Equivalent net state
  to import-then-instantiate.
- `POST /api/blueprints/validate` body = a Blueprint document — parse + validate + resolve references
  WITHOUT persisting; returns the same Validation Result shape as import (valid / invalid with a
  reference/role-scoped error list). Used by the Web authoring/import flow and generation preview
  (FR-047/FR-037). 200 with `{ valid, errors[] }`.
- `POST /api/blueprints/generate` body `{ description }` — server-side LLM generation (provider stays
  GitHub Copilot, Principle II). Produces a candidate Blueprint assembled from existing catalog roles
  only (the generator does not create roles), then validates the Blueprint against the schema + role
  constraint. Returns `{ blueprint }`; fails closed on validation failure, persisting no partial
  Blueprint and creating no role, including when the description maps to no fitting catalog role
  (FR-044/FR-045/FR-046). See design decision D4.

**Project creation gains an optional Blueprint field (FR-042/FR-043).** The existing project-create
routes (`POST /api/projects` blank and the from-GitHub create route, backed by
`ProjectService.CreateBlankAsync` / `CreateFromGitHubAsync`) accept an OPTIONAL Blueprint input:
EITHER `blueprint_id` (predefined/authored/stored) OR an inline `blueprint_document`
(mutually exclusive). When present, after the project record + working dir exist, creation runs the
same apply path as instantiation: resolve/validate the Blueprint (FR-037/FR-040), cast the roster,
materialize the default workflow, bind review + sandbox policies, and record provenance — sharing the
`CreateFromBlueprintAsync` seeding span so the roster/workflow/policy seeding is identical to a direct
instantiate. When absent, create behavior is unchanged (no Blueprint-seeded roster). Any failure
rolls back the half-created project (FR-019).

All file parsing, validation, persistence, and instantiation run server-side (Principle III); the
request carries only the document/ids and the response carries only data.

**Design decision D4 — LLM generation over the fixed catalog (FR-044/FR-045).** Add
`apps/Agentweaver.Api/Blueprints/BlueprintGenerator.cs` that takes a description, calls the model via
the existing agent runtime (GitHub Copilot, Principle II), and emits a candidate `BlueprintDocument`.
The generator is prompted to select roles from the catalog only (the role union from
`CatalogReader.LoadAllRoles()` is supplied as context) and reuse the closest-fitting role; it does not
create roles. The candidate Blueprint is then validated through the same
`BlueprintValidator` + reference-resolution path as import (FR-037), so its roster references only
known catalog role ids (FR-040); a candidate that references a non-catalog role is rejected (422) and
persists nothing. Because no role minting occurs, generation needs no writable role store; the
catalog stays the embedded, read-only, curated role library (FR-049). Expanding that library to cover
new archetypes is an out-of-band catalog change, independent of generation.

---

## 6. Phase 5 — MCP parity tools (FR-024, Principle IV)

New `apps/Agentweaver.Mcp/Tools/BlueprintTools.cs` (`[McpServerToolType]`), assembly-discovered,
calling the API over the injected `AgentweaverApiClient` (the `WorkflowTools` pattern exactly):

- `blueprints_list` -> `GET /api/blueprints`
- `blueprint_get` -> `GET /api/blueprints/{id}`
- `blueprints_sync` -> `POST /api/blueprints/sync`
- `blueprint_instantiate` -> `POST /api/blueprints/{id}/instantiate`
- `blueprint_fork` -> `POST /api/blueprints/{id}/fork`
- `blueprint_import` -> `POST /api/blueprints/import` (accepts a blueprint document string) (FR-035/FR-038)
- `blueprint_export` -> `GET /api/blueprints/{id}/export` (returns the document string) (FR-034/FR-038)
- `blueprint_instantiate_document` -> `POST /api/blueprints/instantiate` (inline document) (FR-036/FR-038)
- `blueprints_validate` -> `POST /api/blueprints/validate` (validate a document, no persist) (FR-047/FR-048)
- `blueprint_generate` -> `POST /api/blueprints/generate` (`{ description }` -> `{ blueprint }`) (FR-044/FR-048)

Project creation parity: the optional Blueprint field on project creation (`blueprint_id` OR inline
`blueprint_document`, FR-042/FR-043) is exposed on the existing project-create MCP
path so applying a Blueprint at creation is reachable from both clients (FR-048).

Minimum for parity per the task: list/get/instantiate plus import/export/instantiate-from-document,
validate, and generate; fork/sync added for full coverage. No business logic in the tool layer;
SC-006/SC-012 (identical resulting state from both clients) is met because both clients call the same
endpoints.

---

## 7. Phase 6 — Web (Blueprints management page + create-from-blueprint)

Model on `apps/web/src/pages/WorkflowsPage.tsx` and the Team page's Sync affordance
(`apps/web/src/components/SyncPanel.tsx`), inside the Feature 011 shell
(`apps/web/src/components/shell/AppShell.tsx`, `LeftNav.tsx`, `navConfig.tsx`).

- New `apps/web/src/pages/BlueprintsPage.tsx`: list predefined + authored with validation status;
  detail view of the three facets (roles, workflows + default, policy names); fork / author / edit
  / delete actions for authored; a Sync button reusing the Sync pattern. Read-only rendering for
  predefined.
- **File extensibility on the page (FR-038)**: an **Import** affordance (file upload or paste a
  document, POST `/api/blueprints/import`); an **Export** action on each blueprint (download from
  `GET /api/blueprints/{id}/export`); and, in the create-from-blueprint flow, an option to upload/
  paste a Blueprint document and create a project from it (POST `/api/blueprints/instantiate`).
  Errors surface the server's reference-scoped validation message verbatim (no client validation).
- **Generation + validation affordances (FR-044/FR-047/FR-048)**: a **Generate from description**
  control (`POST /api/blueprints/generate`) that returns a candidate Blueprint for
  the user to review before applying; a **Validate** action that posts a document to
  `POST /api/blueprints/validate` and renders the Validation Result. Both are thin clients over the
  server endpoints.
- **Apply a Blueprint at project creation (FR-042/FR-043)**: extend the project-creation UX (the
  gallery/create flow, `ProjectGalleryPage.tsx` / `CastingWizardPage.tsx`) on BOTH the blank and
  from-GitHub paths with an OPTIONAL Blueprint selector — pick a predefined/authored Blueprint by id,
  upload/paste a Blueprint document, or generate one — passed as the optional Blueprint field on the
  project-create call. Omitting it keeps today's create behavior.
- Add a nav entry in `navConfig.tsx` and route wiring in `AppShell.tsx`.
- API client methods in `apps/web/src/api/client.ts` for the new endpoints (`/api/blueprints`,
  `/api/blueprints/generate`, `/api/blueprints/validate`, the optional Blueprint field on
  project-create; snake_case payloads).

The web slice is owned by the Web engineer; this plan specifies the contract it binds to.

---

## 8. Phase 7 — The four v1 predefined Blueprints (FR-033)

Each references the Feature 010 `default` workflow as its default (id `default`), the `default`
review policy (RAI + Rubber-duck; `DefaultReviewPolicyTemplate.DefaultPolicyName`), and a named
sandbox preset (per D2). Predefined blueprint rosters use ONLY roles already present in the catalog
groupings (`Catalog/Resources/groupings/*.json`); blueprints introduce no bespoke roles. Any future
role must be added to the catalog so it is usable by BOTH casting and blueprints; reuse existing
roles where possible. The allowed role union (from the four groupings) is: `lead-researcher`,
`writer`, `editor`, `lead-pm`, `customer-researcher`, `prototype-designer`, `lead-architect`,
`core-implementer`, `docs-writer`, `frontend-engineer`, `backend-engineer`, `ai-safety-reviewer`.

**1. Content authoring** (`blueprint-content-authoring`, generalizes
`content-authoring-and-research`):
- Roles: `lead-researcher`, `writer`, `editor`.
- Default workflow: `default`. Review policy: `default`. Sandbox preset: `restricted`
  (network off; docs work needs no shell network).

**2. Product management** (`blueprint-product-management`, generalizes
`product-feature-delivery`):
- Roles: `lead-pm`, `customer-researcher`, `prototype-designer`, `docs-writer`.
- Default workflow: `default`. Review policy: `default`. Sandbox preset: `restricted`.

**3. Software Development** (`blueprint-software-development`, generalizes
`quick-software-development`, enriched to a complete delivery team):
- Roles: `lead-architect`, `frontend-engineer`, `backend-engineer`, `docs-writer`.
- Default workflow: `default`. Review policy: `default` for v1 (a human-review-opt-in policy can
  be authored later per FR-032; SD is the natural candidate). Sandbox preset: `default`
  (shell on, network off, destructive-command gating on).

**4. Product & Software Delivery** (`blueprint-pm-and-software-development`, combines the edited
Product management and Software Development rosters, deduplicated):
- Roles: `lead-pm`, `customer-researcher`, `prototype-designer`, `lead-architect`,
  `frontend-engineer`, `backend-engineer`, `docs-writer` (`docs-writer` appears once).
- Default workflow: `default`. Review policy: `default`. Sandbox preset: `default`
  (matches Software Development: shell on, network off, destructive-command gating on).

All rosters are non-empty (FR-009), use abstract role ids only (no persona names, FR-006/FR-008),
and resolve to existing catalog grouping roles, so each ships as a complete, instantiable Cast +
default Workflow + policy references (FR-033).

---

## 9. Phase 8 — Test plan (live SQLite, no mocks; Principle VII)

Unit tests (`tests/Agentweaver.Tests/Blueprints/`):

- `BlueprintValidatorTests` — zero roles fails (FR-009); zero/multiple default workflows fail
  (FR-010); persona-name leakage fails (FR-008); declared Ceremonies/Seed facets rejected
  (FR-020); valid blueprint passes (SC-004 cases).
- `BlueprintRegistryTests` — dual-source merge (predefined + authored from an actual
  `SqliteBlueprintStore` over a temp SQLite db); invalid authored blueprint excluded with a
  scoped error while valid ones remain (FR-023/SC-004); `Sync` refreshes the set; an instantiation
  holding a pre-Sync value is unaffected (SC-007).
- `BlueprintCastAllocationTests` — `ProposeBlueprintCastAsync` over a role set produces one named
  member per role with names from `UniversePools`; absent model preference falls back to system
  default without failure (FR-007/FR-027/US1.5).
- `BlueprintSerializerTests` — `Serialize` then `TryParse` round-trips a predefined and an authored
  blueprint to an equal `Blueprint` (FR-034/SC-008); a malformed document fails parse with a scoped
  message; JSON and (if accepted) YAML inputs parse to the same `Blueprint`.

Integration tests (actual stores and services):

- `CreateFromBlueprintEndToEndTests` — instantiate each predefined blueprint over a temp working
  dir + temp SQLite; assert: project row created with `source_blueprint_id` + version (FR-018);
  `.agentweaver/workflows/default.yaml` materialized and `default_workflow_id` set (FR-011);
  `active_review_policy_name` bound (FR-013); sandbox `.agentweaver/settings.yml` written
  (FR-014); `.squad/team.md` lists one named member per role (FR-007/SC-001); a run can be
  constructed (SC-001). Casting failure / unresolved reference leaves no project row or working
  dir (FR-019, half-create guard).
- `BlueprintForkTests` — fork a predefined, change a facet, assert the fork is independent and the
  source unchanged (SC-003); predefined cannot be edited in place or deleted (FR-005).
- `BlueprintEndpointsHttpTests` + `BlueprintMcpParityTests` — list/get/instantiate yield identical
  state from the API and from `AgentweaverApiClient` (the MCP path), SC-006.
- `BlueprintFileImportTests` — import a valid document persists an authored row that is then
  instantiable (FR-035); import a document whose roster references a bespoke/unknown role id (not in
  the catalog groupings) fails closed with a role-scoped error and writes no row (FR-037/FR-040);
  import a document with an unresolved workflow/policy ref fails closed with a reference-scoped error
  and writes no row (FR-037/SC-008); export an existing blueprint then import the exported bytes
  yields an equivalent blueprint (FR-034 round-trip).
- `CreateFromDocumentEndToEndTests` — one-shot instantiate from an inline document creates a project
  AND upserts a store row (D3), with `source_blueprint_id` + version resolving to that row (FR-036/
  FR-018); a one-shot with an unresolved ref leaves no project row, no working dir, and no store row
  (FR-037/FR-019); import-then-instantiate and one-shot reach equivalent project state.
- `ApplyBlueprintAtCreationTests` — create a blank project with a `blueprint_id` and assert roster
  cast, default workflow materialized, review + sandbox policies bound, provenance recorded
  (FR-042/SC-010); repeat on the from-GitHub path after clone (FR-042); create with an inline
  document applies identically (FR-043); create WITHOUT a Blueprint reproduces today's
  default project state (FR-042/SC-010); an applied Blueprint that fails validation or casting leaves
  no half-created project (FR-019/FR-043).
- `BlueprintGenerateTests` — `POST /api/blueprints/generate` with a description that maps onto catalog
  roles returns a validated Blueprint whose roster is drawn entirely from catalog roles
  (FR-044/FR-045/SC-011); a description needing a capability no catalog role covers is rejected with a
  role-scoped error, creates no role, and persists no partial Blueprint (FR-045/FR-046/SC-011); a
  generation whose validation fails persists nothing (FR-046/SC-011).
- `BlueprintValidateEndpointTests` — `POST /api/blueprints/validate` returns a valid result for a
  conformant document and a reference/role-scoped error list for an unresolved/bespoke-role document,
  WITHOUT persisting anything (FR-047).
- Extend `BlueprintMcpParityTests` to cover `blueprints_validate`, `blueprint_generate`, and the
  optional Blueprint field on project creation, asserting identical resulting state from the API and
  the MCP path (FR-048/SC-012).

Commands:
- Backend: `dotnet build agentweaver.sln -c Release`; `dotnet test tests\Agentweaver.Tests -c Release`.
- Web: `npm run build` and `npx vitest run` in `apps/web`.

---

## 10. Phase 9 — Agent / work split and parallelization map

Slices and suggested owners:

- **B1 Domain + storage** (Tank / backend): `Blueprint.cs`, `BlueprintLoadResult.cs`,
  `BlueprintValidator.cs`, `blueprints` table + `IBlueprintStore`/`SqliteBlueprintStore`, project
  provenance columns + `Project.cs` + `IProjectStore` method + `SqliteProjectStore` mapping.
- **B2 Catalog + registry + file serializer** (Tank / backend, depends on B1 types):
  `CatalogReader.LoadBlueprints`, embedded `blueprints/*.json`, `BlueprintRegistry` (load-once +
  Sync + fork + `ImportAsync`), `BlueprintDocument` + `BlueprintSerializer` (one schema / three
  sources, FR-034/FR-035/FR-037).
- **B3 Casting integration** (Casting owner, depends on B1 role types): D1
  `ProposeBlueprintCastAsync` in `CastingService`.
- **B4 Instantiation** (Tank / backend, depends on B1+B2+B3): `CreateFromBlueprintAsync` +
  `SandboxPolicyPresets` (D2) + rollback span.
- **B5 API endpoints + DTOs** (Tank / backend, depends on B2+B4): `BlueprintEndpoints`,
  `BlueprintDtos`, including `import` / `{id}/export` / `instantiate` (inline document) routes
  (FR-034..FR-038), `validate` (FR-047), and the optional Blueprint field (`blueprint_id` OR inline
  document) on both project-creation routes (FR-042/FR-043).
- **B5b LLM generation** (Tank / backend, depends on B2+B5, see D4):
  `POST /api/blueprints/generate`, the generator that assembles rosters from existing catalog roles
  only (no role creation) and validates the candidate against the schema + role constraint
  (FR-044/FR-045/FR-046).
- **B6 MCP tools** (MCP owner, depends on B5 routes existing): `BlueprintTools`, including
  `blueprint_import` / `blueprint_export` / `blueprint_instantiate_document`, `blueprints_validate`,
  `blueprint_generate`, and the optional Blueprint field on project-create parity (FR-048).
- **B7 Web** (Web owner, depends on B5 contract): `BlueprintsPage`, nav wiring, create-from-
  blueprint flow, apply-at-creation selector on both project-create paths (FR-042), file import/
  export/instantiate-from-document affordances, generate-from-description + validation affordances
  (FR-044/FR-047), `client.ts`.
- **B8 Predefined blueprints + their tests** (Tank / content, depends on B2): the four JSON files
  (Phase 7) and `CreateFromBlueprintEndToEndTests`.

Parallelization:

- Hard dependencies: B1 -> B2 -> B4 -> B5; B3 needed before B4; B5 before B6 and B7.
- Concurrent: B3 (casting) runs alongside B2 once B1 types land. B6 (MCP) and B7 (Web) run
  concurrently once B5 routes/DTOs are defined (they only need the contract). B8 JSON files can be
  authored alongside B2; their tests wait on B4.
- Critical path: B1 -> B2 -> B4 -> B5 -> (B6 || B7). Integrator wires `Program.cs` after B5.

---

## 11. Program.cs wiring report (integrator)

No `Program.cs` edits are made by this plan's owners. The integrator must add:

- DI (alongside the Feature 010 registrations):
  - `builder.Services.AddSingleton<SqliteBlueprintStore>();` and bind `IBlueprintStore` to it
    (matching how `SqliteProjectStore`/`IProjectStore` are registered).
  - `builder.Services.AddSingleton<BlueprintRegistry>();`
  - If `CreateFromBlueprintAsync` needs `BlueprintRegistry`/`CastingService` injected into
    `ProjectService`, add those to the `ProjectService` constructor registration (confirm at
    implementation; expected: `ProjectService` gains `BlueprintRegistry` + `CastingService`
    dependencies).
- Endpoint mapping: `app.MapBlueprintEndpoints();` next to `app.MapWorkflowDefinitionEndpoints();`.
- MCP `Program.cs`: no change (tools are assembly-discovered via `.WithToolsFromAssembly()`).

---

## 12. Constitution compliance checklist

- **API-first (III)**: all validation/casting/materialization/policy resolution server-side;
  clients call endpoints only (Phase 4-7).
- **MCP + Web parity (IV)**: every list/read/fork/instantiate/sync/import/export/instantiate-from-
  document capability exposed in both `BlueprintEndpoints` and `BlueprintTools` over the same API
  (Phase 4-6, FR-038, SC-006).
- **GitHub Copilot provider (II)**: per-role model preference influences within-provider selection
  only; never selects another provider (FR-027, D1 default-model fallback).
- **Human accountability (IX)**: instantiation runs through `ProjectService` under the owner;
  provenance recorded; runs remain attributable (FR-026).
- **Runtime governance not weakened (X, XI)**: sandbox/human-approval/audit stay enforced by the
  runtime; the referenced sandbox policy only constrains within mandatory guarantees (FR-025, D2).
- **No mocks/fakes (VII)**: tests use live SQLite, the actual `CastingService`, actual registries
  (Phase 9).
- **No emojis (VIII)**: enforced in blueprint definitions, catalog listings, validation messages,
  logs, and UI; covered by a catalog content assertion mirroring `CatalogReaderTests`'s no-emoji
  check.

---

## Open items to confirm at implementation (flagged for @sabbour)

**Confirmed by @sabbour (folded into this plan):**

- **D1 casting entrypoint — CONFIRMED.** Add `CastingService.ProposeBlueprintCastAsync(projectId,
  IReadOnlyList<Role> roles, universeOverride, ct)`, called by `CreateFromBlueprintAsync` (NOT inline
  in `ProjectService`). Used throughout Phase 3 / Phase 9 (B3).
- **D2 sandbox naming — CONFIRMED.** Add named `SandboxPolicyPresets` (e.g. `restricted`, `default`,
  ...) mapping a preset name to a concrete `SandboxPolicy` factory; blueprints reference a preset by
  name. Used in Phase 3 / Phase 7 / Phase 2.5 validation.
- **Project-state dir — CONFIRMED `.agentweaver/`** (not `.scaffolders/`). Spec text drift corrected
  in `spec.md` this revision; plan uses `.agentweaver/` throughout.

**New decision raised this revision:**

- **D3 one-shot persistence (file extensibility) — recommendation: one-shot upserts a store copy.**
  Instantiating directly from a supplied document (FR-036) upserts that document into the authored
  store (idempotent on `{id, version}`) before provenance is written, so `source_blueprint_id`
  always resolves and FR-018/FR-030 hold. The alternative (store only id+version+content-hash, no
  body) is rejected. Confirm acceptable, or choose the alternative.

**Still to confirm at implementation:**

1. **`ProjectService` constructor growth** — `CreateFromBlueprintAsync` likely needs
   `BlueprintRegistry` + `CastingService`; confirms a Program.cs DI change for `ProjectService`.
2. **File format reach** — primary format is JSON (matches the catalog); YAML input accepted as a
   convenience, export always JSON. Confirm whether YAML input is wanted for v1 or JSON-only.
