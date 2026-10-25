# Troubleshoot Agentweaver 1.x

Use this article to diagnose configuration and deployment failures. Start
with the owning service and preserve its error code. Do not treat a successful
manifest render or an HTTP `202 Accepted` response as proof that a workload
completed.

## Before you begin

You need the v1 source, the approved Kubernetes context, and permission to read
the affected namespace. Use only the context for the intended cluster:

```powershell
kubectl config current-context
kubectl get pods -n agentweaver
```

Do not print Secret objects, bearer tokens, projected identity tokens, or
database credentials while collecting evidence.

## Resolve common failures

| Symptom | Cause to check | Action |
| --- | --- | --- |
| A provider cannot be resolved | The provider ID is not registered, is disabled, or does not satisfy the platform component and capability contract. | Compare the deployment catalog with [Configure providers](../guide/configure-providers.md). Do not assume that a provider options class means the provider is selectable. |
| A provider binding changes unexpectedly | The resource ID, generation, options revision, schema version, or negotiated capabilities differ from the accepted binding. | Restore the approved resource identity and catalog revision. Increase a resource generation only after wiping or replacing that resource. |
| Knowledge fails during PostgreSQL connection | The role is missing, does not match the Knowledge managed identity name, the projected identity is wrong, or the TLS host does not match the server certificate. | Verify the PostgreSQL runtime role and grants through the approved bootstrap process. Keep the connection password-free and `SSL Mode=VerifyFull`. |
| Knowledge rejects provider options | A required setting is absent, the schema is reserved or malformed, the generation is below `1`, or the options schema version is unsupported. | Compare the Knowledge ConfigMap with [Configure Knowledge memory](../guide/configure-knowledge.md) and the [runtime reference](../reference/runtime-configuration.md). |
| Gateway reports `502` | An owner is unavailable, redirects, returns an invalid response, or exceeds a contract bound. | Check the configured HTTPS owner address and that owner's readiness. Keep owner URLs fixed; do not add arbitrary proxy targets. |
| Gateway reports `504` | The finite owner request timeout expired. | Check the owner service and its database/network dependencies. The configured timeout is from 1 to 120 seconds and defaults to 15. |
| Orchestrator or Events and Sessions readiness returns `503` | The service could not verify its PostgreSQL schema or another readiness prerequisite. | Read the affected pod's logs with `kubectl logs deployment/<service-name> -n agentweaver`. A warning records the exception type and sanitized reason, at most once per minute. The response body remains empty; connection strings and secret values are not logged. |
| Web sign-in or CORS fails | The configured web origin does not exactly match the browser origin, or the Broker client registration does not match the redirect and scopes. | Set the exact HTTPS origin and use a registered client. Do not use wildcards or the loopback acceptance client for production. |
| `kubectl apply -k deploy\k8s` fails | A required input, image digest, namespace/policy value, TLS object, or runtime prerequisite is missing or invalid. | Render first, then use the guarded release preflight. Resolve incomplete workload-required ConfigMaps and operator-owned prerequisites before retrying. |
| A workload does not restart after a ConfigMap edit | Kubernetes does not restart a process when referenced ConfigMap data changes. | Restart only the affected reviewed Deployment and check its rollout status. |
| A pod starts but a feature is unavailable | The source may contain an unpublished service candidate, an unregistered provider, or a route that is not deployed. | Check the release manifest and provider catalog. Source code, route declarations, and health probes do not prove deployed feature availability. |
| Run selection returns `409 platform_runtime_unconfigured` | No platform runtime defaults revision has been written yet. | Bootstrap the first PlatformAdmin, then apply the reviewed `deploy/k8s/runtime-configs/platform-runtime-defaults.json` with the [deployment guide](./deploy-to-azure.md). Retry selection after the script confirms the exact revision. |
| PlatformAdmin bootstrap Job fails | The receipt reports the exception type, an allowlisted configuration category when applicable, and validated PostgreSQL SQL state, constraint, and table metadata; it omits free-form exception messages. A failed receipt does not establish that the grant transaction rolled back. | Read `bootstrap.diagnostic`, then have a database operator verify the exact active PlatformAdmin assignment and matching `role_assigned` audit row for the approved identity before any retry. This repository has no dedicated read-only API or script for that exact readback. |

## Collect a safe diagnostic

