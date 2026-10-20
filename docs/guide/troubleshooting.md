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
| A provider cannot be resolved | The provider ID is not registered, is disabled, or does not satisfy the selected seam and capability contract. | Compare the deployment catalog with [Configure providers](../guide/configure-providers.md). Do not assume that a provider options class means the provider is selectable. |
| A provider binding changes unexpectedly | The resource ID, generation, options revision, schema version, or negotiated capabilities differ from the accepted binding. | Restore the approved resource identity and catalog revision. Increase a resource generation only after wiping or replacing that resource. |
| Knowledge fails during PostgreSQL connection | The role is missing, does not match the Knowledge managed identity name, the projected identity is wrong, or the TLS host does not match the server certificate. | Verify the PostgreSQL runtime role and grants through the approved bootstrap process. Keep the connection password-free and `SSL Mode=VerifyFull`. |
| Knowledge rejects provider options | A required setting is absent, the schema is reserved or malformed, the generation is below `1`, or the options schema version is unsupported. | Compare the Knowledge ConfigMap with [Configure Knowledge memory](../guide/configure-knowledge.md) and the [runtime reference](../reference/runtime-configuration.md). |
| Gateway reports `502` | An owner is unavailable, redirects, returns an invalid response, or exceeds a contract bound. | Check the configured HTTPS owner address and that owner's readiness. Keep owner URLs fixed; do not add arbitrary proxy targets. |
| Gateway reports `504` | The finite owner request timeout expired. | Check the owner service and its database/network dependencies. The configured timeout is from 1 to 120 seconds and defaults to 15. |
| Web sign-in or CORS fails | The configured web origin does not exactly match the browser origin, or the Broker client registration does not match the redirect and scopes. | Set the exact HTTPS origin and use a registered client. Do not use wildcards or the loopback acceptance client for production. |
| `kubectl apply -k deploy\k8s` fails | A required input, image digest, namespace/policy value, TLS object, or runtime prerequisite is missing or invalid. | Render first, then use the guarded release preflight. Resolve incomplete workload-required ConfigMaps and operator-owned prerequisites before retrying. |
| A workload does not restart after a ConfigMap edit | Kubernetes does not restart a process when referenced ConfigMap data changes. | Restart only the affected reviewed Deployment and check its rollout status. |
| A pod starts but a feature is unavailable | The source may contain an unpublished service candidate, an unregistered provider, or a route that is not deployed. | Check the release manifest and provider catalog. Source code, route declarations, and health probes do not prove deployed feature availability. |

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

The P1 API and MCP runners record privileged command failures under
`observations.fixtureSetup.commandFailures`. Each entry contains
`productErrorCode`, `kubectlExitCode`, `processErrorClass`, and `stderrClass`.
The runner keeps only known product error codes and fixed classifications.
It does not save standard error, standard output from a failed command,
process error messages, or tokens. An unknown product code stays
`unrecognized`; a generic product failure stays `product-unclassified`.

For `forbidden`, compare the approved issuer, subject, and tenant in
`ProjectsConfig__PrivilegedSetup__*` with the selected Broker actor. Also
check current tenant membership, TenantAdmin authority, unbound request
claims, and the `api.read`, `projects.admin`, and `projects.orchestrator` scopes.
A `kubectl-forbidden` classification instead indicates Kubernetes
authorization. For `product-unclassified`, first confirm that the Projects
Config runtime ConfigMap contains `ConnectionStrings__ProjectsConfigAuthority`
and the `ProjectsConfig__AuthorityDatabase__WorkloadIdentity__*` keys. The
command fails before it connects to the database when they're missing. Then
check migration state, workload identity, database connectivity, and
concurrency failures. For the required values, see
[Runtime configuration](../reference/runtime-configuration.md). A `timeout` or
`exec-transport` result does not establish whether the owner committed the
operation.

The runner does not retry an indeterminate command automatically. Do not
retry setup, start a replacement invocation, or run cleanup until you have
reviewed the original invocation state. Use the persisted invocation UUID
from the evidence, not a new UUID.

1. Generate the read-only query from the repository root:

   ```powershell
   node scripts\harness-shared\diagnose-p1-setup.mjs --invocation-id <invocation-uuid> > diagnosis.sql
   ```

2. Have the database operator run `diagnosis.sql` against the **attested
   Projects Config database**, using an existing approved read-only database
   session. The generator does not connect, sign in, or acquire credentials.
   The query uses a bounded, repeatable-read, read-only transaction and ends
   with `ROLLBACK`. Save its JSON readback with the original invocation
   evidence. A query error means the state is still unknown.
3. Review the immutable setup and cleanup operation rows, current projects,
   role assignments, and run selections. The query matches the invocation
   receipt and the exact `p1-<invocation-uuid>` project name, so a project
   without an operation receipt remains visible. It excludes configuration
   payloads and credential material.
4. Treat `not-observed` as absence **at that snapshot**, not proof that an
   earlier exec cannot commit later. Confirm that the original command has
   ended before considering a retry. `fixtures-observed` means the returned
   rows require reconciliation; it does not mean cleanup passed.
5. Compare issuer, subject, tenant, membership, project, assignment IDs,
   revisions, and audit IDs with the original evidence. Keep projects with
   accepted run selections as retained fixtures. A cleaned receipt must
   agree with the current archived project and revoked grants. Missing or
   mismatched rows remain indeterminate. Do not adopt, archive, revoke, or
   delete a fixture as part of diagnosis.

The readback always returns `retryAuthorized: false`. It is diagnostic
evidence, not a cleanup receipt or permission to replay a write. Pod logs
from the service process might not contain standard error from a separate
`kubectl exec` process. Missing log entries do not prove that setup did
nothing.

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
