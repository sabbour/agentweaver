# Projects and configuration

## 1–4. Goals, constraints, context and strategy

Use this page to understand how Projects & Config manages project authority,
revisioned settings, and the immutable configuration snapshot for a run. A
project is not a repository checkout: Environment and Source Control own the
resources used to execute its accepted configuration.

Revisioned edits and immutable selections let a run retain its inputs while
configuration evolves. The corresponding constraint is strict: stale edits
conflict, and an unavailable accepted model cannot silently become another model.

## 5. Building blocks and authority

### Understand project authority and revisions

Projects & Config stores project identity, lifecycle, team, skills, model
selection, and provider choices. It separates editable configuration from an
accepted run selection. Editing advances a revision; accepting a run freezes
the exact inputs for that run. Later edits do not rewrite the snapshot.

| Source | Responsibility |
| --- | --- |
| `ProjectAuthorization.cs` | Current membership, roles, scopes, and effective permissions |
| `ProjectConfiguration.cs` | Typed settings, validation, limits, and egress narrowing |
| `ProjectsConfigService.cs` | Project lifecycle, revision writes, and run selection |
| `NativeAcsPolicyEndpoints.cs` and `NativeAcsPolicyStore.cs` | Canonical Platform ACS Policy API and immutable policy source |
| `ModelProviderDraftService.cs` and `ModelProviderDraftRecords.cs` | Customer-provided model-provider metadata in Draft heads and immutable revisions |
| `ProjectsConfigDbContext.cs` and `ProjectConfigRecords.cs` | Service-owned persistence |
| `ProjectsConfigCastingService.cs` and `ProjectsConfigCastingTransferService.cs` | Reviewed team proposals and transfers |
| `ProjectBlueprintCatalog.cs`, `AgentSuiteBlueprintConverter.cs`, and `BlueprintPackages/` | 0.x blueprints, their AgentSuite conversion, and the ported 0.x package validator |
| `ProjectSkillContentService.cs` and `ProjectMarketplaceSourceService.cs` | Imported skill content and source ownership |
| `ProjectsConfigMigrator.cs` | Explicit migration and runtime permission checks |

All paths in this table are under
`services/projects-config/Agentweaver.Projects.Config/`.

The service owns issuer-and-subject tenant memberships and resource-role
assignments in the `projects_config` schema. OAuth scopes are necessary route
permissions, not a substitute for those assignments.

- Project reads require a current project role or tenant administrator.
- Writes require Owner or tenant administrator authority.
- Project creation requires a current TenantAdmin assignment and atomically
  assigns the authenticated creator an active Owner role with an audit row.
  Stale or revoked TenantAdmin authority returns 403. If the existing
  role-write grants are unavailable, creation returns 503 and rolls back the
  project, configuration, Owner assignment, and audit row.
- Platform defaults require platform administrator authority.
- Run selection requires the project Orchestrator role and its scopes.

`GET /api/authorization/context` returns a versioned, no-store projection of
effective permissions. A signed project/run binding limits its target; headers
and token role claims cannot create authority.

The runtime database principal reads authority records but cannot mutate them.
Setup and revocation use a separate privileged path. Ordinary service requests
do not bootstrap membership.

## 6. Runtime view

### Store one canonical ACS Policy source

Projects & Config exposes Platform Policy read, validation, and compare-and-swap
write routes. It uses one head per scope and keeps the Policy ID of an existing
head. Only an empty scope gets the server-owned `canonical` Policy ID. Each
artifact contains all policy domains; the API does not accept a
scope or domain selector from the caller.

The read route returns the current artifact only after the store checks its
content hash. The validation route does not write state. Head writes are off by
default; the write route returns `503` until `NativeAcsPolicy:HeadWritesEnabled`
is `true`. A retry of a saved request first gets its receipt, after a current
authority check and without new validation. An enabled write validates the
complete artifact before the existing serializable transaction appends the
revision, idempotency receipt, audit record, and outbox event. The transaction
rechecks current membership and role revisions before commit. The schema
enforces one head per scope. If old data has duplicate heads, the migration
stops and names the affected scopes and policy IDs. It does not select, delete,
or change a policy record.

If `ACS_OPA_PATH` is missing, the service still starts. Policy validation then
returns `503` and the other service functions continue.

