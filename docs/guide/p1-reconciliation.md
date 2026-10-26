# Reconcile an interrupted P1 run

Use read-only reconciliation before attempting recovery cleanup, retrying
setup, or starting a live journey. Keep the invocation ID from the existing
evidence; do not generate a replacement ID.

```powershell
npm run harness:p1:signin-run -- --reconcile <invocation-id> --input <manifest.json> --output <reconciliation.json>
```

Use the same manifest that identifies the approved target and setup actor. The
command validates it, signs in once with the existing registered client and
setup scopes, then reads the Gateway contract, visible project list, current
authorization context, role assignments for one visible matching project,
and audit events linked to the invocation. It writes one redacted JSON
evidence file. Tokens stay in memory and are not written or displayed.

Reconciliation issues only GET requests. It does not create or update
projects, change grants, perform cleanup, admit a run, or start another
journey. A **safe to clean up** verdict is an assessment only; it does not
authorize or perform cleanup.

## Read the verdict

- **already clean**: the caller has tenant-wide project visibility, no project
  has the exact `p1-<invocation-id>` name, and a complete audit read proves
  both setup assignments (Owner and Orchestrator) were later revoked.
- **safe to clean up**: one active matching project is visible, a complete
  assignment read shows the setup actor's Owner and Orchestrator roles, and
  the complete invocation audit read matches those assignments.
- **needs review**: any required read is missing, denied, malformed, partial,
  or truncated; a not-found result lacks tenant-wide project visibility; or
  the project, assignments, and audit evidence do not agree.

The project list is filtered by current permissions. A not-found result
without tenant-wide project visibility does not prove that a project is absent.
An empty audit result is reported as `no_invocation_linked_audit_evidence`;
it does not prove that setup had no authority effects and remains **needs
review**. A visible current Owner's explicit audit-read denial is reported as
`owner_audit_read_denied`; other explicit audit denials are reported as
`authority_audit_read_denied`. Neither is converted into an empty result.
An HTTP 401 is reported as `authority_audit_read_unauthenticated`, not as an
Owner authorization denial.
An HTTP 404 from the audit endpoint is reported as
`authority_audit_read_not_found`, not proof that the invocation has no linked
records. These distinct `verdictReasonCode` values preserve why reconciliation
needs review. The audit endpoint returns at most 32 records and marks a larger
result incomplete. Neither an incomplete read nor **safe to clean up** is
permission to mutate state. Use the guarded recovery-cleanup mode below as a
separate, explicit action; it cannot start or resume a journey.

## Run guarded recovery cleanup

Use the original manifest and the saved **safe to clean up** reconciliation
file from before cleanup. The mode validates both files locally, signs in once
with the setup scopes, then performs a fresh reconciliation in that same
signed-in session. It refuses before any write unless the invocation, setup
actor, project, both assignment IDs and revisions, and the complete audit with
no revocations exactly match the saved assessment. The project metadata and
configuration revisions must also match. The current authorization context
must show tenant-admin project-write authority.
Run it only after separate approval of the exact cleanup target; the saved
assessment alone is not that approval.

```powershell
npm run harness:p1:signin-run -- --recover-cleanup <invocation-id> --input <manifest.json> --reconcile-evidence <reconciliation.json> --output <cleanup.json>
```

The mode uses only the public Projects API. It revokes the Orchestrator
assignment at its expected revision, archives the project at its expected
revision, and then revokes the Owner assignment at its expected revision. The
order preserves the active-project last-Owner rule. Each write is sent once;
there is no retry. If a request fails or its receipt does not match, the mode
stops further writes, reports **indeterminate**, performs read-only
reconciliation, and writes a redacted receipt. Do not retry from that receipt;
review its readback first.

The output path must be new. The harness reserves it before sign-in so an
existing evidence file cannot block the cleanup receipt after writes. If the
receipt cannot be written, the redacted result is printed; do not retry an
indeterminate outcome.

After all three writes, the mode reconciles again. A visible archived project
with no active assignments and complete audit evidence for both setup grants
and later revocations is **already clean**. Partial states remain **needs
review**. The cleanup receipt keeps the preflight **safe to clean up**
assessment separate from the final readback verdict.

Add `--dry-run` to validate the manifest and saved evidence without opening a
browser, contacting the target, sending writes, or creating output.

## Migration 039 safety

Migration 039 validates existing audit links before it backfills them. If live
data is inconsistent, PostgreSQL aborts the migration transaction; the new
column, backfill, migration-history entry, and trigger changes are rolled back
without partial writes. Resolve the inconsistency through the reviewed P1
setup/cleanup workflow that owns the invocation. Do not edit authority-audit
or setup-operation rows by hand.

`--dry-run` on `--reconcile` validates the manifest and prints the
reconciliation steps without sign-in, target requests, or evidence output. A
completed assessment that returns **needs review** or **safe to clean up** uses
a nonzero exit code; read the `verdict` in the evidence file.
