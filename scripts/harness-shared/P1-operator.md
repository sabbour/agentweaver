<span id="run-the-p1-acceptance-harness"></span>

# Use the P1 launcher

Use this guide for the separate UI sign-in flow and for reconciliation or guarded cleanup of an interrupted invocation. API acceptance uses the read-only CLI case script; it does not create fixtures, request browser sign-in, or use these recovery procedures.

For API case input, apphost, dry-run, and evidence requirements, follow [Run a read-only CLI API case](../../docs/guide/api-acceptance-migration.md). Use only an explicitly approved non-production target.

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
expected Owner and Orchestrator assignments before the result can say
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
Resolve it through the reviewed setup and cleanup workflow that owns the
invocation; do not edit authority-audit or setup-operation rows by hand.

## Validate and run an API case

The API case script validates local input with `--dry-run`, then invokes normal CLI commands through the exact native apphost. It does not open a browser or create project/run fixtures. Follow [Run a read-only CLI API case](../../docs/guide/api-acceptance-migration.md) for the full procedure.

Use the separate UI surface for interactive browser sign-in:

```powershell
npm run harness:p1:signin-run -- --surface ui --input <manifest.json> --output <evidence.jsonl>
```

For UI, complete one interactive sign-in in the headed Chrome window with the approved account. Review the requested scopes and approve only the registered client request. The API case does not share this browser session or its credentials. Do not use another account or paste a token into a prompt.

The UI launcher needs no token file, token environment variable, identity module, or page module. It keeps Broker tokens in memory, does not read or copy an existing browser profile or browser token storage, and removes its temporary Chrome profile when the run finishes, fails, or is cancelled.


## Review the result

Keep the redacted JSONL output. It contains no token values. A blocked or
failed cleanup is not a successful cleanup. If the output says a mutation may
have completed without a verified receipt, stop and follow its diagnosis.
Do not retry the mutation or remove unrelated resources.

A passed UI journey does not prove complete API acceptance. Controlled tests do not prove a live journey.