Reads require `api.read` and a current unbound PlatformAdmin grant. They do not
require `projects.admin`. Writes also require `projects.admin` and the
`WritePlatformRuntimeDefaults` permission. This API stores source only; it
does not activate ACS or change the v4 Policy path. The
[native ACS contract](../reference/contracts.md#native-acs-artifact-and-evaluation-contract)
defines its full error and request contract.

### Edit configuration safely

Project metadata, project configuration, and platform runtime defaults have
separate revision histories. Configuration contains typed model references,
legacy provider-override fields, charters, casting, workflow references, skill
pins, egress narrowing, run limits, and optional Source Control settings.
Current project-level selection is available for model configuration only.
Provider choices for other platform components are resolved from platform
configuration; legacy project provider-override values remain loadable but are
ignored for new selections. Writes must send empty provider-override arrays,
and the service carries stored legacy values forward unchanged. The
configuration choices read lists enabled model references from a read-only
copy of the runtime model-binding eligibility fields, loaded and validated at
startup. Without that copy, model choices are reported as unavailable. Saved
model references are listed separately and are not treated as eligible.

When you update a revision, supply the expected current revision. The service
appends a new revision and advances the head. If another writer wins first,
your update conflicts instead of overwriting its change.

Customer-provided model-provider metadata has a separate Draft-only store.
Every read and write requires `api.read` and `projects.admin`. Platform requests
must be unbound and made by a PlatformAdmin. Project requests must match the
exact project binding, have no run binding, and be made by a project Owner or
PlatformAdmin. Draft metadata does not change model eligibility or accepted run
selections. This source slice adds no HTTP route, activation, credentials, or
rate-card data. See
[issue #2635](https://github.com/sabbour/agentweaver/issues/2635).

### Accept a run selection

Before acceptance, you need an active project, current Orchestrator authority,
configured platform defaults, and the current configuration revisions. The
selection request is made by Orchestrator with a run ID and revisioned selection
context.

1. The service starts a serializable transaction and locks current authority,
   project and platform heads, and the run ID.
2. It rechecks the Orchestrator role. A new run requires an active project and
   configured platform defaults.
3. It selects the project model or platform default and checks that the
   reference is available in the supplied context.
4. `ProviderResolver` resolves candidates from the enabled platform catalog.
   Project-supplied provider override fields do not participate in resolution.
5. Egress requirements must fit the platform baseline and project narrowing.
   Project run limits cannot widen platform limits.
6. The service writes the immutable effective selection and request fingerprint,
   then rechecks authority before commit.

An exact retry returns the saved snapshot without reopening mutable settings. A
different request for the same run ID conflicts. Archiving blocks new
acceptance; it does not erase an accepted snapshot.

The result contains provider candidates, not provisioned resource pins. Each
resource owner negotiates its actual backend and persists the consumer binding.
See [Providers and models](./providers-models.md).

For supported setting names and project setup, see the
[runtime configuration reference](../reference/runtime-configuration.md) and
[provider configuration guide](../guide/configure-providers.md).

### Review team and skill changes

Casting proposals stage a complete configuration draft for review. Scenario
templates, blueprints, and manual role selections produce version-bound proposals.
Confirmation appends a configuration revision only while the proposal and
configuration remain current. Rejection leaves configuration unchanged.
The [Gateway casting routes](../reference/api.md#casting-through-the-gateway)
expose the same review flow, templates, roles, and explicit transfers. The owner
still checks current permissions and revision fences; import is limited to
1 MiB at both entry points and never applies a team before confirmation.

A blueprint proposal lists each skill binding it can't apply, with a stable
code, and never changes whether a skill is enabled. Blueprints are delivered
as AgentSuite packages; see
[Blueprints on AgentSuite](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/agent-application-bundles.md#blueprints-on-agentsuite).

Skills use imported content revisions and digests, with assignments to project
agents. Configuration validation checks these pins. An accepted run reads its
assigned imported revisions, not later content from a marketplace source.
Marketplace browsing and imported project content have different owners and
lifetimes.

### Recover from configuration errors

Stale revisions, unavailable model references, invalid skill pins, and
incompatible provider selections stop acceptance. The service does not replace
them with a different default. Revoked authority blocks privileged operations
even when a token has not expired.

Ordinary startup verifies applied migrations and runtime database privileges;
it does not apply migrations. Retained revisions and idempotent selection replay
provide recovery without rewriting accepted history.
Fresh operator initialization of owner-service databases is separate: it verifies the operator can
assume the dedicated migration owner before creating runtime roles, and runs
Projects migrations plus reviewed runtime-grant reconciliation in one
transaction. Runtime principals never own this schema or apply its DDL.

## 7–9. Deployment, shared concepts and decisions

Projects runs as a shared HTTPS owner over `projects_config`. Runtime reads
authority records without mutating membership or applying DDL; separate
privileged setup and migration paths perform those operations. See the
[deployment view](./infra-deployment.md) for identity and grant prerequisites.

Provider selection follows the [provider view](./providers-models.md);
Identity tokens identify actors but do not replace Projects roles.
The [system ADR links](./00-system-overview.md#9-architecture-decisions)
retain actual statuses. This view does not define a new compatibility policy
or turn an accepted configuration into a deployed resource.

## 10–12. Quality, risks and glossary

| Context and stimulus | Response and measure | Verification state |
| --- | --- | --- |
| Concurrent writers edit the same head revision | One advances; the stale request conflicts instead of overwriting | Projects PostgreSQL revision tests |
| An identical run-selection request is retried | Return the immutable snapshot; changed input for the run conflicts | Selection transaction tests |
| A user rejects a staged team proposal | Leave active configuration unchanged | Casting proposal tests |

Legacy provider overrides remain loadable but do not drive new non-model
selection. Clients must not present saved legacy values as eligible choices;
the Projects owner must preserve that distinction in configuration reads.

**Terms:** revision is editable-history position; selection is an immutable
run snapshot; model eligibility is current startup-validated configuration,
not any previously saved reference.

## Related content

- [Projects & Config contract](../architecture/projects-config.md)
- [Skill marketplace sources](../architecture/skill-marketplace.md)
- [Contracts and endpoints](../reference/contracts.md)
- [Platform components](../guide/platform-components.md)
- [Identity and secrets](./identity-secrets.md)
