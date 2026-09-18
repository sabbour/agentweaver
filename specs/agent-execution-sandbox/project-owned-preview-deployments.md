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

Artifact creation streams a checked-out committed tree to private object storage, verifies the
SHA-256 before persistence, and writes it once. The deployment controller receives only the
artifact id and a short-lived, audience-scoped read credential for that one object. It never
mounts, reads, or clones the archived source run worktree. Artifact retention is independent
of run retention and is not deleted by run archive/delete; it is deleted only after all
preview generations and their terminal audit-retention windows no longer reference it.

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
| `project_preview_generations` | `preview_id`, monotonically increasing `generation`, immutable `source_artifact_id`, `runtime_ref`, `route_ref`, `url_token_ciphertext`, `url_token_fingerprint`, `state`, `created_at`, `published_at`, `stopped_at`, `failure_code`, `failure_message`. Unique `(preview_id, generation)` and one filtered active-generation index. |
| `project_preview_operations` | `operation_id`, `preview_id`, `kind`, caller-supplied `idempotency_key`, `request_fingerprint`, `status`, `generation`, `fence`, `requested_by`, `created_at`, `completed_at`, result/failure fields. Unique `(preview_id, kind, idempotency_key)`; same key with a different request fingerprint returns `409 idempotency_key_reused`. |
| `project_preview_quota_reservations` | `id`, `project_id`, `preview_id`, `generation`, `state` (`reserved`, `consumed`, `released`), `expires_at`, `created_at`, `released_at`. Unique active `(project_id, preview_id, generation)` and indexed expiration. |

`policy_snapshot_json` captures validated project defaults and limits (lifetime, maximum
extension horizon, per-project quota, deployment profile, health/readiness budgets, and audit
retention) at generation creation. Later policy changes apply only to newly requested
generations; no controller reads mutable policy while reconciling a recorded generation.

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
must be sent only after authorization, set `Referrer-Policy: no-referrer` in the web embed,
and must not be exposed in list/status responses to callers without URL-disclosure permission.

The legacy run-preview endpoints and `start_preview` agent/MCP tool continue targeting the
source run sandbox and using `AgentPreviewGate`; they do not create `ProjectPreview` rows and
cannot promote a route. A project preview is created only by the new project-preview API/MCP
operations after artifact capture. The run timeline may record a safe `project_preview_id` and
generation link, never a raw capability URL or token.

## Lifecycle, races, and cleanup

### States

`requested -> reserving -> provisioning -> publishing -> ready` is the success path.
`ready -> stopping -> stopped` is an explicit stop; `ready -> expiring -> expired` is expiry;
and `requested|reserving|provisioning|publishing -> failed` is terminal provisioning failure.
`ready -> redeploying -> provisioning` creates generation N+1 while generation N remains
serving. `deleting` is a project-delete administrative state that always converges to
`stopped`/`expired` plus retained audit metadata. Terminal states are `stopped`, `expired`, and
`failed`; only a new start or redeploy operation creates another generation.

Every mutation requires an `Idempotency-Key`. The API persists the operation before scheduling
work and returns its stored result for exact replay. A database compare-and-swap acquires the
aggregate lease by updating `lease_owner`, `lease_expires_at`, and incrementing
`operation_fence`. Every controller write and Kubernetes projection carries that fence; a
worker may publish, extend, stop, or clean up only when its fence remains current. Lease loss
causes the worker to stop without changing state. A crashed owner is recoverable after lease
expiry by the reconciler.

