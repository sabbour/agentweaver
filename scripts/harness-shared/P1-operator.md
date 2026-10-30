# Run the P1 acceptance harness

Use this guide after the repaired release is deployed to an approved target.
The harness checks the supplied release evidence, signs in once, runs the
selected journey, and records a redacted result.

## Before you run

Prepare one manifest for the exact target. It must include:

- The exact release and source revision.
- The publication record and AgentHost image archive with their measurements.
- Current provider, runtime, and storage receipts.
- The current platform selection revision.
- Independently produced native and recovery evidence for checks that need it.

For a draft-stage release manifest, the harness accepts only releases pinned in
the release deployment script. It checks each release's tag, exact source
commit, and immutable publication result before it records evidence. v1.0.9 is
pinned with an annotated tag. It was deployed by the whole-release script, so it
stays pinned for this check but can't be deployed with the pre-acceptance
bootstrap. v1.0.11 is pinned the same way, except its release tag is a
lightweight tag on the published source commit.
v1.0.14 is also pinned for P1 evidence only. Its lightweight tag resolves to
source commit `4105e2f2520659e7c9c636cf34972661fa26342f`, and its
`publication.json` asset is pinned to SHA-256
`21dc5b63e3bab229e8e8df07cc083ce94d48c031c045af3f7ba438a93fbe5538`.
v1.0.15 is pinned for P1 evidence only. Its lightweight tag resolves to source
commit `d35c4851f591aca0f623559e1a03e3bed722f20f`, and its `publication.json`
asset is pinned to SHA-256
`3be9dfd2a28616f0208a052e58c72069c102eafdef0961dc69765466e1d198f0`.

Deployment and service evidence stay pinned to the release commit and source
tree. Reused images keep their original build labels. The harness verifies
that build source against the publication before measuring the AgentHost
archive and carries that checked binding into each journey. Do not replace
the release source pins with an older image build source. An older source
supplied in JSON alone does not authorize reuse.

The one-account launcher cannot produce a distinct denied identity. If the
manifest requests one, the launcher stops before sign-in. Do not use the
signed-in actor as the denied actor. A missing denied identity or evidence item
must remain blocked; it is not a pass.

The launcher does not change registered clients, redirect addresses, scopes,
tenant settings, roles, or permissions. Any trust change needs separate
approval for the exact client, redirects, scopes, resources, and consequences.

## Reconcile an interrupted run

Before starting another live journey, inspect the invocation from the existing
evidence file:

```powershell
npm run harness:p1:signin-run -- --reconcile <invocation-id> --input <manifest.json> --output <reconciliation.json>
```

Use the same target manifest that identifies the approved environment and setup
actor. The command uses one interactive sign-in with the existing registered
client and setup scopes. It reads the Gateway contract, visible project list,
the actor's current authorization context, the matched project's active role
assignments, and the authority-audit records linked to the invocation.
`listProjects` has no invocation-name query, so the harness compares the exact
`p1-<invocation-id>` name in the returned list. That list is
permission-filtered: a missing project is not proof that it is gone unless the
authority context confirms tenant-wide project visibility.

The audit read is bounded to 32 records. A denied, malformed, missing,
truncated, or otherwise incomplete read produces **needs review**. If a
project is visible, the role assignments and audit events must match the
expected P1 Owner and Orchestrator setup before the result can say
**safe to clean up**. A complete audit read and tenant-wide visibility with no
matching project can establish **already clean** only when it proves both the
setup Owner and Orchestrator assignments were later revoked. An empty audit
result is not positive cleanup evidence and remains **needs review**.

A complete empty audit read is reported as `no_invocation_linked_audit_evidence`.
An explicit denial for a visible current Owner is reported as
`owner_audit_read_denied`; other audit denials use
`authority_audit_read_denied`. These are never interpreted as empty results.
An HTTP 401 is reported as `authority_audit_read_unauthenticated`, not as an
Owner authorization denial.
An HTTP 404 from the audit endpoint is reported as
`authority_audit_read_not_found`, not proof that the invocation has no linked
records.

Reconciliation is an assessment only. It writes one redacted JSON evidence
file and issues only reads. It never writes a fixture or grant, performs
cleanup, starts or admits a journey, or approves a cleanup action. Even a
**safe to clean up** verdict is an assessment, not authorization for the
harness to clean up.

