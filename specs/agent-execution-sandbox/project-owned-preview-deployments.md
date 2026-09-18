# Durable project-owned Preview deployments

**Issue:** [#1455](https://github.com/sabbour/agentweaver/issues/1455)
**Area:** Agent execution & sandbox

## User story

As a project collaborator, I want a preview to be a durable project deployment with an
independent lifecycle, so I can evaluate, share, extend, stop, and redeploy it after its
source run has completed, been archived, or been deleted.

## Context / problem

The current sandbox browser preview is correctly designed as a temporary, run-owned
capability: its Service and HTTPRoute target that run's AgentHost sandbox pod, its token is
bound to the run in HTTPRoute annotations, and `PreviewRunnerCredential` is minted for that
AgentHost then deleted on run release or orphan reaping. This makes it intentionally
unsuitable as a durable project deployment. Reusing its worktree, AgentHost, `SandboxClaim`,
turn bearer token, or preview-runner credential after the source run ends would retain
untrusted run authority and couple project availability to a deleted resource.

`ProjectPreview` is a new project-owned aggregate. It reuses only the proven Gateway
publication conventions (single-label capability host, Service/HTTPRoute, Gateway hostname
readiness validation, bounded DNS convergence, and idempotent cleanup). It does not reuse a
run-owned runtime or secret.

## Scope

### In

- immutable preview source artifacts and durable project-preview storage;
- independently scheduled, standalone preview runtimes and Gateway publication;
- project-level API, MCP, and web projections with consistent authorization;
- explicit expiry, quota, audit retention, recovery, and project-delete semantics;
- a compatibility boundary that leaves existing run previews unchanged.

### Out

- production promotion, custom domains, persistent application data, traffic splitting, or
  automatic activity-based renewal;
- retaining or reusing a source run worktree, AgentHost, sandbox claim, turn token, or any
  source-run credential;
- changing legacy `start_preview` behavior.

## Recommended contract

### Immutable source artifact

An eligible source is a completed project-backed run with a committed tree. Before creating
or redeploying a `ProjectPreview`, the API creates one immutable `PreviewSourceArtifact`:

| Field | Contract |
| --- | --- |
| `artifact_id` | New opaque immutable identifier; never derives authority from `run_id`. |
| `project_id` | Owning project; immutable and required. |
| `source_run_id` | Provenance only. It may refer to an archived or deleted run after capture. |
| `git_commit` / `tree_hash` | Exact committed source identity captured from the eligible run. |
| `archive_uri` / `sha256` / `size_bytes` | Private object-store bundle and verified digest/size. The bundle is a deterministic archive of the committed tree, excluding `.git` and runtime material. |
| `created_by` / `created_at` | Audit provenance. |
| `object_version` / `retention_until` | Immutable object-store version identifier and the artifact retention deadline captured at creation. |

Artifact creation atomically allocates one stable artifact record and opaque `artifact_id` in
`capturing` state before any bytes are captured. It then streams a checked-out committed tree to
a private, versioned object key, verifies the SHA-256 and byte count against the stored object
version, and transitions that same record to `available`. The row is write-once after
`available`: identity, provenance, digest, size, object URI/version, and retention metadata
cannot be updated or reused for different bytes. The deployment controller receives only an
`available` artifact id and a short-lived, audience-scoped read credential for that exact object
version. It never mounts, reads, or clones the archived source-run worktree.

Artifact retention is independent of run retention and is not deleted by run archive/delete.
Each generation holds an immutable reference to one stable artifact identity. The initial
generation may reference its atomically created `capturing` artifact while capture is in
progress; it is not provisionable, publishable, or deployable until that same artifact becomes
`available`. Candidate and later generations reference only `available` artifacts. The
reconciler repairs incomplete capture by deleting an unreferenced `capturing` row and its exact
object version after its capture deadline; it never treats an incomplete object as deployable. It may
purge an `available` artifact only after every referencing generation is terminal, every
referencing preview audit deadline has elapsed, and the artifact's `retention_until` has
elapsed. Purge first marks the row `purging`, deletes only its recorded object version, verifies
absence, then records `purged`; a failed delete remains retryable and keeps the row/object
inaccessible. No purge scans or deletes a prefix, mutable key, or unrecorded object.

A source run without a project, a verified committed tree, or a successfully captured artifact
is ineligible (`409 preview_source_ineligible`). The API never falls back to a mutable project
workspace or a current branch head. This preserves replayability and prevents a later checkout
from silently changing the deployed bytes.

### Aggregate, storage, and migration

`ProjectPreview` is the authoritative lifecycle record; Kubernetes annotations are a
reconcile projection, not the database of record. Add these tables to both
`MemoryDbContext` (SQLite migrations) and the PostgreSQL migration project, following the
paired existing migration pattern:

| Table | Required columns and indexes |
| --- | --- |
| `project_previews` | `id` (opaque), `project_id`, `display_name`, `state`, `generation`, `active_generation`, `current_source_artifact_id`, `created_by`, `created_at`, `ready_at`, `expires_at`, `policy_snapshot_json`, `quota_reservation_id`, `operation_id`, `operation_fence`, `lease_owner`, `lease_expires_at`, `last_error_code`, `last_error_message`, `terminal_at`, `audit_purge_at`, `deleted_at`. Unique `(project_id, display_name)` for non-deleted rows; index `(project_id, state, expires_at)`; index `(lease_expires_at)`; index `(audit_purge_at)`. |
| `preview_source_artifacts` | `artifact_id`, `project_id`, `source_run_id`, `git_commit`, `tree_hash`, `archive_uri`, `object_version`, `sha256`, `size_bytes`, `state` (`capturing`, `available`, `purging`, `purged`), `created_by`, `created_at`, `capture_deadline`, `retention_until`, `purged_at`. Immutable after `available`; unique `(project_id, source_run_id, git_commit, tree_hash, sha256)`; indexes on `state, capture_deadline` and `retention_until`. |
| `project_preview_generations` | `preview_id`, monotonically increasing `generation`, immutable `source_artifact_id`, `runtime_profile_version`, `runtime_profile_snapshot_json`, `runtime_ref`, `route_ref`, `url_token_ciphertext`, `url_token_fingerprint`, `state`, `created_at`, `published_at`, `stopped_at`, `failure_code`, `failure_message`. Unique `(preview_id, generation)`; filtered indexes permit exactly one active generation and at most one candidate generation per preview. |
| `project_preview_operations` | `operation_id`, `project_id`, required `preview_id` (allocated before the atomic create insert), `kind`, caller-supplied `idempotency_key`, `request_fingerprint`, `status`, `generation`, `fence`, `requested_by`, `created_at`, `completed_at`, result/failure fields. Unique `(project_id, kind, idempotency_key)`; a matching fingerprint replays the original operation/result and a different fingerprint returns `409 idempotency_key_reused`. |
| `project_preview_quota_reservations` | `id`, `project_id`, `preview_id`, `generation`, `state` (`reserved`, `consumed`, `released`), `expires_at`, `created_at`, `released_at`. Unique active `(project_id, preview_id, generation)` and indexed expiration. |
| `project_identity_archives` | `project_id`, immutable public identity snapshot (project display identity and deletion timestamp), `deleted_by`, `created_at`, `audit_purge_at`. This is the only project identity retained after parent deletion; it contains no workspace, membership, secret, or live authorization data. |

`policy_snapshot_json` captures validated project defaults and limits (lifetime, maximum
extension horizon, per-project quota, named runtime-profile version, health/readiness budgets,
and audit retention) at generation creation. `runtime_profile_snapshot_json` is the complete,
server-selected profile replay contract: source layout/archive extraction rules; builder and
runtime image **digests**; fixed build and launch commands; fixed listening port and health
probe; CPU/memory/ephemeral-storage limits; and bootstrap and steady-state network rules. A
profile is an allowlisted, versioned server asset, never caller input. The controller reconciles
only the stored snapshot, not a mutable profile definition. It rejects an artifact whose layout,
manifest, or supported profile constraints do not validate before scheduling with closed-set
`409 preview_artifact_unsupported` or `409 preview_runtime_profile_unsupported` codes.
Later policy/profile changes apply only to newly requested generations.

`url_token_ciphertext` is encrypted at rest using the platform secret protector. The raw token
is generated with CSPRNG at 128 bits or greater, appears only in an authorized
capability-URL response, and is never written to logs, events, Kubernetes annotations, audit
records, or error messages. The MCP capability-URL tool returns it only as its direct tool
result and must not repeat it in structured metadata or recovery guidance. `url_token_fingerprint` is a non-reversible
correlation value. The controller reconstructs the hostname only in process memory. A fresh
non-reused token is generated for every generation; a stopped or expired URL never revives.

Migrations backfill no `ProjectPreview` rows from legacy HTTPRoutes. Existing routes have
run-only provenance and lifecycle data, so importing them would create inaccurate durable
records. The migration is additive and deployed before the controller. Rollback is forward
repair: stop new preview runtimes and retain rows/audit data; do not downgrade a database that
contains the new tables.

### Standalone runtime and security boundary

Each generation gets a newly scheduled project-preview workload with an empty service account,
read-only artifact download volume, read-only root filesystem where its image supports it,
non-root user, dropped Linux capabilities, resource limits, and the narrow preview NetworkPolicy.
It has no project source-workspace mount, no AgentHost control endpoints, no API service key,
no run bearer/preview credential, no cloud/GitHub credentials, and no access to a source
`SandboxClaim`. The deployment profile is an allowlisted platform-owned build/runtime profile;
no caller supplies a shell command, image reference, environment variable, host path, service
account, or Kubernetes object name.

Ingress remains Gateway-only. The API/controller never probes a workload pod IP; it validates
the generated Gateway hostname as the current preview implementation does. Egress defaults to
DNS plus artifact download during bootstrap, then is disabled except explicit platform-required
endpoints in the selected profile. The preview URL is an unauthenticated capability URL. It
must be sent only after authorization and must not be exposed in list/status responses to callers
without URL-disclosure permission. The Gateway-level preview route injects
`Referrer-Policy: no-referrer` on **every** preview response, including application responses,
Gateway redirects, and Gateway error responses; the web embed is defense in depth only and
cannot substitute for this policy.

The legacy run-preview endpoints and `start_preview` agent/MCP tool continue targeting the
source run sandbox and using `AgentPreviewGate`; they do not create `ProjectPreview` rows and
cannot promote a route. A project preview is created only by the new project-preview API/MCP
operations after artifact capture. The run timeline may record a safe `project_preview_id` and
generation link, never a raw capability URL or token.

## Lifecycle, races, and cleanup

### States

The aggregate state always represents the active generation lifecycle. For an initial
deployment it transitions `requested -> reserving -> provisioning -> publishing -> ready`; its
first generation cannot enter `provisioning` until its referenced `capturing` artifact has become
`available`, and capture failure is terminal `failed`. During blue/green redeploy the aggregate
remains `ready` for active generation N; N+1 has a separate candidate state:
`candidate_reserving -> candidate_provisioning -> candidate_publishing -> candidate_ready` or
`candidate_failed`. Candidate states never change N's state, expiry, URL-disclosure right, or
cleanup lease, so the active URL, Extend operation, and mandatory expiry continue normally. Only
an atomic cutover changes N+1 to `active_ready` and marks N `superseded` for fenced cleanup. A
stalled candidate is reconciled under its own lease and recorded deadline: lease takeover either
continues it or changes it to `candidate_failed`, cleans its objects, and releases only its
candidate reservation; N remains ready. `active_ready -> stopping -> stopped` is an explicit
stop; `active_ready -> expiring -> expired` is expiry. `deleting` is a project-delete
administrative state that always converges to `stopped`/`expired` plus retained audit metadata.
Terminal states are `stopped`, `expired`, and `failed`; only a new start or redeploy operation
creates another generation.

Every mutation requires an `Idempotency-Key`. For **create**, one database transaction inserts
the project-scoped `(project_id, kind=create, idempotency_key)` operation, allocates the opaque
preview id, creates exactly one stable `capturing` artifact record/identity, creates its first
generation with `source_artifact_id` referencing that identity, and reserves quota **before
artifact capture**. The unique constraint serializes concurrent identical create requests: the
winner owns the allocation; a same-fingerprint loser loads and returns that exact stored
operation/preview id; a different fingerprint returns `409 idempotency_key_reused`. Artifact
capture begins only after this committed transaction, and scheduling begins only after the
referenced artifact has transitioned to `available`. The API persists every other operation before
scheduling work and returns its stored result for exact replay. A database compare-and-swap acquires the
aggregate lease by updating `lease_owner`, `lease_expires_at`, and incrementing
`operation_fence`. Every controller write and Kubernetes projection carries that fence; a
worker may publish, extend, stop, or clean up only when its fence remains current. Lease loss
causes the worker to stop without changing state. A crashed owner is recoverable after lease
expiry by the reconciler.

| Trigger | Deterministic transition and race rule |
| --- | --- |
| Start | Validate project role, `sourceRun.ProjectId == target projectId`, same-project authorization, source eligibility, name, policy snapshot, and idempotency before capture. The atomic create transaction reserves quota before artifact capture; exhaustion returns `409 preview_quota_exhausted`, with no artifact or runtime object. The controller consumes the reservation only after it creates the isolated workload. |
| Publication | Create workload, Service, and HTTPRoute tagged with preview id/generation/fence; wait through the recorded convergence/readiness budgets. Only a Gateway-hostname success can move to `ready`, set `ready_at`/`expires_at`, and disclose the URL. Any failure deletes route, Service, and workload, releases quota, redacts diagnostics, and becomes `failed`. |
| Extend | Targets the `active_ready` generation whenever the aggregate is `ready`, including while a candidate is redeploying. Validate the recorded maximum horizon and policy snapshot; CAS updates active N's `expires_at` under a new fence. The expiry reaper re-reads the active row/fence immediately before deletion, so an extension committed before that check wins; a completed `expiring` transition returns `409 preview_not_ready` and requires redeploy. |
| Stop | Idempotent for `stopping`, `stopped`, and `expired`; it fences/cancels in-flight publication, deletes route then Service then workload, releases reservation, and records `stopped`. A concurrent start/redeploy that has not published is cancelled; a stale publisher cannot restore resources because its fence is invalid. |
| Redeploy | A request with no `source_run_id` redeploys directly from the retained `available` artifact referenced by active N; it does not load the source run and continues to work after that run is deleted. A request with `source_run_id` explicitly requests replacement bytes and must validate `sourceRun.ProjectId == target projectId`, same-project authorization, eligibility, and capture a new artifact before N+1 exists. In both cases reserve quota for candidate N+1 before touching N. The aggregate and N remain `ready` and URL-disclosable while N+1 is `candidate_provisioning`/`candidate_publishing`; Extend and expiry always target N, and N's normal cleanup remains fenced. Only validated candidate publication atomically changes active generation, revokes N's URL/route, then cleans N and releases N's reservation. Candidate failure, stalled-candidate timeout, or candidate cleanup failure never changes N, its URL, expiry, or reservation. |
| Expiry | Reaper selects `ready` rows with `expires_at <= now`, acquires a lease/fence, changes to `expiring`, and performs the same route/service/workload cleanup. It then releases quota and records `expired`; it never silently extends from browser activity. |
| Project delete | `ProjectService.TryBeginDeleteAsync` transactionally writes an immutable `project_identity_archives` snapshot, revokes public preview routes/tokens at the Gateway publication boundary, marks every dependent preview/generation `deleting`, and persists cleanup obligations before deleting project workspace/runs. This revocation barrier commits before the parent deletion can commit; after it, public and project routes deny access, while only controller/reconciler cleanup identities can read retained metadata. Parent deletion may then complete with durable retry obligations, because audits/artifacts reference the immutable archive rather than a deleted parent. The reaper deletes route, Service, workload, and token material; retains sanitized lifecycle audit rows and artifact references until their recorded deadlines; then purges audit rows, archive identity, and eligible artifact object versions under the safe purge rules. |
| Reconcile | A hosted service scans expired leases, nonterminal rows, stale quota reservations, and orphaned resources carrying the project-preview labels. It trusts row+fence state over object presence, recreates missing required objects only for the current nonterminal generation, and deletes objects with no current matching row/fence. It cannot resurrect terminal previews. |

Cleanup failure is retryable and observable (`cleanup_pending` event/diagnostic) rather than
success-shaped. Quota is released only when the controller has either confirmed deletion or
recorded an orphan-cleanup obligation that the reconciler owns; a reconciler finally releases
stale reservations whose aggregate/generation is terminal or absent. Terminal audit rows retain
provenance, lifecycle transitions, sanitized diagnostics, and token fingerprint until the
recorded policy snapshot's audit deadline; they never retain the raw URL token or source-run
credentials.

## API and MCP contract

All routes derive `project_id` from the path and load the aggregate server-side. They use the
existing `ProjectAuthorization.RequireAccessAsync` roles; the shared internal-service identity
and run-capability claims are explicitly forbidden. This intentionally differs from legacy
run preview, where a run-bound agent callback has a narrow allowance.

| Operation | REST | MCP | Minimum role | URL/content disclosure |
| --- | --- | --- | --- | --- |
| List | `GET /api/projects/{projectId}/previews` | `project_previews_list(project_id)` | Viewer | Status, timestamps, provenance ids/tree hash, and sanitized diagnostics. URL omitted. |
| Get metadata | `GET /api/projects/{projectId}/previews/{previewId}` | `project_preview_get(project_id, preview_id)` | Viewer | Same as list; URL omitted. |
| Get capability URL | `POST /api/projects/{projectId}/previews/{previewId}/url` | `project_preview_get_url(project_id, preview_id)` | Contributor | `200` only when `ready`; response contains URL once and `expires_at`, with `Cache-Control: no-store`. Never returned in events/list/get. |
| Start | `POST /api/projects/{projectId}/previews` | `project_preview_start(project_id, source_run_id, name, idempotency_key)` | Contributor | `202` operation/status; URL is absent until caller explicitly gets it after ready. |
| Extend | `POST /api/projects/{projectId}/previews/{previewId}/extend` | `project_preview_extend(project_id, preview_id, minutes, idempotency_key)` | Contributor | `200` metadata, never URL. |
| Stop | `DELETE /api/projects/{projectId}/previews/{previewId}` | `project_preview_stop(project_id, preview_id, idempotency_key)` | Contributor | `202`/stored operation, never URL. |
| Redeploy | `POST /api/projects/{projectId}/previews/{previewId}/redeploy` | `project_preview_redeploy(project_id, preview_id, idempotency_key, source_run_id?)` | Contributor | Without `source_run_id`, replay retained artifact; with it, explicitly replace source bytes after same-project validation. `202`; URL absent until ready and explicitly requested. |
| Policy | `PUT /api/projects/{projectId}/preview-settings` | no mutation tool in this lane | Owner | Existing settings response; add durable-preview limits to the owner-only request/response. |

Request bodies use the repository's snake_case DTO convention. `start` accepts
`source_run_id` and optional display `name`; `redeploy` accepts an optional `source_run_id`
only to explicitly replace bytes and otherwise uses the retained artifact. Both require the
idempotency header; they
never accept a URL, artifact URI, command, port, image, credentials, policy snapshot, or
runtime specification. The create/redeploy response is `202 Accepted` with
`operation_id`, `preview_id`, `generation`, `state`, and `status_url`; an exact idempotent
replay returns the same status/result. `GET` returns `404` for an unknown preview in an
accessible project and `403` for unauthorized projects, preserving existing project endpoint
behavior. Invalid fields are `400`; unavailable/ineligible source, terminal-state action,
operation collision, or quota exhaustion are closed-set `409` errors; caller cancellation does
not cancel a persisted operation.

MCP forwards the caller bearer token to these API routes exactly as existing tools do. Tool
schemas must make `project_id`, source id, and idempotency key explicit; tool descriptions must
not print URLs or advise an agent to handle them. The API, not MCP, is the authorization and
state-transition authority. No internal-service exception, API key, agent tool closure, or
run capability can invoke these project-preview operations.

## Implementation lanes and dependencies

1. **Contract and persistence:** introduce domain records, DTOs, store interface, SQLite and
   PostgreSQL migrations/model snapshots, encrypted token storage, immutable artifact capture,
   and unit/integration tests. This lane must land before any controller or public route.
2. **Runtime controller:** add standalone workload adapter, lease/fencing repository,
   quota reservation, Gateway projection/readiness, cleanup, and reconciler. It depends on lane
   1 and must not modify legacy `SandboxPreviewService` semantics.
3. **API and MCP:** add authenticated routes, MCP forwarding/tools, OpenAPI/client DTOs, events,
   and endpoint/tool tests. It depends on lanes 1–2.
4. **Web and documentation:** add Project Previews navigation, status/audit/operation views,
   one-time URL acquisition/open behavior, settings controls, and user/reference docs. It depends
   on lane 3.
5. **Deployment and live verification:** add least-privilege RBAC, artifact-store identity,
   resource/network profiles, metrics/alerts, migrations rollout order, then deploy to staging.
   It depends on lanes 1–4 and is the release gate for this capability.

## Validation matrix

| Layer | Required evidence |
| --- | --- |
| Domain/storage | Unit tests for every transition; atomic create allocation/operation/`capturing` artifact identity/first-generation reference/quota reservation before capture; no provisioning or deployment until that referenced artifact is `available`; same-key concurrent replay and fingerprint collision; lease takeover/fence rejection; token encryption/redaction; artifact write-once identity/object-version/digest/size validation; capture reconciliation; generation references; safe eventual purge; archive retention; and SQLite/PostgreSQL migration upgrade from the current schema. |
| Authorization | API and MCP tests covering Viewer/Contributor/Owner, cross-project denial, **cross-project source-run start/redeploy denial**, deleted project, legacy non-project run, internal-service key, and run-capability claim. Assert URL absence from list/get/events/errors and `no-store` URL response. |
| Controller/race | Deterministic fake-clock and fake-Kubernetes tests for start/stop, extend-vs-expiry, start-vs-project-delete revocation barrier, stale publisher, crash/lease takeover, retained-artifact redeploy after source-run deletion, explicit source replacement, candidate failure or timeout preserving active N's availability/URL/extension/mandatory expiry, publication rollback, orphan cleanup, and quota release exactly once. |
| Runtime profile | Contract tests for each versioned profile snapshot/replay: fixed source layout, builder/runtime image digests, command, port, health probe, resource and network rules. Negative tests assert `preview_artifact_unsupported` and `preview_runtime_profile_unsupported` before workload scheduling. |
| Gateway privacy | Direct-navigation and external-resource tests cover success, redirect, and error paths and assert the Gateway returns `Referrer-Policy: no-referrer` on every preview response. |
| Sandbox/security | Manifest and adapter tests assert no source-run mounts, AgentHost endpoints, claims, run credentials, API key, or privileged service account; assert Gateway-only ingress, bounded egress, resource limits, and URL/token redaction. |
| Compatibility | Existing `SandboxPreviewService`, `PreviewStep`, AgentHost preview-runner, and `start_preview` API/MCP suites run unchanged; tests prove no legacy route produces a `ProjectPreview`. |
| Full repository | Run `npm run validate:layer -- --area dotnet --dotnet-filter "FullyQualifiedName~ProjectPreview"` for each backend lane, broaden to the affected existing preview/project/MCP filters, and run `npm run validate:full` at the integrated stack top. Run `npm run docs:build` when the published docs lane changes. |
| Post-deploy API harness | After staging deploy and migration, run the Agentweaver API harness against the deployed API with a project-owner persona: start from a completed committed run, wait ready, acquire/open the URL, extend, redeploy, stop, and verify run archive/delete does not affect it. Run negative personas for Viewer URL denial, cross-project access, quota exhaustion, source ineligibility, and expiry. Capture the harness rollup and verify Kubernetes/object-store cleanup plus terminal audit retention. |

## Acceptance criteria

- [ ] Create atomically establishes the project-scoped idempotent operation, preview id, one `capturing` artifact identity, first-generation reference to that identity, and quota reservation before artifact capture; scheduling cannot begin until that artifact becomes `available`, and exact and concurrent replays return the same operation/preview.
- [ ] A ready ProjectPreview stays reachable after its source run finishes, is archived, or is deleted; retained-artifact redeploy works after deletion, while an explicitly supplied source run is validated as a same-project replacement.
- [ ] Source artifacts are write-once, versioned, reconciled, generation-referenced, and purged only after all recorded retention and reference conditions hold.
- [ ] Active and candidate redeploy generations remain separately fenced: while a candidate runs, the aggregate remains ready and active URL availability, Extend, mandatory expiry, and cleanup safety target active N; failed or stalled candidates cannot reduce them.
- [ ] Project deletion snapshots identity, commits public-route revocation before parent deletion, and retains/purges audits and artifacts safely without orphan access.
- [ ] Every Gateway preview response, including redirects and errors, has `Referrer-Policy: no-referrer`.
- [ ] No project-preview generation reuses a source run worktree, AgentHost, sandbox claim, or credential.
- [ ] The aggregate, immutable source artifact, policy snapshot, quota reservation, operation lease/fence, and terminal audit record are durable in both supported stores.
- [ ] API, MCP, and web expose the same project-role authorization and lifecycle semantics; raw URLs are disclosed only through the Contributor capability-URL operation.
- [ ] Start, extend, stop, redeploy, expiry, project delete, and reconcile converge deterministically under retries and races without resource/quota leaks.
- [ ] Redeploy failure leaves the prior validated generation serving; publication success is required before a new URL exists.
- [ ] Legacy run previews continue unchanged and cannot cross the new security boundary.
- [ ] The staged implementation passes the complete validation matrix, including the post-deploy API harness.
