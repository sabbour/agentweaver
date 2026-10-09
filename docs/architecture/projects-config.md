# Projects & Config service candidate

`Agentweaver.Projects.Config` is an unpublished .NET 10 service candidate. It owns project lifecycle, typed configuration revisions, platform runtime defaults, and immutable run-selection snapshots in the `projects_config` PostgreSQL schema. Source presence is not evidence of deployment or release.

```mermaid
sequenceDiagram
    autonumber
    participant O as Orchestrator
    participant C as Projects & Config
    participant P as Provider catalog
    participant DB as PostgreSQL
    O->>C: Accept run ID, expected revisions, immutable context
    C->>DB: Recheck active membership and Orchestrator role; lock run ID and project/platform heads
    DB-->>C: Project configuration and platform defaults at expected revisions
    C->>C: Select project model or platform default; fail closed if unavailable
    C->>P: Resolve allowed provider candidates and capabilities
    P-->>C: Candidate metadata or explicit resolution failure
    C->>C: Check egress subset and run-limit narrowing
    C->>DB: Append fingerprinted immutable run-selection snapshot
    DB-->>C: Commit snapshot with exact revision inputs
    C-->>O: Effective run-selection context (not a resource pin)
    Note over O,P: After provisioning, the consumer negotiates a real resource and pins its identity, generation, and capabilities.
```

## API surface

All routes require a validated bearer token with exactly one `sub`, the required OAuth scopes, and the service's audience. Projects & Config is the sole live owner of issuer-and-subject memberships and resource-role assignments. It resolves the current caller to one active tenant membership and current resource-role assignments in its own database. A tenant selector can choose among existing memberships but cannot create authority; any signed tenant assertion must agree with the selected membership. Project reads require an assigned Owner, Contributor, or Viewer role (or tenant administrator); project writes require Owner or tenant administrator; project creation requires an existing tenant administrator and does not auto-assign an Owner. Platform defaults require an assigned platform administrator. Run-selection reads and accepts require an assigned project Orchestrator role as well as matching signed project/run bindings when present. Every privileged request rechecks current membership and role state, so revocation takes effect without refreshing the broker token.

The Identity broker validates upstream identity but does not forward upstream tenant or role claims or assign application roles. OAuth scopes remain necessary route permissions, but they are not authority by themselves. Role claims and request headers never create authority; missing, ambiguous, stale, or foreign memberships fail closed.

| Route | Operation |
| --- | --- |
| `POST /api/projects` | Create an active project and its initial configuration revision. |
| `GET /api/projects` | List projects visible through current project roles; a tenant administrator sees all projects in that tenant. |
| `GET /api/projects/{projectId}` | Read an authorized project summary. |
| `GET /api/projects/{projectId}?runId={runId}` | Read the project summary for an exact bound run after rechecking the caller's current project read role. |
| `PATCH /api/projects/{projectId}` | Change project name or lifecycle state using an expected project revision. Archive rather than physically delete. |
| `GET /api/projects/{projectId}/configuration?revision={n}` | Read the current or a retained configuration revision. |
| `PUT /api/projects/{projectId}/configuration` | Append a configuration revision using an expected configuration revision. |
| `GET /api/casting/templates` | List the built-in scenario templates available to seed a reviewed team proposal. |
| `GET /api/catalog/roles` | List the role definitions available to scenario and manual team proposals. |
| `POST /api/projects/{projectId}/casting/proposals` | Create a reviewed proposal from a complete project configuration draft. |
| `POST /api/projects/{projectId}/casting/proposals/scenario` | Create a version-bound proposal from a scenario template. |
| `POST /api/projects/{projectId}/casting/proposals/manual` | Create a version-bound proposal from selected role IDs. |
| `GET /api/projects/{projectId}/casting/proposals` | List casting proposals for an authorized project. |
| `GET /api/projects/{projectId}/casting/proposals/{proposalId}` | Read a proposal and its draft for review. |
| `PATCH /api/projects/{projectId}/casting/proposals/{proposalId}` | Amend a pending proposal against its expected draft revision. |
| `POST /api/projects/{projectId}/casting/proposals/{proposalId}/confirm` | Atomically confirm a current proposal as a new configuration revision. |
| `POST /api/projects/{projectId}/casting/proposals/{proposalId}/reject` | Reject a proposal without changing project configuration or run state. |
| `GET /api/projects/{projectId}/casting/export?revision={n}` | Export charter/casting configuration from the current or a retained revision. |
| `POST /api/projects/{projectId}/casting/import` | Stage an explicit owner-authorized charter/casting import for review. |
| `GET /api/platform/runtime-defaults` | Read the platform administrator's current defaults. |
| `PUT /api/platform/runtime-defaults` | Append platform defaults using an expected revision. |
| `PUT /api/projects/{projectId}/runs/{runId}/selection` | Accept or idempotently replay a run-selection request from the Orchestrator. |
| `GET /api/projects/{projectId}/runs/{runId}/selection` | Read the immutable selection snapshot for the authorized Orchestrator. |
| `GET /api/authorization/context` | Return the validated caller's current effective permissions through versioned contract 1. |

