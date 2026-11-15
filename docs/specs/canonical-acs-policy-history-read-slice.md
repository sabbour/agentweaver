# Specification: Platform Policy history and exact revision reads

**Status:** Implemented in source. Target: v1.1.0.

This specification defines two read-only Platform Policy operations for
Projects & Config and their finite Gateway routes. It does not enable writes
or enforcement.

## Problem and goals

The current Policy API returns only the head. Authorized callers need bounded
revision metadata and the complete bytes for one retained revision. The reads
must preserve immutable history and use current Platform authority.

## Non-goals

This slice adds no project Policy routes, rollback or other writes, validation,
activation, CLI command, browser feature, decision-audit API, schema change, or
runtime consumer. It does not change policy enforcement or the v4 Policy path.
`NativeAcsPolicy:HeadWritesEnabled` remains unchanged.

## Intended behavior and requirements

The owner exposes:

| Method and owner path | Result |
| --- | --- |
| `GET /api/policies/platform/revisions` | Newest-first revision metadata for the stable Platform lineage. |
| `GET /api/policies/platform/revisions/{revision}` | The complete artifact from exactly the requested revision. |

History accepts only `pageSize` and `cursor`, each at most once. `pageSize`
defaults to 20 and ranges from 1 through 100. Invalid values return
`400 native_acs_policy_history_request_invalid`. The response contains the
fixed Platform scope, stable Policy ID, first-page snapshot revision, items
with revision/hash/creation time, and an optional next cursor. An empty scope
returns an empty page with null lineage fields.

The cursor is a versioned URL-safe selector with a 2,048-character limit. It
binds descending order, Platform scope, Policy ID, snapshot revision, page
size, and last returned revision. Each page rechecks current authority and
queries only revisions at or below the snapshot. A head advance does not
change an active traversal.

Exact reads accept a canonical positive decimal Int64 revision and no query
values. Invalid input returns `400 native_acs_policy_revision_request_invalid`.
No head or a revision beyond the head returns `404 native_acs_policy_not_found`.
The owner reads the exact retained revision and all relative file bytes,
orders files by path, recomputes the artifact hash, and returns the original
creation time. Missing committed source or a hash mismatch returns
`500 native_acs_policy_corrupt`; it never returns partial bytes or substitutes
the current head.

Both operations require `api.read`, an active membership, an unbound request,
and current PlatformAdmin authority. They do not require `projects.admin`.
The owner checks authority before and after retrieval. Both return
`Cache-Control: no-store`; neither writes state nor runs native validation.
Provider availability failures return
`503 native_acs_policy_source_unavailable`. Cancellation propagates, and
unrelated exceptions keep the existing unhandled-error behavior.

The Gateway publishes:

- `GET /api/v1/policies/platform/revisions`, with only `pageSize` and `cursor`.
- `GET /api/v1/policies/platform/revisions/{revision}`, with no query values.

It rejects undeclared query keys before dispatch. It forwards the validated
bearer, JSON Accept header, and optional tenant selector. It does not forward
`Idempotency-Key`, `If-Match`, arbitrary headers, or a request body for either
new route. The routes use the existing 5 MiB response limit and remain outside
the MCP catalog. Existing Platform routes keep their forwarding behavior.

## Contracts and boundaries

Projects & Config owns authorization, reads, integrity checks, and responses.
Gateway owns only finite routing, query-key rejection, and the specified
forwarding boundary. The stored revision hash identifies source bytes; history
metadata does not certify artifact integrity or a decision-audit chain.

Current reads, validation, writes, stored receipts, and their response shapes
remain unchanged. Source reads do not validate or activate historical
artifacts. Budget, cost, quota, and numeric-limit enforcement remain outside
ACS and unchanged.

## Failure and recovery

| Condition | Result |
| --- | --- |
| Missing or stale Platform authority, conflicting tenant selector, or bound token | Existing safe authorization denial with no Policy data. |
| Invalid history selector or cursor | `400 native_acs_policy_history_request_invalid`. |
| Invalid exact revision or query value | `400 native_acs_policy_revision_request_invalid`. |
| No head or future exact revision | Empty history or `404 native_acs_policy_not_found`. |
| Missing committed revision or corrupt bytes | `500 native_acs_policy_corrupt`; retain records for separately reviewed recovery. |
| Database availability failure | `503 native_acs_policy_source_unavailable`, without provider details. |
| Request cancellation | Propagate cancellation. |
| New head during traversal | Continue the original snapshot; omit the cursor to start a new traversal. |

Reads commit no changes. Repeat the same cursor or exact revision after a lost
response. No state repair or fallback to another revision occurs.

## Acceptance and validation

| Criterion | How to verify | State |
| --- | --- | --- |
| Strict selectors and cursor bounds | Database-free Projects & Config request tests | Implemented |
| Gateway finite routes, OpenAPI, and actual request forwarding | Controlled Gateway host tests | Implemented |
| Older artifact bytes, stable lineage, snapshot paging, and source failures | PostgreSQL API tests | Hosted CI |
| Existing writes and enforcement remain unchanged | Existing Policy tests and source review | Hosted CI |
| Documentation links, build, and diagrams | Repository documentation checks | Candidate validation |

## Rollout and compatibility

The routes are additive. No migration, runtime grant, release activation, or
deployment is required by this specification. Removing the routes leaves
retained data, existing routes, and the write gate unchanged.

## Open questions and decisions

The resource remains one complete artifact and one head per scope. The next
state-changing slice must define its domain profile and mutation gates before
project writes or checked rollback are enabled.
