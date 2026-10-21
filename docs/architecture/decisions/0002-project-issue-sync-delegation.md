# ADR 0002: Owner-delegated project issue synchronization

**Status:** Accepted for the #2176 source implementation; not deployed.

## Context

[ADR 0001](0001-platform-architecture.md) marks GitHub issue sync as a rebuild.
The 0.x story requires periodic work without a signed-in owner manually importing
each cycle. Existing run-bound grants cannot authorize a background project job.
An installation ID, saved actor ID, or persisted user bearer is not authority.
The [owner decision](https://github.com/sabbour/agentweaver/issues/2176#issuecomment-6305136576)
approves a separately reviewed existing-owner delegation path.

## Decision

Identity owns an explicit, revocable `issue-sync` grant for one tenant, project,
creator and currently selected GitHub App repository. Only a current project
Owner can create it while signed in, using an unconsumed owner-bound repository
selection. The creator or a current tenant administrator can revoke it.
Losing exact project Owner authority, an inactive grant, or a changed repository
or connection denies the next issuance and subsequent protected effects.
An observed loss of the creator's Owner role or an inactive project durably
deactivates the grant; later reassignment does not silently revive it.

Orchestrator calls dedicated pinned-mTLS Identity token/validation endpoints.
Identity calls the Projects owner over its own pinned-mTLS channel to re-read
current membership, exact Owner assignment and selected repository. No
forwarded header or synthesized bearer substitutes for native TLS identity.
The ordinary authenticated revocation proof is separate from worker authority.

Identity requests a repository-specific GitHub App installation token with
metadata read and issues write, because this job writes back to issues; it
does not request contents, pull-request or organization write permissions.
The provider-issued expiry is enforced, the cycle deadline is at most 120
seconds, and the worker invalidates its credential at cycle completion.
Tokens are not persisted, included in public receipts, or cached between jobs.
Existing run-token issuance and secret grants are unchanged.

Every mint has a durable append-only `requested` audit record before the
irreversible provider call, containing grant ID, owner, project and repository.
A confirmed mint adds `minted` evidence before the credential can be returned.
Failure to persist audit fails closed; a requested-only record deliberately
does not claim that an uncertain remote mint succeeded. Audit rows contain no
token, bearer, private key or OAuth secret.

## Consequences and reuse

The grant ID is a background-job authority reference, not a run ID. Public
management, private issuance, current-owner proof, mint auditing and bounded
credential lifetime are separate building blocks. #1914 schedules can reuse
this owner-delegation pattern and the existing Projects proof/mTLS transport,
but must define a separately reviewed purpose and minimum operation scope.
A schedule must not use an issue-write grant to start runs or redeem run
credentials. No schedule implementation or general-purpose grant is claimed
by this issue-sync change.

The initial implementation supports the selected GitHub App repository, not
arbitrary providers or secret-based repository authentication. Revocation is
checked before future effects; it cannot roll back an external write already
accepted by GitHub.

Identity migration `20261021000100_AddIssueSyncGrants` creates the grant and
mint-audit tables. Orchestrator migration 027 requires backlog migration 026
only. Exact grants, migrations, TLS profiles and live application grants are
code-only, unapplied and require separate operator approval.