| Trigger | Deterministic transition and race rule |
| --- | --- |
| Start | Validate project role, source eligibility, name, policy snapshot, and idempotency. Atomically reserve quota before `provisioning`; exhaustion returns `409 preview_quota_exhausted`, with no runtime object. The controller consumes the reservation only after it creates the isolated workload. |
| Publication | Create workload, Service, and HTTPRoute tagged with preview id/generation/fence; wait through the recorded convergence/readiness budgets. Only a Gateway-hostname success can move to `ready`, set `ready_at`/`expires_at`, and disclose the URL. Any failure deletes route, Service, and workload, releases quota, redacts diagnostics, and becomes `failed`. |
| Extend | `ready` only. Validate the recorded maximum horizon and policy snapshot; CAS updates `expires_at` under a new fence. The expiry reaper re-reads the row/fence immediately before deletion, so an extension committed before that check wins; a completed `expiring` transition returns `409 preview_not_ready` and requires redeploy. |
| Stop | Idempotent for `stopping`, `stopped`, and `expired`; it fences/cancels in-flight publication, deletes route then Service then workload, releases reservation, and records `stopped`. A concurrent start/redeploy that has not published is cancelled; a stale publisher cannot restore resources because its fence is invalid. |
| Redeploy | Capture/verify a new immutable artifact and reserve quota for N+1 before touching N. N remains `ready` until N+1 has a validated URL. On success atomically make N+1 active, revoke/delete N's route/runtime, release N's reservation, and publish the new URL exactly once. On failure clean only N+1 and keep N serving. |
| Expiry | Reaper selects `ready` rows with `expires_at <= now`, acquires a lease/fence, changes to `expiring`, and performs the same route/service/workload cleanup. It then releases quota and records `expired`; it never silently extends from browser activity. |
| Project delete | `ProjectService.TryBeginDeleteAsync` must first mark dependent previews `deleting` transactionally and enqueue their cleanup. Project workspace/run deletion proceeds only after the preview cleanup barrier has terminalized every generation or a durable retry record exists. Access is denied after deletion begins except controller/reconciler operations. |
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
| Redeploy | `POST /api/projects/{projectId}/previews/{previewId}/redeploy` | `project_preview_redeploy(project_id, preview_id, source_run_id, idempotency_key)` | Contributor | `202`; URL absent until ready and explicitly requested. |
| Policy | `PUT /api/projects/{projectId}/preview-settings` | no mutation tool in this lane | Owner | Existing settings response; add durable-preview limits to the owner-only request/response. |

Request bodies use the repository's snake_case DTO convention. `start`/`redeploy` accept only
`source_run_id`, optional display `name` on start, and require the idempotency header; they
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
| Domain/storage | Unit tests for every transition, idempotency fingerprint collision, lease takeover/fence rejection, token encryption/redaction, immutable artifact digest mismatch, audit retention, and SQLite/PostgreSQL migration upgrade from the current schema. |
| Authorization | API and MCP tests covering Viewer/Contributor/Owner, cross-project denial, deleted project, legacy non-project run, internal-service key, and run-capability claim. Assert URL absence from list/get/events/errors and `no-store` URL response. |
| Controller/race | Deterministic fake-clock and fake-Kubernetes tests for start/stop, extend-vs-expiry, start-vs-project-delete, stale publisher, crash/lease takeover, redeploy failure preserving generation N, publication rollback, orphan cleanup, and quota release exactly once. |
| Sandbox/security | Manifest and adapter tests assert no source-run mounts, AgentHost endpoints, claims, run credentials, API key, or privileged service account; assert Gateway-only ingress, bounded egress, resource limits, and URL/token redaction. |
| Compatibility | Existing `SandboxPreviewService`, `PreviewStep`, AgentHost preview-runner, and `start_preview` API/MCP suites run unchanged; tests prove no legacy route produces a `ProjectPreview`. |
| Full repository | Run `npm run validate:layer -- --area dotnet --dotnet-filter "FullyQualifiedName~ProjectPreview"` for each backend lane, broaden to the affected existing preview/project/MCP filters, and run `npm run validate:full` at the integrated stack top. Run `npm run docs:build` when the published docs lane changes. |
| Post-deploy API harness | After staging deploy and migration, run the Agentweaver API harness against the deployed API with a project-owner persona: start from a completed committed run, wait ready, acquire/open the URL, extend, redeploy, stop, and verify run archive/delete does not affect it. Run negative personas for Viewer URL denial, cross-project access, quota exhaustion, source ineligibility, and expiry. Capture the harness rollup and verify Kubernetes/object-store cleanup plus terminal audit retention. |

## Acceptance criteria

- [ ] A ready ProjectPreview stays reachable after its source run finishes, is archived, or is deleted.
- [ ] No project-preview generation reuses a source run worktree, AgentHost, sandbox claim, or credential.
- [ ] The aggregate, immutable source artifact, policy snapshot, quota reservation, operation lease/fence, and terminal audit record are durable in both supported stores.
- [ ] API, MCP, and web expose the same project-role authorization and lifecycle semantics; raw URLs are disclosed only through the Contributor capability-URL operation.
- [ ] Start, extend, stop, redeploy, expiry, project delete, and reconcile converge deterministically under retries and races without resource/quota leaks.
- [ ] Redeploy failure leaves the prior validated generation serving; publication success is required before a new URL exists.
- [ ] Legacy run previews continue unchanged and cannot cross the new security boundary.
- [ ] The staged implementation passes the complete validation matrix, including the post-deploy API harness.
