# Azure P0 operator tooling

These dependency-free tools define and inspect infrastructure.
They do not deploy a service or prove workload identity.
The [#1777 specification](../../docs/specs/1777-azure-p0-infrastructure.md)
describes source receipts, target guards, and the separate #1784 runtime gate.

## Offline validation

Run these commands from the repository root:

```powershell
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
kubectl kustomize deploy\k8s\base
npm run release:validate
npm run test:release
```

No Azure account, credentials, or cluster connection is necessary.
Tests use fake transports and generated local fixtures.
The only real Azure CLI test reads local CLI version information.

## ACNS security source setting

AKS opts into Advanced Container Networking Services (ACNS) with security
enabled and observability disabled, the security-only configuration needed for
the #1784 FQDN-filtering work. Runtime Kubernetes must be version 1.29 or later.
The template leaves `kubernetesVersion` empty so AKS selects its currently
supported default rather than pinning an older version.

ACNS has per-node/hour charges with a cluster-wide effect; security-only is not
documented as free. This source setting does not apply to a cluster or prove
FQDN enforcement. Applying it requires separate explicit approval of the exact
target and cost, a current regional quote, and a guarded, reviewed update. No
Azure operation is part of source validation.

## Future approved target

CAUTION: Do not run a plan, deployment, bootstrap, or write diagnostic without
separate explicit approval for its dedicated Azure target and effects.
Allow-list strings and `--execute` are technical guards, not approval.

The resource group must exist with matching ownership tags after approved bootstrap.
The tools query its exact account, tenant, full ID, tags, and resource inventory.
Access denied, missing targets, malformed responses, and untagged managed roots block.
Every Azure command explicitly binds the selected subscription.

The inventory guard checks exact resource IDs, types, and names.
Declared child resources and module deployments do not require ownership tags.
The PostgreSQL administrator ID comes from the reviewed JSON parameters.
Historical outer deployments require matching SHA-derived names and source outputs.
Historical receipts also require a lowercase, 40-character `sourceTree`.
Missing or invalid tree outputs block plan and redeploy, including prior receipts without that field.
The tooling never deletes or repairs those receipts automatically.
Generated NICs require reciprocal links to an approved, tagged Private Endpoint.
Both the endpoint and all NIC IP configurations must use the dedicated private-endpoints subnet.
Other NICs, child names, scopes, and resource types block.

The source must be a clean full HEAD commit equal to the locally fetched
admitted `origin/v1` tip. Unreviewed descendants fail closed.
Only the tracked main template and tracked JSON parameters
under `infra/bicep/parameters` are supported.
The checked-in examples contain placeholders and cannot run.
The selected JSON parameters must bind prefix, tenant, owner, and cost center.
The input hash covers all tracked infrastructure files.
`sourceTree` is the 40-character Git tree SHA for the source commit.
`sourceHash` is the separate SHA-256 infrastructure input hash.

After separate approval, the operator command shape is:

```powershell
node scripts\azure\deploy.mjs --resource-group aw-v1-p0 --parameters infra\bicep\parameters\approved.json --subscription <id> --allowed-subscription <id> --tenant <id> --allowed-tenant <id>
```

Without `--execute`, this command produces an offline infrastructure summary.
It makes no Azure call.
`plan.mjs` uses the same arguments for a guarded live read-only what-if.
`deploy.mjs --execute` performs the guarded what-if before Incremental create.
Bicep source parameters and outputs bind the receipt to exact reviewed inputs.
No unsupported deployment tags occur.

## Acceptance

`verify-acceptance.mjs` accepts the same source/target arguments, plus
`--expected-sha`, `--deployment-name`, and `--cluster-name`.
Monitor configuration evidence additionally requires `--workspace-id`,
`--run-id` (the probe's 32-character lowercase hexadecimal nonce),
`--trace-id`, `--span-id`, and `--started-at` (a fresh ISO timestamp).
It uses the Logs data-plane API from an approved private-network path.
Custom query overrides are not supported.
The query uses `AppDependencies` and `AppRequests`, not `AppTraces`.
It matches `foundation-probe`, the trace/span IDs, and the exact `probe.source_sha`, `probe.source_tree`, and `probe.nonce` properties.
The returned timestamp must fall within the fresh observation window.

Successful deployment outputs include `foundationProbeIdentity` and `foundationResources`.
The tooling checks their exact dedicated resource IDs, endpoints, namespace, ServiceAccount, and workspace GUID.
The named identity contains distinct `clientId` and `principalObjectId` fields.
Parallel identity arrays do not select the probe principal.
The workspace GUID is not its ARM resource ID.

The report always blocks full P0 acceptance in this definition-only slice.
It distinguishes infrastructure configuration from actual workload evidence.
The admitted #1784 Job must prove pod/image provenance, token exchange,
exact KV redemption, owned PG effects, owned Blob cleanup, and SHA/nonce telemetry.
The CLI does not read secret values or treat its own credentials as pod proof.
It checks the fixed `foundation-probe` federation name, subject, issuer, and audience.
Caller configuration cannot replace that identity check.
Exact federation configuration still does not prove token exchange.
It does not consume or certify the final #1784 runtime receipt.
New deployment fields cannot enter the strict #1784 target DTO without its explicit schema update.

## Authentication prerequisites

AKS uses managed Entra authentication and Azure RBAC, with local accounts disabled.
There is no local administrator credential route.
Operator access requires separately approved cluster-user credential access and scoped Kubernetes permissions.
The private API also requires an approved network path.
These definitions grant no operator access.

The probe identity has Log Analytics Reader on the exact workspace.
It has Monitoring Metrics Publisher on the exact Application Insights resource.
These roles support Logs queries and authenticated exporter ingestion.
They do not prove token exchange, private connectivity, role propagation, or complete probe permissions.
No role assignment occurred during offline validation.

Key Vault Secrets User remains read-only.
The probe requires a separately approved fixture with an exact secret name and version.
Probe secret creation, deletion, and purge are not permitted by this contract.
Purge protection remains enabled.
The executable #1784 probe must adopt this read-only fixture before cloud acceptance.

The exported Blob diagnostic is operator-only and requires separate write approval.
It creates a local fixture under `artifacts\azure`, uses a generated owned blob,
and removes only that observed generation with its exact ETag in `finally`.
Primary and cleanup failures both remain visible.
The CLI does not enable this diagnostic.