## Guarded recovery cleanup

Use the exact original manifest and saved **safe to clean up** reconciliation
file. The recovery mode signs in once, reads the invocation again, and refuses
before writing unless the fresh result has the same setup actor, project,
Owner and Orchestrator assignment IDs and revisions, and complete audit with no
revocations. The project metadata and configuration revisions must also
match. It also requires current
tenant-admin project-write authority.
Run it only after separate approval of the exact cleanup target; the saved
assessment alone is not that approval.

```powershell
npm run harness:p1:signin-run -- --recover-cleanup <invocation-id> --input <manifest.json> --reconcile-evidence <reconciliation.json> --output <cleanup.json>
```

The mode uses the public Projects API to revoke Orchestrator, archive the
project, and revoke Owner, with an expected revision on each write. This order
keeps the active project's last Owner until after archive. Each write is sent
once and is never retried. A failed request or unverified receipt stops the
remaining writes as **indeterminate**; the harness then performs read-only
reconciliation and records a redacted receipt. Review that readback before
taking any further action.

Choose a new output path. The harness reserves it before sign-in so an
existing evidence file cannot block the cleanup receipt after writes. If the
receipt cannot be written, the redacted result is printed; do not retry an
indeterminate outcome.

When all receipts are exact, a second reconciliation must report **already
clean**: the project is archived, its active assignment list is empty, and
the complete invocation audit proves both setup assignments were granted and
revoked. Partial states stay **needs review**. The cleanup receipt separates
the preflight assessment from the residual-state readback. Add `--dry-run` to
validate the local manifest and saved evidence without sign-in, network
requests, writes, or output.

Migration 039 validates existing audit links before backfilling them. A live
inconsistency aborts its PostgreSQL transaction without partial writes,
including restoring the migration-history state and append-only trigger.
Resolve it through the reviewed P1 setup/cleanup workflow that owns the
invocation; do not edit authority-audit or setup-operation rows by hand.

## Check the manifest without contacting the target

```powershell
npm run harness:p1:signin-run -- --surface api --input <manifest.json> --output <evidence.jsonl> --dry-run
```

The dry run checks the manifest shape and prints the operator steps. It does
not open a browser, contact the target, write evidence, create a project, or
create role assignments. It does not prove that the release evidence is live
or current.

## Run a journey

Choose one command:

```powershell
npm run harness:p1:signin-run -- --surface api --input <manifest.json> --output <evidence.jsonl>
npm run harness:p1:signin-run -- --surface ui --input <manifest.json> --output <evidence.jsonl>
```

The Agentweaver 1.x acceptance launcher has no MCP surface; it rejects
`--surface mcp` rather than falling back to the legacy runner. API acceptance
uses the native .NET CLI. For UI, complete one interactive sign-in in the
headed Chrome window with the approved account. Review the requested scopes
and approve only the registered client request. The API flow does not share
this browser session or its credentials. Do not use another account or paste
a token into a prompt.
Keep the shared browser window open between setup and run-bound token requests.
The launcher closes each completed authorization page, not the shared browser.

For the UI flow, the launcher starts the app's silent authorization with the
same Broker session. It does not ask you to sign in again.

The launcher needs no token file, token environment variable, identity module,
or page module. It keeps Broker tokens in memory. It does not read or copy an
existing browser profile or read browser token storage. Its temporary Chrome
profile is removed when the run finishes, fails, or is cancelled.

The runner uses the public Projects API to create an invocation-owned project
and assign the signed-in actor Owner and then Orchestrator. It runs only the
selected journey. In-process cleanup revokes Orchestrator, archives the
project, and then revokes Owner. Guarded recovery cleanup uses the same
revision-checked sequence for an existing invocation after a fresh evidence
match. The evidence records the receipts for setup and cleanup.

## Review the result

Keep the redacted JSONL output. It contains no token values. A blocked or
failed cleanup is not a successful cleanup. If the output says a mutation may
have completed without a verified receipt, stop and follow its diagnosis.
Do not retry the mutation or remove unrelated resources.

A passed surface journey is not full P1 acceptance. Missing native or recovery
evidence remains blocked. A one-account run also cannot pass a check that
requires a distinct denied actor. Controlled tests do not prove a deployed
service or a live journey.
