# Specification: Canonical ACS Policy API source slice

**Status:** Proposed.

**Target:** v1.1.0, issue [#2655](https://github.com/sabbour/agentweaver/issues/2655).

## Problem and goals

Projects & Config stores immutable ACS policy revisions, but no public API reads
or writes them. Later egress work must update the canonical Policy source. It
must not add a second writable egress policy.

This slice adds a bounded Platform Policy read, validation, and compare-and-swap
(CAS) write API. CAS means a write succeeds only when its expected revision is
still current. The API stores the complete native ACS artifact in the existing
store. It does not activate that artifact in a runtime.

Writes that change the head are off by default. They stay off until every
included policy domain has a proven consumer and a profile check. While writes
are off, the write route still returns the saved receipt for a retry of an
earlier committed request.

The resource boundary follows the owner decision: one artifact and one head per
scope; each artifact contains every policy domain. Scope and domain selection
remain server-owned. Future consumers evaluate applicable scope/domain policies
independently, with hard-deny precedence. This API only stores the source.

## Non-goals

- Project-scope Policy API. Project writes need checks that enforce the
  #2653 narrow-only rule and prevent a project editor from changing the shared
  egress baseline. This slice does not add those checks.
- List, history, exact-revision reads, preview, activation, rollback, CLI, or UI.
- v4 source migration, consumer cutover, egress implementation, or live policy
  activation.
- Budget, cost, quota, or numeric-limit policy fields.
- AgentMesh adoption, identity mapping, or identity enrollment.
- Live acceptance, package or image publication, or deployment.

The existing v4 Policy path remains unchanged. This API adds a canonical source;
it does not make that source active.

## Source-backed baseline

- PR #2721 added `NativeAcsArtifact`, the native artifact validator and
  evaluator in `packages/Agentweaver.Policy.Acs`. The validator checks the
  complete artifact and its content hash. It limits the manifest to 65,536
  bytes, the artifact to 1,048,576 bytes, files to 64 at 262,144 bytes each,
  diagnostics to 32, nesting to 64 levels, and OPA parse work to 5,000 ms.
  It requires an absolute OPA path.
- The pinned upstream `AgentControlSpecification` validator runs a second OPA
  validation pass with a 10-second total timeout
  ([source at the pinned commit](https://github.com/microsoft/agent-governance-toolkit/blob/48f04e5ad1efd4f2e5692d4b67def2e0f99a6870/policy-engine/core/src/artifact_validation.rs)).
- PR #2740 added immutable artifact revisions, a current head, actor-scoped
  idempotency, append-only audit and outbox records, and a serializable CAS
  write in `NativeAcsPolicyStore`. The store rechecks current authority and
  membership/role revisions before commit. It has no read method or HTTP route.
- `ProjectsConfigDbContext` currently keys policy heads by scope and
  `PolicyId`, so the schema permits more than one head per scope. The API must
  use one server-owned `PolicyId` and the schema must enforce one head per
  scope.
- `ProjectConfigEndpoints` maps owner routes. `GatewayRouteCatalog` maps
  public v1 routes and sets request and response limits. The Gateway does not
  infer policy scope from request data.
- The Projects & Config project does not reference `Agentweaver.Policy.Acs`
  and its image does not contain OPA. `eng/acs/README.md` records the pinned
  `AgentControlSpecification` `0.4.0-beta.0` package and OPA `0.70.0` asset
  provenance. Existing image checks cover AgentHost and Orchestrator, not
  Projects & Config.
- Orchestrator still reads the v4 YAML Policy. No current consumer reads the
  ACS policy store.
- The v0.x `/api/sandbox-policy` GET/PUT pair is only a route-shape precedent.
  It reads and updates repository settings through a different store. It does
  not provide v1 authorization, immutable revisions, CAS, native validation,
  or a policy migration path.
- `ProjectsConfigService.EnsurePlatformAdminAsync` currently requires
  `projects.admin` for both platform-default reads and writes. The binding
  #2653 matrix requires that scope for writes. Do not copy the stricter helper
  for Policy reads.

Binding decisions: the [#2653 design and owner answers](https://github.com/sabbour/agentweaver/issues/2653#issuecomment-6756973283)
and the [#2655 resource-boundary clarification](https://github.com/sabbour/agentweaver/issues/2655#issuecomment-6865210000).

## Intended behavior and requirements

### Resource and routes

For this bounded slice, expose only the Platform resource:

| Public route | Owner route | Behavior |
| --- | --- | --- |
| `GET /api/v1/policies/platform` | `GET /api/policies/platform` | Read the current artifact and revision metadata. |
| `POST /api/v1/policies/platform/validate` | `POST /api/policies/platform/validate` | Validate a supplied artifact without reading or writing policy state. |
| `PUT /api/v1/policies/platform` | `PUT /api/policies/platform` | Validate and append a complete artifact at the expected revision. |

The route selects the Platform scope. The service sets scope ID `default`. It
finds the one existing head for that scope and keeps its `PolicyId`. An older
database can have a head with a different ID; the API keeps that ID. Only an
empty scope gets the server-owned ID `canonical`. The request cannot supply a
scope, policy ID, domain, or policy kind. The artifact contains all domains. A
later egress edit must use this same artifact and head.

`GET` returns the exact manifest bytes, all relative file bytes, the content
hash, and the current revision. It returns `404` when no head exists. It checks
the reconstructed artifact hash before returning bytes. All responses use
`Cache-Control: no-store`; the Gateway response limit is 5 MiB. The Gateway
forwards the optional `X-Agentweaver-Tenant` selector and exposes the
`Cache-Control` response header.

`PUT` accepts a complete artifact, nullable `expectedRevision` (`null` only
when creating the first head), an idempotency key, a reason, and a correlation
ID in a strict JSON body. Reject unknown fields and do not accept a partial
patch. The server processes a write in this order:

1. Check the request shape and resolve the caller.
2. Check write authority.
3. Look for a saved receipt for the same caller, key, and request. If one
   exists, check current authority and return the receipt with
   `replayed: true`. This step does not validate the artifact again, so a lost
   response can be recovered while the validator is unavailable.
4. If the `NativeAcsPolicy:HeadWritesEnabled` setting is not `true`, return
   `503` `native_acs_policy_writes_unavailable`. The default is `false`.
5. Apply the per-caller limit and validate the artifact.
6. Call the existing CAS writer.

A request that stops before step 6 writes no revision, receipt, audit, or
outbox row. A successful write commits those records in the same transaction.

Add a unique database constraint on `(scope_type, scope_id)` for policy heads.
Project IDs are globally unique, so this key does not need a tenant column.
Before the migration adds the constraint, it locks the head table and checks
for scopes with more than one head. If it finds one, it stops with an error
that names each affected scope and its policy IDs. It does not show policy
content. It does not merge, select, delete, or renumber any row. The migration
runs in one transaction, so a stopped migration changes no record and is not
recorded as applied. Recovery requires a separate reviewed plan.

### Authorization

- Platform reads and validation require the current unbound PlatformAdmin
  grant and `api.read`. Use a current-role check, not the existing platform
  defaults helper, so reads do not gain a `projects.admin` requirement.
- Platform writes also require `projects.admin` and
  `WritePlatformRuntimeDefaults`.
- Resolve the caller through `ProjectAuthorizationOwner`. Do not trust role,
  scope, or policy identity values from the body.
- Preserve the store's current-authority, membership-revision,
  role-revision, idempotency, audit, and outbox checks.
- Require the public API audience and no purpose, project, or run binding.
- Accept `X-Agentweaver-Tenant` only as a selector for the caller's active
  membership. The Gateway forwards it, and Projects & Config checks it against
  the membership and any signed tenant assertion. This header does not select
  the Platform Policy scope. Reject conflicting tenant assertions and multiple
  tenant selector headers. Do not expose these routes to MCP.

### Validation and bounds

Use the shared `NativeAcsArtifactValidator` for both the validation route and
every write. Extend `NativeAcsValidationResult` with a nullable,
machine-readable dependency-failure value, separate from policy diagnostics.
Set it when OPA is missing or unusable, its process cannot start, crashes, or
times out, OPA fails without a structured Rego parse error, or native
validation throws. A timeout does not prove that the policy is invalid. Report
invalid Rego only when OPA exits with code 1 and its error output contains
only `rego_parse_error` items. Return diagnostics without source excerpts. A
valid artifact is not proof of activation or provider enforcement.

Projects & Config registers the validator only when `ACS_OPA_PATH` is an
absolute path. If the setting is missing, the service starts and its other
functions continue. The validation and write routes then return `503` with
dependency `opa`.

The artifact limit is 1,048,576 bytes. Set the owner and Gateway JSON body
limits and the Gateway response limit to 5 MiB. This allows for the
`ImmutableArray<byte>` JSON representation and bounded metadata. Return `413`
for requests over the limit. Keep the validator's 32-diagnostic and 5,000 ms
OPA parse bounds.

Limit validation and write requests to six per authenticated actor per minute.
Allow one active validation per Projects & Config process, with no waiting
queue. Return `429` when an actor reaches the rate limit and `503` when the
process is busy. Key the rate limit by the existing issuer and actor ID; do not
derive an Entra object ID. Apply the same limits to both validation and write
routes. These are source-level per-process bounds; the final acceptance phase
must check them against the deployed replica count and CPU limits.

The shared validator runs two OPA passes in sequence: its source-fact inspection
is limited to 5,000 ms, and the pinned native validator has a separate
10,000 ms total OPA deadline. The OPA work is therefore bounded by both passes,
not by a single 5,000 ms request deadline. Treat a Rego module that exceeds
these validation limits as an unavailable dependency, not as invalid input.
Test the timeout and process cleanup behavior in both passes.

The Projects & Config image must use the pinned Linux-x64 ACS package and OPA
`0.70.0` at `/app/opa`, with `ACS_OPA_PATH=/app/opa` and
`LD_LIBRARY_PATH=/app`. Verify OPA against the SHA-256 in `eng/acs/README.md`.
Do not add a runtime download or `PATH` fallback. Extend the image probe to run
the shared artifact validator against a valid artifact. The probe policy uses
only allowed input facts. The AgentHost, Orchestrator, and Projects & Config
Docker builds copy `packages/Agentweaver.Policy.Acs` for this probe. The ACR
candidate builder and the integration image workflow build Projects & Config
from its Dockerfile when that file exists, and stop if the pinned ACS package
is missing. The final image must pass this probe before any future
publication. Do not run publication or deployment in this phase.

The API must distinguish invalid policy input from an unavailable native or
OPA dependency. Return proven invalid or unsupported input as `422`
`native_acs_policy_invalid` with policy diagnostics. Return missing or unusable
assets, timeouts, and native or OPA invocation failures as a machine-readable
`503` `native_acs_dependency_unavailable` result. For the pinned native
validator, the codes `opa_timeout`, `opa_invalid_executable`,
`opa_execution_error`, `rego_staging_error`, and `validation_internal_error`
are dependency failures. Other native validator diagnostics are invalid input.
Never return `400` or `422`
for an unavailable validator. A malformed request body still returns `400`. Keep the two outcomes distinct in the shared validator result and the
API response. Validation success proves only the checks implemented by the
shared validator. It does not prove that every future policy domain is
supported by a runtime consumer.

### Errors and recovery

| Condition | Response | State change |
| --- | --- | --- |
| Malformed request body or query parameter | `400` | None |
| Invalid, unsupported, or hash-mismatched artifact | `422` with bounded diagnostics | None |
| Rego validation exceeds either OPA deadline | `503` dependency failure | None |
| Missing or unusable native/OPA asset, or `ACS_OPA_PATH` not set | `503` dependency failure | None |
| Head writes are disabled (default) | `503` `native_acs_policy_writes_unavailable` | None |
| Missing head on read | `404` | None |
| Stale expected revision or idempotency-key conflict | `409` | None |
| Missing or stale authority | `403` | None |
| Request exceeds the JSON body limit | `413` | None |
| Per-caller validation limit is reached | `429` | None |
| Validation concurrency is full | `503` with retry guidance | None |
| Native or OPA dependency is unavailable | `503` | None |

A retry with the same idempotency key and request returns the saved receipt only
after current authority passes. This happens before the write setting and
validation checks. A different request with the same key returns `409`. If two
requests with the same key race, one can get `409`; retry the identical request
to get the saved receipt. Do not return an allow token or use validation as an execution grant.

## Acceptance and validation

| Criterion | How to verify | State or evidence |
| --- | --- | --- |
| One Platform head exists for the Platform scope and a second Policy ID cannot create another head for that scope | EF model and migration checks; PostgreSQL regression test | Planned |
| An upgrade keeps one existing head with a non-`canonical` ID and stops, without changes, on duplicate heads | PostgreSQL upgrade test from the previous migration | Planned |
| Head writes are off by default, and a lost-response retry returns its receipt while writes or validation are unavailable | PostgreSQL API tests | Planned |
| Projects & Config starts without `ACS_OPA_PATH` and reports Policy validation as unavailable | API tests with no registered validator | Planned |
| Reads return complete, hash-verified bytes only to unbound PlatformAdmins with `api.read`, without requiring `projects.admin` | Focused Projects & Config API tests | Planned |
| Invalid Rego returns `422` policy diagnostics, while unavailable, crashed, ambiguous, or timed-out OPA/native dependencies return a typed `503` dependency failure | Shared validator and API tests | Planned |
| Invalid artifacts and unavailable assets do not write rows | Controlled endpoint and PostgreSQL tests | Planned |
| CAS races, idempotent retry, current-authority recheck, audit and outbox remain atomic | Existing and new PostgreSQL tests | Planned |
| Owner and Gateway body/response bounds agree | API route tests at and above each limit | Planned |
| Rate and concurrency limits reject excess work without an unbounded queue | Controlled limiter tests | Planned |
| Every changed release component has pending changeset coverage | Existing release changeset check | Planned |
| Projects & Config uses the pinned Linux-x64 package and OPA in its image | Build and run the ACS image probe in the final image | Deferred to acceptance |
| No runtime consumer, v4 migration, or policy activation changed | Source diff and focused regression tests | Planned |

Run the focused `Agentweaver.Projects.Config.Tests`, applicable ACS tests, and
the repository's required docs build, link, and diagram checks. Add changeset
coverage for each changed release component through the existing workflow; do
not edit package versions manually. Keep source tests separate from exact-image
and live acceptance evidence. No live API, policy activation, policy migration,
image publication, or deployment is part of this slice.

## Rollout and compatibility

The API adds a route over the existing immutable source. It does not alter
runtime behavior or move v4 policy data. Head writes stay off until a later
reviewed change sets `NativeAcsPolicy:HeadWritesEnabled` to `true`. The head uniqueness migration must
stop on duplicate scope heads; it must not choose a winner. No migration is run
against a live target during this work.

Update the Projects & Config and ACS asset references in the architecture,
contract, and testing documentation. No existing diagram shows this API's
request path, so do not add a diagram unless review finds a concrete reader
need. Run all required diagram checks.

## Open questions and follow-up

- Confirm per-caller and concurrency limits against the supported Projects &
  Config replica and CPU limits during exact-image acceptance.
- Add project Policy routes only with enforced narrow-only checks. In
  particular, do not let a project API edit the platform egress baseline.
- Add preview, history, rollback, v4 source migration, and consumer cutover in
  later reviewed slices. Preview needs a separate design for simulated context,
  verified identity inputs, and oracle limits. Keep ACS inactive until the
  separate acceptance phase.
