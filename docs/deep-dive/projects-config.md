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
| `NativeAcsPolicyEndpoints.cs`, `PlatformPolicySourceEndpoints.cs`, `PlatformPolicySourceTls.cs`, and `NativeAcsPolicyStore.cs` | Public Platform and Project ACS Policy reads, private Environment source read, and immutable policy source |
| `ModelProviderDraftService.cs`, `ModelProviderDraftRecords.cs`, and `ModelProviderCredentialPublicationEndpoints.cs` | Customer-provided provider Drafts, immutable revisions, and private credential publication |
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

Events uses `GET /api/authorization/projects/{projectId}/model-pricing-search`
for the Gateway's Azure Retail price route. Projects resolves the caller's
current membership and roles on every request. It requires `api.read`, rejects
conflicting project and run bindings, and returns `204` only for current
PlatformAdmin or exact Owner authority on the requested project. The response
uses `Cache-Control: no-store`; Events does not search Azure Retail when
Projects denies or cannot answer.

The runtime database principal reads authority records but cannot mutate them.
Setup and revocation use a separate privileged path. Ordinary service requests
do not bootstrap membership.

## 6. Runtime view

### Store one canonical ACS Policy source

Projects & Config exposes Platform and Project Policy source, history, and
exact-revision reads. Platform also has validation and preparation routes. The
store uses one head per scope and keeps the Policy ID of an existing head. Only
an empty Platform scope gets the server-owned `canonical` Policy ID. Each
artifact contains all policy domains; these reads do not accept a scope or
domain selector from the caller.

The read route returns the current artifact only after the store checks its
content hash. The validation route does not write state. Head writes are off by default. A
new request returns `503` until `NativeAcsPolicy:HeadWritesEnabled` is `true`.
Retries of committed writes still return their receipt after a current
authority check. Preparation retries also check that the expected head is
current. An enabled write validates the complete artifact, then stores an
immutable preparation in a serializable transaction. The preparation binds the
full proposed artifact and its hash to the expected head ID, revision, and
hash. It has `state: "pending"` and `checkpoint: null`; it does not change the
canonical head, revision history, committed-write receipt, audit, or outbox.
The transaction rechecks current membership and role revisions before commit.
A moved head returns `409`; submit a new request with a new idempotency key.
The private source route reads only the committed head and returns pending
admission with a null checkpoint. It does not read preparations. The schema
enforces one head per scope. If old data has duplicate heads, the migration
stops and names the affected scopes and policy IDs. It does not select, delete,
or change a policy record.

If `ACS_OPA_PATH` is missing, the service still starts. Policy validation then
returns `503` and the other service functions continue.

History returns revision, stored content hash, and creation time, newest first.
Its `pageSize` defaults to 20 and ranges from 1 through 100. The opaque cursor
binds the stable Policy ID, the first-page revision snapshot, page size, and
last returned revision. New writes do not enter an active traversal. A cursor
does not grant access; each page checks current authority again. Metadata does
not verify stored file bytes.

An exact-revision read loads only the requested member of the current head's
lineage. It returns the retained manifest and every relative file, sorted by
path, after recomputing the artifact hash. It returns the revision's original
creation time. Missing or changed source bytes return
`500 native_acs_policy_corrupt`; the owner does not return a partial artifact
or substitute the head. No head or a revision beyond the head returns 404.

History and exact reads require `api.read` and a current unbound PlatformAdmin
grant. They do not require `projects.admin`. The owner checks authority before
and after retrieval. Writes also require `projects.admin` and the
`WritePlatformRuntimeDefaults` permission. This API stores source only; it
does not activate ACS or change the v4 Policy path. The
[native ACS contract](../reference/contracts.md#native-acs-artifact-and-evaluation-contract)
defines its full error and request contract.

Project Policy reads require `api.read`, an active membership, and current
TenantAdmin authority in the selected tenant or an Owner, Contributor, or
Viewer grant on the project. A project-specific request accepts an unbound
token or a token bound to that exact project; run-bound and purpose-bound
tokens are rejected. A project list request requires an unbound token and
returns metadata only for visible projects with stored heads. PlatformAdmin
alone does not grant Project Policy access. Each list page uses one bounded
query to resolve current membership and roles while reading current heads.
Its protected cursor is bound to the caller's current read grants; a grant
change invalidates the cursor, and the caller must restart the list. The
cursor does not pin a cross-page snapshot or expose Project IDs as clear text.
The list does not include Platform Policy or an
inherited view. Project history for an authorized scope with no head returns an
empty page; current and exact-revision reads return 404.

The Environment reads the current complete artifact through the
disabled-by-default `GET /internal/policy-source/platform` route on the
existing Projects HTTPS listener. The shared handshake requests an optional
client certificate; pinned mTLS identifies the Environment peer. Projects
rechecks that certificate at request start and before sending the serialized
response, reads the source in one bounded, read-only
PostgreSQL snapshot, and returns admission as pending with no checkpoint.
The read is independent of OPA and leaves policy state unchanged. See the
[private source contract](../reference/contracts.md#private-environment-policy-source-read)
and [runtime TLS settings](../reference/runtime-configuration.md#optional-environment-policy-source).

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
Public reads and writes require `api.read` and `projects.admin`. Platform
requests must be unbound and made by a PlatformAdmin. Project requests must
match the exact project binding, have no run binding, and be made by a project
Owner or PlatformAdmin. Draft metadata and credential publication leave model
eligibility and accepted run selections unchanged.

The private Broker route
`POST /internal/byok/credential-publications` appends an exact credential
version to an existing provider Draft. It copies the current metadata,
configuration hash, and rate-card references. The Broker caller must check
current authority before publication. A short-lived token binds the actor and
request; Projects checks the native TLS handshake and exact token-to-body
match. An exact retry returns the saved receipt after later edits, and a
metadata save preserves the credential version. This route does not activate or
select a provider. Public Gateway routes still support only list, read, create,
and update for platform and project Drafts. They return a public metadata
projection, not the full binding record. `hasCredential` reports whether the
current revision has a credential version, but does not expose it. Lists use a
bounded, provider-ID-ordered query over current heads. Exact save retries
return the immutable revision saved by the original operation. See the
[credential publication contract](../reference/contracts.md#customer-provided-model-provider-draft-contract).

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