1. Record the service name, pod name, image digest, timestamp, and structured
   error code.
2. Check the Deployment and pod status:

   ```powershell
   kubectl get deployment,pod -n agentweaver
   kubectl describe pod <pod-name> -n agentweaver
   kubectl logs deployment/<service-name> -n agentweaver --tail=100
   ```

3. Check only the relevant Service and NetworkPolicy objects. Do not widen
   egress or disable policy as a diagnostic shortcut.
4. Compare the deployed image and ConfigMap source with the reviewed release
   receipt and [runtime configuration reference](../reference/runtime-configuration.md).

## Diagnose an indeterminate P1 project setup

The P1 acceptance harness runs API and MCP checks against a deployment.
Its runners set up and clean up their fixture project through the public
Gateway API with the signed-in setup token:

1. `createProject` creates a project named `p1-<invocation-uuid>`.
2. `assignProjectRole` assigns the setup actor the Owner role, then the
   Orchestrator role.
3. Cleanup revokes the Orchestrator assignment, archives the project with
   `updateProject`, and then revokes the Owner assignment.

The runner records each failed mutating request under
`observations.fixtureSetup.requestFailures`. Each entry contains `step`,
`operationId`, `status`, and `problemCode`. A `null` status means the response
wasn't received, such as a timeout or transport failure. The runner doesn't
save response bodies, error messages, or tokens. When a failure leaves the
state unknown, `diagnosis.projectName` contains the fixture project name.

Common results:

| Step | Status and problem code | What to check |
| --- | --- | --- |
| `create-project` | `403 forbidden` | The setup actor needs an unbound token with the `api.read` and `projects.admin` scopes and a current TenantAdmin assignment. |
| `assign-owner`, `assign-orchestrator`, `revoke-*` | `503 role_assignment_unavailable` | The runtime database grants for role assignments aren't applied. See [Runtime authority writes](../reference/runtime-configuration.md#runtime-authority-writes). No authority row changed. Don't grant other privileges as a workaround. |
| `assign-*` | `404 principal_not_found` | The setup actor has no active membership in the selected tenant. |
| `revoke-*` | `409 revision_conflict` | Another request changed the assignment. Reconcile it before you do anything else. |
| `revoke-owner` | `409 last_project_owner` | The project isn't archived yet. Confirm the archive step result first. |

Any failure after the runner sends a mutating request makes setup or cleanup
indeterminate. The runner doesn't retry the request or run cleanup. Don't
retry setup, start a replacement invocation, or run cleanup until you have
reviewed the original invocation state. Use the persisted invocation UUID
from the evidence, not a new UUID.

1. As a tenant admin, call `listProjects` and find the project whose name
   matches `diagnosis.projectName`. If no project has that name, the create
   request didn't commit at that snapshot.
2. Call `listProjectRoleAssignments` for that project. Compare the active
   assignments, their IDs, and revisions with `setup` in the evidence.
3. Call `getAuthorizationContext` with the setup actor's token to confirm its
   current tenant membership and roles.
4. Treat a missing project or assignment as absence **at that snapshot**, not
   proof that an earlier request can't commit later. Keep projects with
   accepted run selections as retained fixtures. Don't adopt, archive, or
   revoke a fixture as part of diagnosis. Make any repair as a separate,
   reviewed change.

Each assignment and revocation also writes an `authority_audit` row in the
same transaction. A database operator can review those rows with an existing
approved read-only session.

Evidence from runs before this change can still contain
`commandFailures` from the privileged `kubectl exec` path. For those runs,
generate the read-only query with
`node scripts\harness-shared\diagnose-p1-setup.mjs --invocation-id <invocation-uuid>`
and have a database operator run it against the attested Projects Config
database. The readback always returns `retryAuthorized: false`; it isn't
permission to replay a write.

## Escalate unresolved issues

If the failure remains, include the safe diagnostic details and the exact
source SHA. Redact access tokens, client secrets, connection strings, private
keys, and secret values. Do not change Azure permissions, delete data, or
replace a persistent store as a troubleshooting step without separate review.

## Expected result

You have either isolated the failure to its owning service and recorded a
sanitized diagnostic, or corrected the approved configuration and verified
the affected workload. Do not report a resolution based only on a successful
manifest render or an accepted asynchronous request.
