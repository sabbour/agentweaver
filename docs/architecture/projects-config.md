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
    C->>DB: Authorize tenant; lock run ID and project/platform heads
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

All routes require a validated bearer token with exactly one `sub` and `tenant_id` claim. Project management is owner-scoped; a platform administrator can manage platform defaults. The Orchestrator role can accept and read run selections only for projects in the caller's tenant.

The Identity broker derives `tenant_id` from a unique `tid` in the configured, validated upstream OIDC identity and forwards only the `platform_admin` and `orchestrator` role values into broker-signed access tokens. The Projects API validates its configured issuer and audience before reading the shared Identity claim contract. It ignores request headers and OAuth scopes when determining tenant or roles; missing or ambiguous tenant context and unknown roles fail closed.

| Route | Operation |
| --- | --- |
| `POST /api/projects` | Create an active project and its initial configuration revision. |
| `GET /api/projects` | List projects in the caller's tenant; owners see their projects and platform administrators see the tenant's projects. |
| `GET /api/projects/{projectId}` | Read an authorized project summary. |
| `PATCH /api/projects/{projectId}` | Change project name or lifecycle state using an expected project revision. Archive rather than physically delete. |
| `GET /api/projects/{projectId}/configuration?revision={n}` | Read the current or a retained configuration revision. |
| `PUT /api/projects/{projectId}/configuration` | Append a configuration revision using an expected configuration revision. |
| `GET /api/platform/runtime-defaults` | Read the platform administrator's current defaults. |
| `PUT /api/platform/runtime-defaults` | Append platform defaults using an expected revision. |
| `PUT /api/projects/{projectId}/runs/{runId}/selection` | Accept or idempotently replay a run-selection request from the Orchestrator. |
| `GET /api/projects/{projectId}/runs/{runId}/selection` | Read the immutable selection snapshot for the authorized Orchestrator. |

Revision conflicts and a reused run ID with a different request return conflict responses. Invalid configuration and run-selection context return client errors; missing projects and inaccessible tenant-owned projects do not disclose their existence.

## Selection and persistence rules

- A project model-selection reference takes precedence over the platform reference. If the explicit project reference is absent from the supplied immutable selection context, run selection fails closed; it does not fall back to the platform model.
- `SecretRef` values may be persisted as references. Credential values are not part of configuration or run-selection snapshots.
- Project provider overrides only select provider IDs permitted by the platform catalog. Exclusive, ordered-composite, platform-singleton, and layered provider seams use the existing `ProviderResolver`; unsupported cardinalities fail explicitly. The snapshot preserves adapter/options versions and advertised/required capabilities.
- The service requires project egress rules to be a subset of the platform baseline and checks each run's required destinations. Project run limits may only reduce configured platform limits.
- Project configuration revisions, platform runtime revisions, and run-selection snapshots are append-only at the database boundary. Project metadata and revision heads remain mutable under optimistic revision checks.
- Run-selection acceptance serializes a run ID and holds share locks on the project and platform revision heads through persistence, so concurrent edits cannot change the accepted input set. Replays return the originally stored snapshot.
- Provider resolution returns candidates, not resource bindings. Resource identity, generation, negotiated capabilities, and final pins are owned by the consumer after provisioning.

The service does not own provider catalog registrations, Git or workflow materialization, remote MCP, UI state, run journals, or secret redemption. The catalog owner supplies its snapshot through the required `ProjectsConfig:ProviderCatalog` startup configuration section, containing `Registrations`, `Defaults`, `PermittedOverrides`, `OrderedSelections`, and `LayerSelections`; provider registrations include adapter version, options schema/revision, hosting pattern, enabled state, and advertised capabilities. The service validates the supplied catalog through `ProviderCatalog.Create` and fails startup if it is missing or invalid rather than using an empty catalog. The snapshot is fixed for the service process lifetime, and run requests carry their own immutable context revision. Database migrations run through the separate `--migrate` operation; normal startup verifies that migrations have already been applied.

## Validation

Run the pure validator tests and the PostgreSQL integration test from the repository root:

```powershell
dotnet test tests/Agentweaver.Projects.Config.Tests/Agentweaver.Projects.Config.Tests.csproj --configuration Release
```

The PostgreSQL test uses Testcontainers. It verifies the migration, tenant-scoped run selection, revision and idempotency behavior, model fail-closed semantics, provider candidate metadata, and database append-only triggers.