Revision conflicts and a reused run ID with a different request return conflict responses. Invalid configuration and run-selection context return client errors; missing projects and inaccessible tenant-owned projects do not disclose their existence.

### Reviewed casting proposals and explicit transfers

Casting templates and role definitions are authenticated catalog reads. Scenario proposal creation accepts an `expectedConfigurationRevision` and a `templateId`; manual creation accepts the expected revision and a non-empty list of catalog `roleIds`. Both create a pending proposal from the configuration at that revision, replacing only the proposed `AgentCharters` and `Casting` while retaining unrelated project settings. Manual inputs reject unknown, repeated, and reserved orchestration roles. The original full-draft proposal route remains available for callers that already compose a complete `ProjectConfiguration`.

Authorized project readers can list and read proposals; project writers can amend a draft using its expected draft revision and explicitly confirm or reject it. Confirmation rechecks current project authority and the base configuration revision while holding the project lock, then appends the configuration revision and marks the proposal confirmed in the same transaction. A changed project revision conflicts rather than silently replacing a newer roster. Rejection records only the proposal transition; it does not write configuration or run state. Accepted run selections continue to refer to their immutable configuration revision.

Charter/casting transfer is a separate explicit owner operation. Export identifies the source project and configuration revision and includes a format version and content digest. Import checks that format and digest, validates the proposed configuration through the ordinary owner-authorized configuration validator, preserves target settings outside charters/casting, and creates a pending proposal rather than applying it. Incompatible or tampered transfers return an actionable client error. The service does not mirror Squad files, reconcile them in pre-commit hooks, or export them after runs.

The authorization-context route requires `api.read`, accepts the existing optional `X-Agentweaver-Tenant` selector, and resolves only the authenticated issuer and subject. It rejects query parameters, including caller-subject and role selectors, and purpose-bound tokens. It applies the validated service audience, OAuth scopes, and optional project/run bindings before returning grouped effective permissions with membership and role revisions; it does not return assignment rows or a transferable credential. Responses use `Cache-Control: no-store`. Resource services request this context for each privileged operation and do not maintain separate membership/role records, caches, or authorization pins. The contract-1 wire DTOs are shared in `Agentweaver.Abstractions`; this shares serialization types, not authority data or database access.

## Selection and persistence rules

