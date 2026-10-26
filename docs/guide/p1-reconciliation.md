# Reconcile an interrupted P1 run

Use read-only reconciliation before retrying setup, cleanup, or a live
journey. Keep the invocation ID from the existing evidence; do not generate a
replacement ID.

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
An HTTP 404 from the audit endpoint is reported as
`authority_audit_read_not_found`, not proof that the invocation has no linked
records. These distinct `verdictReasonCode` values preserve why reconciliation
needs review. The audit endpoint returns at most 32 records and marks a larger
result incomplete. Neither an incomplete read nor **safe to clean up** is
permission to mutate state. Resolve the evidence through the normal reviewed
process before any separate cleanup or new journey.

## Migration 039 safety

Migration 039 validates existing audit links before it backfills them. If live
data is inconsistent, PostgreSQL aborts the migration transaction; the new
column, backfill, migration-history entry, and trigger changes are rolled back
without partial writes. Resolve the inconsistency through the reviewed P1
setup/cleanup workflow that owns the invocation. Do not edit authority-audit
or setup-operation rows by hand.

`--dry-run` validates the manifest and prints the reconciliation steps without
sign-in, target requests, or evidence output. A completed assessment that
returns **needs review** or **safe to clean up** uses a nonzero exit code;
read the `verdict` in the evidence file.
