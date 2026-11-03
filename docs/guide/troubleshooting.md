# Troubleshoot Agentweaver

Use this guide to identify the owner of a configuration or deployment failure.
Start with read-only checks. Apply a remedy only when the observed state matches
its condition. A successful manifest render or an HTTP `202 Accepted` response
does not prove that a workload or request completed.

## Before you investigate

Use the current source, the approved Kubernetes context, and read access to the
affected namespace. Confirm the context before querying the cluster:

```powershell
kubectl config current-context
kubectl get pods -n agentweaver
```

Do not print Secret objects, bearer tokens, projected identity tokens, database
credentials, private keys, or secret values.

## Diagnose common failures

| Symptom | Read-only check | What the check establishes | Safe next step |
| --- | --- | --- | --- |
| A provider cannot be resolved | Compare the project selection with the provider catalog, enabled registration, platform component, and required capabilities. | A provider options class or source implementation does not prove that the provider is registered or selectable. | Use [Configure providers](./configure-providers.md) to identify the owning catalog. Do not change a resource identity to make an unresolved provider appear available. |
| A provider binding changes unexpectedly | Compare the accepted binding with the current resource ID, generation, options revision, schema version, and negotiated capabilities. | The difference identifies which part of the accepted binding changed; it does not prove that stored data is safe to reuse. | Stop before changing the binding. Follow the owning provider's reviewed replacement or recovery procedure. Increase a resource generation only after its resource is wiped or replaced. |
| Knowledge cannot connect to PostgreSQL | Check the sanitized Knowledge readiness result, Entra role name, projected identity, server certificate host, and runtime configuration. | A role mismatch, identity mismatch, or TLS host mismatch can prevent connection. A source test does not prove live grants. | Use the approved database inspection and [runtime settings](../reference/runtime-configuration.md). Keep the connection password-free and `SSL Mode=VerifyFull`; do not widen grants or disable TLS verification. |
| Knowledge rejects provider options | Compare the ConfigMap with [Configure Knowledge memory](./configure-knowledge.md) and the [runtime reference](../reference/runtime-configuration.md). Check required values, schema version, generation, and reserved schema names. | The reported validation error identifies an invalid or unsupported option. | Correct only the reviewed source value that the error identifies. For an existing immutable ConfigMap, use its replacement procedure; do not remove `immutable`. |
| Gateway reports `502` | Check the fixed HTTPS owner address and the owning service's readiness. | The owner request may have been unavailable, redirected, invalid, or outside a contract bound. | Correct the configured owner address only through reviewed configuration. Do not add a caller-controlled or arbitrary proxy target. |
| Gateway reports `504` | Check the owner service and its database and network dependencies. | The finite owner request timed out. The configured timeout is from 1 through 120 seconds and defaults to 15. | Resolve the owner dependency. Do not increase the timeout unless the service owner approves a configuration change. |
| Orchestrator or Events & Sessions readiness returns `503` | Read the affected Deployment's logs without printing credentials: `kubectl logs deployment/<service-name> -n agentweaver`. | A warning records the exception type and sanitized reason, at most once per minute. The response body remains empty; connection strings and secret values are not logged. | Follow the owning service's diagnosis. Do not replace its persistent store or grant extra database access as a first response. |
| Web sign-in or CORS fails | Compare the browser origin with the configured Web origin and the Broker client's registered redirect URI and scopes. | The configured origins or client registration do not match the request. | Set the exact approved HTTPS origin and use a registered client. Do not use wildcards or the loopback acceptance client in production. |
| `kubectl apply -k deploy\k8s` fails | Render the same source and inspect the specific missing or invalid input, image digest, namespace policy, TLS object, or runtime prerequisite. | A render checks manifests; it does not prove a live workload is ready. | Resolve the identified prerequisite, then follow the guarded [release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md). Do not retry an uncertain migration or deployment write. |
| A workload does not restart after a ConfigMap change | Check whether the ConfigMap is immutable, which workloads reference it, and whether the reviewed source contains the intended values. | Kubernetes does not restart a process when referenced ConfigMap data changes. An immutable ConfigMap rejects data updates and has a node cache window. | For an immutable map, use [Update an immutable ConfigMap](../reference/runtime-configuration.md#update-an-immutable-configmap). For a mutable map, apply the reviewed data and restart only the affected Deployment when approved. A `rollout restart` does not replace an immutable ConfigMap. |
| A Pod starts but a feature is unavailable | Check the release manifest, current provider catalog, route owner, and deployed workload image. | A source candidate, route declaration, or health probe does not establish that the feature is published or available in the target. | Use the feature's supported entrypoint and owner. Do not treat source presence or readiness as feature acceptance. |
| Run selection returns `409 platform_runtime_unconfigured` | Read the current platform runtime defaults revision. | The error means no platform runtime defaults revision has been written. | If the first PlatformAdmin is not configured, follow the approved bootstrap in [Deploy Agentweaver to Azure](./deploy-to-azure.md#bootstrap-the-first-platformadmin-and-configure-runtime-defaults). Apply the reviewed `deploy/k8s/runtime-configs/platform-runtime-defaults.json` and retry selection only after the script confirms the exact revision. |
| PlatformAdmin bootstrap Job fails | Read the sanitized receipt. It includes the exception type, an allowlisted configuration category when applicable, and validated PostgreSQL SQL state, constraint, and table metadata. It omits free-form exception messages. | A failed receipt does not prove that the grant transaction rolled back. | Before considering a retry, run the read-only inspection from the fetched `origin/v1` checkout with the documented migration credentials. Do not add `--execute`: `npm run db:maintenance:projects-config -- --publication <receipt.json> --inspect-first-platform-admin`. Check `platformAdminInspection` for active membership, assignments, matching audit rows, and the count of other active administrators. Stop if audit evidence is missing or the result is ambiguous. See [the bootstrap procedure](./deploy-to-azure.md#bootstrap-the-first-platformadmin-and-configure-runtime-defaults). |

## Collect a safe diagnostic

1. Record the service name, Pod name, image digest, timestamp, and structured
   error code.
2. Check the Deployment and Pod status:

   ```powershell
   kubectl get deployment,pod -n agentweaver
   kubectl describe pod <pod-name> -n agentweaver
   kubectl logs deployment/<service-name> -n agentweaver --tail=100
   ```

3. Check only the relevant Service and NetworkPolicy objects. Do not widen
   egress or disable policy as a diagnostic shortcut.
4. Compare the deployed image and ConfigMap source with the approved release
   evidence and [runtime configuration reference](../reference/runtime-configuration.md).

<span id="diagnose-an-indeterminate-p1-project-setup"></span>

## Diagnose interrupted project setup

The API acceptance harness records an invocation ID and creates a project named
`p1-<invocation-id>`. If setup or cleanup is interrupted, keep the same
invocation ID. Do not retry setup or start another journey until you have
reconciled the existing invocation.

Use the read-only [project setup reconciliation guide](./p1-reconciliation.md) with the
same approved manifest and invocation ID.

| Step | Status and problem code | What to check |
| --- | --- | --- |
| `create-project` | `403 forbidden` | The setup actor needs an unbound token with the `api.read` and `projects.admin` scopes and a current TenantAdmin assignment. Stale or revoked authority is denied. |
| `create-project` | `503 role_assignment_unavailable` | The runtime database grants for role assignments are not applied. See [Runtime authority writes](../reference/runtime-configuration.md#runtime-authority-writes). The transaction rolls back the project, configuration, Owner assignment, and audit row; do not grant other privileges as a workaround. |
| `assign-owner`, `assign-orchestrator`, `revoke-*` | `503 role_assignment_unavailable` | The runtime database grants for role assignments are not applied. See [Runtime authority writes](../reference/runtime-configuration.md#runtime-authority-writes). No authority row changed. Do not grant other privileges as a workaround. |
| `assign-*` | `404 principal_not_found` | The setup actor has no active membership in the selected tenant. |
| `revoke-*` | `409 revision_conflict` | Another request changed the assignment. Reconcile it before taking another action. |
| `revoke-owner` | `409 last_project_owner` | The project is not archived yet. Confirm the archive step result first. |
| Audit read | `401 unauthorized` | The read was not authenticated; this is not a denial of the visible Owner's access. Check the setup actor's unbound `api.read` token. |
| Audit read | `403 forbidden` | This is an explicit denial, not an empty result. A visible current Owner's denial is recorded as `owner_audit_read_denied`; Owners with no linked rows are denied. Use the setup actor's unbound `api.read` token and confirm current TenantAdmin authority or ownership of every linked project. |
| Audit read | `complete: false` | The result is truncated or incomplete. Do not use it for a conclusive verdict. |
| Audit read | `404 not found` | The endpoint did not return an audit read. Do not treat this as proof that the invocation has no linked records. |

An empty audit result is reported as `no_invocation_linked_audit_evidence`;
it does not prove that setup had no authority effects. A visible current Owner's
explicit audit-read denial is `owner_audit_read_denied`; other explicit audit
denials are `authority_audit_read_denied`. An HTTP 401 is
`authority_audit_read_unauthenticated`, not an Owner authorization denial. An
HTTP 404 from the audit endpoint is `authority_audit_read_not_found`, not proof
that the invocation has no linked records. The audit endpoint returns at most
32 records and marks a larger result incomplete. Neither an incomplete read
nor a **safe to clean up** verdict permits a mutation.

The reconciliation result is an assessment. A permission-filtered project list
can hide a matching project, so a not-found result without tenant-wide
visibility remains **needs review**. An empty audit read does not prove that
the setup Owner and Orchestrator assignments were revoked, so it cannot
establish **already clean**. Missing, denied, malformed, or truncated reads
also remain **needs review**. Reconciliation never changes a grant, writes a
fixture, performs cleanup, or starts another journey.

Migration 039 validates existing authority-audit links before it backfills
invocation IDs. A live inconsistency aborts the PostgreSQL transaction without
partial writes, so the migration-history entry and append-only trigger remain
unchanged. Resolve the inconsistency through the reviewed setup and cleanup
workflow that owns the invocation. Do not repair audit or setup-operation rows
by hand.

## Escalate unresolved issues

If the failure remains, include safe diagnostic details and the exact source
SHA. Redact access tokens, client secrets, connection strings, private keys,
and secret values. Do not change Azure permissions, delete data, or replace a
persistent store as a troubleshooting step without separate review.

## Expected result

You have either isolated the failure to its owning service and recorded a
sanitized diagnostic, or corrected an approved configuration and verified the
affected workload. A successful render or accepted asynchronous request alone
does not establish recovery.
