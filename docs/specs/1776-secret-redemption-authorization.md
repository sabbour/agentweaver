# Story: Authorize exact run-bound secret redemption

**Issue:** [#1776](https://github.com/sabbour/agentweaver/issues/1776).
**Status:** implemented CANDIDATE; not admitted, released or deployed.

A trusted host composes `Agentweaver.Identity.AuthorizedSecretRedemption`
around an existing `ISecretRedemption` backend. It supplies a cryptographically
authenticated actor/project/run context separately from request data and an
authoritative source of server-owned immutable grants. Requests cannot grant
access, widen purpose/version restrictions, select an administrator bypass or
use wildcards.

Each redemption, including refresh, reads authority anew. Missing, null,
ambiguous, mismatched, revoked or expired grants deny before backend access.
Bindings compare ordinal actor/project/run/purpose and exact SecretRef ID and
version. After asynchronous acquisition, a second fresh read must agree with
the initial immutable grant ID, revision, expiry, state and bindings. Renewals
or same-binding replacements during acquisition invalidate the result rather
than silently extending access.

`SecretCredential.LimitLifetime` narrows metadata to the earlier backend/grant
expiry on the same object under a lock shared with invalidation and value
access. Identity never reads `GetValue`, copies credential values or serializes
them. Null, expired or invalidated backend results fail explicitly. Every
post-acquisition error, including authority failure, lifetime-limit failure and
cancellation after dependencies ignore the token, invalidates before rethrow.
Operational errors are not turned into authorization success or silent denial.

Tests use controlled asynchronous completions and clocks for revocation,
replacement/revision/expiry races, cancellation and operational failures.
They assert backend-not-called denials, exact versions and case, immutable
contracts, shorter/equal/longer lifetime bounds, retained backend invalidation,
concurrent monotonic narrowing and diagnostic/default JSON redaction.
Canonical locked restore, zero-warning Release build and all-foundation
coverage commands are in the [testing guide](../guide/testing.md).

The host authority must advance revisions on all changes (including
revoke/reactivate), returning coherent immutable snapshots. Identical metadata
without a new revision cannot reveal intervening changes; reads are not
transactional with the backend. After-return revocation and erasure of copied
immutable value strings are not guaranteed. Wire authentication, OAuth broker,
durable store, credential delivery service and Azure deployment are separate.
Existing v1 Secrets/KV contracts, expiry validation, test clock and cancellation
patterns are reused; no new competing backend or authentication stack is added.