- A project model-selection reference takes precedence over the platform reference. If the explicit project reference is absent from the supplied immutable selection context, run selection fails closed; it does not fall back to the platform model.
- Project configuration may name an optional `defaultWorkflowId`. Orchestrator accepts it only when it resolves to a server-registered workflow in the accepted selection's authorized catalog; project workflow references narrow that catalog when present. An unknown or unauthorized default falls back to the validated built-in workflow. The field is an identifier, not a workflow definition or an authority grant.
- `SecretRef` values may be persisted as references. Credential values are not part of configuration or run-selection snapshots.
- `ModelSelectionSettings.SourceMode` selects `hostedCopilot` or `byok` in the platform or project configuration. Runtime owner bindings retain that accepted mode. There is no personal model-provider precedence or fallback.
- Hosted selection requires a stable `ConnectionId` and prohibits `CredentialReference`. Identity owns the current connection revision, GitHub user identity, freshness, revocation, and exact protected secret version. Rotation does not change the accepted selection bytes or hash. A different account, owner, or scope requires a new connection and selection.
- BYOK selection requires an exact `CredentialReference` and prohibits `ConnectionId`. Orchestrator exposes that reference only when the confirmed WorkPlan model matches the accepted selection. The reference is not grant authority and contains no credential value.
- Legacy records retain their original omitted or null fields and hashes. Hosted records with a pinned secret require migration to a connection before runtime use. Missing or incompatible source modes fail closed.
- Optional `ProjectConfiguration.SourceControl` contains one repository identity and either exact API/checkout SecretRefs or `authMode: "githubApp"` with an Identity connection ID. A short-lived Identity selection code is supplied only in the initial run-scoped Orchestrator `/pin` request, never accepted project/run configuration or the durable pin. Identity stores only its hash, binds its first run-bound use to one project, and permits later token minting only for that project using the hash; the durable pin retains the hash and exact repository/permission metadata. App mode rejects API/checkout SecretRefs; an optional webhook SecretRef remains independent. The run-selection's complete `projectConfiguration` snapshot is the only source for these values; consumers never substitute mutable current settings for an older accepted run. Null omission preserves legacy configuration shape. Project configuration contains no OAuth, selection-code, or installation-token values; Identity stores only OAuth SecretRefs and returns installation tokens transiently.
- Project provider overrides only select provider IDs permitted by the platform catalog. Exclusive, ordered-composite, platform-singleton, layered and meter-keyed Cost seams use the existing `ProviderResolver`; Application Hosting remains unsupported. Cost requirements name a source explicitly, and multiple distinct meter sources may appear in one selection. The snapshot preserves source keys, adapter/options versions and advertised/required capabilities.
- The service requires project egress rules to be a subset of the platform baseline and checks each run's required destinations. Project run limits may only reduce configured platform limits.
- The immutable run-selection snapshot carries typed platform egress baseline, optional project narrowing, admitted run needs, the effective allowlist, and each layered Network Policy provider's layer, options revision, and required/advertised capabilities. The Environment consumer rechecks the current authorization context before reading this snapshot and compiles the final intent.
- Project configuration revisions, platform runtime revisions, and run-selection snapshots are append-only at the database boundary. Project metadata and revision heads remain mutable under optimistic revision checks.
- Memberships and role assignments live in this service's schema and are provisioned or revoked only through a privileged source path; public APIs cannot self-grant roles. Revocation uses expected revisions, records an immutable audit event, and cannot remove the last explicit project Owner. Runtime database credentials have SELECT-only access to membership, assignment, and audit tables; casting row locks use the fixed security-definer authority function, whose exact EXECUTE grant is checked at startup.
- Run-selection acceptance serializes a run ID and rechecks the current Orchestrator assignment before committing. Replays return the originally stored snapshot, but still require current authorization; snapshots never pin membership or roles.
- Provider resolution returns candidates, not resource bindings. Resource identity, generation, negotiated capabilities, and final pins are owned by the consumer after provisioning.

The service does not own provider catalog registrations, Git or workflow materialization, remote MCP, UI state, run journals, or secret redemption. The catalog owner supplies its snapshot through the required `ProjectsConfig:ProviderCatalog` startup configuration section, containing `Registrations`, `Defaults`, `PermittedOverrides`, `OrderedSelections`, `LayerSelections`, and optional `MeterSourceSelections` entries (`MeterSource`, `ProviderId`); provider registrations include adapter version, options schema/revision, hosting pattern, enabled state, and advertised capabilities. The service validates the supplied catalog through `ProviderCatalog.Create` and fails startup if it is missing or invalid rather than using an empty catalog. The snapshot is fixed for the service process lifetime, and run requests carry their own immutable context revision. Database migrations run through the separate `--migrate` operation; normal startup verifies that migrations have already been applied.

Projects & Config and Knowledge use the shared loader from `Agentweaver.Providers`.
Both validate the supplied catalog through `ProviderCatalog.Create` and fail startup
if it is missing or invalid rather than using an empty catalog. The snapshot is fixed
for each service process lifetime; run requests carry an immutable context revision,
and consumers fail closed if a selected candidate differs from the supplied catalog.
Database migrations run through the separate `--migrate` operation; normal startup
verifies that migrations have already been applied.

Cost candidates remain selections, not producer authority. An opaque model-selection
reference plus a selected meter key does not prove the effective SDK model/source.
That proof belongs to the canonical model/producer owners. Existing selection GET
still requires current Orchestrator `ReadRunSelection` authority, not project-admin
write permission; adding Cost cardinality does not widen authorization.

Run-selection accept and read responses are `no-store`. The current
authorization-context GET also uses `no-store`; it returns grouped permissions
and membership/role revisions, not assignment rows or transferable credentials.
The run-bound project-summary GET requires exactly one `runId` query parameter,
an exact signed project/run binding, and a current project Owner, Contributor, or
Viewer role (or tenant administrator); it returns `no-store` so consumers can
recheck authorization before forwarding run-scoped data.
See [Environment egress](./environment-egress.md) for how a privileged consumer
uses both current authorization and the immutable selection without a separate
membership or role cache.

## Validation

Run the pure validator tests and the PostgreSQL integration test from the repository root:

```powershell
dotnet test tests/Agentweaver.Projects.Config.Tests/Agentweaver.Projects.Config.Tests.csproj --configuration Release
```

The PostgreSQL tests use Testcontainers. They verify tenant-scoped run selection, revision and idempotency behavior, model fail-closed semantics, provider candidate metadata, SELECT-only runtime authority grants, CAS revocation and audit retention, the last-Owner invariant, and database append-only triggers.
