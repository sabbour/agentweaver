# Story: Dedicated Azure P0 infrastructure

**Issue:** [#1777](https://github.com/sabbour/agentweaver/issues/1777).
**Status:** Definitions and offline validation only. No deployed acceptance.

## Scope

Native Bicep defines dedicated AKS, workload identity, PostgreSQL, Key Vault,
Blob, and Azure Monitor resources. Kustomize defines a namespace, quotas,
default-deny network policies, DNS egress, and a reserved `foundation-probe`
ServiceAccount. It contains no workload.

Identity is the product host. Relay is a persistence library, and release
is a CLI. They are not four broker/auth/relay/release runtime services.
The `foundation-probe` principal belongs to the acceptance-only
[#1784 Job](https://github.com/sabbour/agentweaver/issues/1784), not the operator CLI.
That issue owns executable .NET probe code, the image receipt, and scoped Job
egress. This slice does not duplicate that runtime.

No live provisioning, secret reads, permission changes, bootstrap, deployment,
or cleanup occurred. Future cloud operations require separate explicit approval
for the dedicated target and each owned write effect.

## Dedicated target

The `aw-v1-p0` naming convention is necessary, but does not prove isolation.
Every live tooling route queries the selected account with `--subscription`.
The actual enabled account and tenant must match the selected target.
Every subsequent Azure command also includes the explicit subscription.

The resource group must already exist after separately approved bootstrap.
Its full resource ID and ownership tags must match the target.
The resource inventory must contain only resources in that exact group with
matching environment, manager, owner, and cost-center tags.
Access denied, malformed JSON, null tags, and missing groups block execution.
No failed lookup permits creation or fallback to the default subscription.

## Exact-source route

The tooling requires a clean full HEAD commit equal to the locally fetched
admitted `origin/v1` tip. An unreviewed descendant does not pass.
It accepts only tracked `infra/bicep/main.bicep` and
tracked JSON parameters beneath `infra/bicep/parameters`.
Untracked, ignored, changed, outside-repository, and 0.x inputs fail closed.
The source hash covers all tracked infrastructure inputs.
The existing dependency-free release validator checks manifest compatibility.

JSON parameters must bind the exact resource-group prefix, tenant, owner,
and cost center. Placeholder examples do not pass these guards.
Operators must fetch the current reviewed target before a future deployment.
Caller-supplied allow-list strings select the target. They do not constitute
approval or replace the actual account, ownership, and source checks.

The deployment uses supported Bicep `sourceSha`, `sourceTree`, and `sourceHash` parameters
and outputs. It never uses unsupported `az deployment group create --tags`.
A guarded what-if precedes Incremental deployment.
The tooling revalidates source inputs after what-if and before create.
The returned receipt binds the successful deployment ID, subscription,
tenant, resource group, source SHA, Git tree SHA, and input hash.
The Git tree SHA has 40 hexadecimal characters.
It is not the 64-character SHA-256 infrastructure input hash.

The `foundationProbeIdentity` output selects the principal by service name, not parallel-array order.
It contains `name`, `resourceId`, `clientId`, `principalObjectId`, `namespace`, and `serviceAccount`.
The client ID and principal object ID are distinct.
The `foundationResources` output contains exact cluster, vault, storage, container, PostgreSQL, workspace, and Application Insights resource IDs.
It also contains `vaultUri`, `blobContainerUri`, `postgresHost`, and the workspace GUID.
The producer does not invent database roles, secret fixtures, registry publication, or successful probe effects.
The tooling rejects mismatched resource IDs, endpoints, identity fields, and workspace GUIDs.

These outputs are deployment configuration, not a new #1784 target schema.
The strict #1784 target DTO rejects unknown fields.
Its owner must explicitly map or add fields before the executable probe can consume this configuration.

This receipt proves **infrastructure only**. No resource consumes a service
image, and no service rollout or immutable image proof occurs here.
Digest format checks alone cannot prove a running pod.

## Network definitions

AKS disables local accounts and uses managed Entra authentication with Azure RBAC.
The tenant comes from the exact reviewed parameters.
Operator access requires separately approved cluster-user access and scoped Kubernetes permissions.
The private API requires an approved network path.
This template defines no operator grants and provides no local administrator fallback.

AKS enables ACNS security and leaves ACNS observability disabled. This
security-only setting supports the #1784 FQDN-filtering work; it does not
establish a deployed FQDN policy or prove enforcement. Runtime Kubernetes must
be 1.29 or later. The template leaves `kubernetesVersion` empty so AKS selects
its currently supported default instead of pinning an older release.

ACNS has per-node/hour charges with a cluster-wide effect, and security-only is
not documented as free. This is a source-only setting: no cluster was changed
or ACNS feature activated, and source CI cannot claim cluster enforcement.
Applying it requires separate explicit approval for the exact target and cost,
a current regional quote, and a guarded, reviewed update.

Key Vault and Blob retain Private Endpoints and private DNS.
PostgreSQL uses its dedicated delegated subnet and private DNS.
Azure Monitor uses an AMPLS with private query and Open ingestion.
Both the workspace and Application Insights disable public query.
Both explicitly enable ingestion. Open does not override private DNS or
provide a public fallback.

The AMPLS Private Endpoint depends on both resource associations.
Its zone group includes all five documented zones:

- `privatelink.monitor.azure.com`
- `privatelink.oms.opinsights.azure.com`
- `privatelink.ods.opinsights.azure.com`
- `privatelink.agentsvc.azure-automation.net`
- `privatelink.blob.core.windows.net`.

Private query requires an approved VNet-connected AKS Job, VM, or VPN and
correct private DNS. The Logs data-plane API supports Private Link.
An ARM query does not. The default-deny base permits DNS only.
`CHANGEME` identity annotations are not deploy-ready configuration.
The #1784 overlay must supply the pod workload-identity label and explicit egress.

The `foundation-probe` principal receives two additional built-in role definitions:

- Log Analytics Reader (`73c42c96-874c-492b-b04d-ab87d138a893`) on the exact workspace
- Monitoring Metrics Publisher (`3913510d-42f4-4e42-8a64-420c390055eb`) on the exact Application Insights resource.

The inventory guard admits only these exact role scopes and ARM-generated assignment IDs.
It uses the principal object ID, not the client ID.
These definitions support private Logs queries and authenticated telemetry ingestion.
They do not prove actual role propagation, token exchange, private connectivity, or complete executable permission coverage.
No live grant occurred.

## PostgreSQL bootstrap prerequisite

The declarative Entra administrator is not a runtime database principal.
[`postgres-bootstrap.sql`](../../infra/bicep/postgres-bootstrap.sql) defines a
separate, approval-gated bootstrap procedure. It does not run from CI or deploy.

The Entra admin connects to `postgres` and invokes
`pg_catalog.pgaadauth_create_principal_with_oid(roleName, objectId, 'service', false, false)`.
The object ID is the UAMI `principalId` or service-principal object ID.
It is not the workload-identity `clientId`.
The database username is the chosen `roleName`.
The token scope is `https://ossrdbms-aad.database.windows.net/.default`.
The federation audience `api://AzureADTokenExchange` is not that token scope.

A separate approved migration principal owns the service schema.
The runtime role has no `azure_pg_admin`, CREATEROLE, CREATEDB, schema CREATE,
or migration privileges. Bootstrap grants schema USAGE.
The post-migration `postgres-runtime-grants.sql` defines only required persistence
table DML and explicitly denies access to the migration registry.
Current persistence migrations use no sequences.
Domain tables and sequence USAGE require separately reviewed grants from #1784.
No blanket table grant or default privilege admits future tables.
The procedure addresses dedicated-database PUBLIC grants.
Existing role memberships and inherited privileges require a separate audit.
NOINHERIT alone does not prohibit SET ROLE through existing membership.

The current `PostgresOutbox.MigrateAsync` creates a schema and migration tables.
It must run with the migration principal before runtime proof.
Existing outbox/inbox migrations do not create the probe's domain-effect table.
#1784 must define that table in the same approved migration boundary.
Bootstrap and migration success require real database evidence before acceptance.
This slice makes no claim about undocumented transaction restrictions.

The future #1784 Npgsql composition must use native asynchronous
`UsePasswordProvider` with `WorkloadIdentityCredential` for each new physical
connection. A pooled checkout does not request another password.
Cancellation and authentication errors must not fall back to another credential.
A fixed 55-minute cache is not a universal token lifetime.
The current locked foundations use Npgsql 10.0.3 and Azure.Identity 1.17.1.
#1784 must verify its executable composition against its own locked dependencies.
No duplicate runtime composition enters this infrastructure slice.

## Acceptance evidence

`scripts/azure/verify-acceptance.mjs` returns a structured `p0-integration`
report. Its overall status remains **blocked** until admitted #1784 runtime
evidence and an authorized deployment exist.
Configuration checks have their own scope. They cannot close integration acceptance.

Required runtime evidence includes:

- The exact source SHA, generated nonce, pod UID, and immutable image digest
- Verified token exchange with the exact issuer, subject, and audience
- Redemption of the exact Key Vault version without secret values in evidence
- Owned PostgreSQL state, outbox, inbox, and recovery effects
- Owned Blob generation, conditional create, readback, and conditional cleanup
- Fresh Azure Monitor evidence with the same source SHA and nonce.

Issuer presence and exact federation configuration are not token exchange.
The operator CLI is not workload-identity proof.
Caller digest strings or caller JSON that says `verified: true` cannot satisfy the gate.
The Monitor query has no custom-query override. It requires a fresh start time,
SHA, nonce, and matching returned row fields.
The query uses `AppDependencies` and `AppRequests` with `Name == "foundation-probe"`.
`OperationId` matches the trace ID, and `Id` matches the span ID.
The properties are `probe.source_sha`, `probe.source_tree`, and `probe.nonce`.
The nonce has 32 lowercase hexadecimal characters.
The returned timestamp must fall within the fresh observation window.
`AppTraces | take 1`, dashed UUID nonces, and historical telemetry cannot pass.

Key Vault Secrets User permits exact-version redemption, not fixture creation, deletion, or purge.
The fixture requires separate approved preparation.
The executable probe must use that read-only fixture.
This slice does not broaden Key Vault RBAC or disable purge protection.

The final runtime receipt uses camelCase and has no `kind` or `schemaVersion`.
Only the image receipt uses `kind: "foundation-probe-image"` and `schemaVersion: 1`.
A local image config digest is not a registry manifest digest.
Empty repository digests prove no registry publication.
Live acceptance requires registry provenance and exact-cluster observations after the Job exits.
The consumer must match Job/pod UIDs, immutable manifest digest, completion, exit code, nonce, source, and workload identity.
The Job requires `backoffLimit: 0` and a deadline of 360 to 420 seconds.
The in-process receipt cannot prove external cluster identity or post-exit completion.
Owned PostgreSQL effects, conditional Blob cleanup, and correlated workload telemetry also remain mandatory.
The current CLI does not implement that final consumer.

The optional operator Blob diagnostic is not runtime acceptance.
It requires separate authorization for write effects.
It uses a generated nonce, native If-None-Match create, and finally cleanup.
Cleanup reads generation metadata and deletes only the observed exact ETag.
Upload failure, timeout, changed ownership, and cleanup failure block the result.
The report retains both primary and cleanup errors.
No unscoped deletion occurs.

## Validation and release

`npm run test:azure` runs credential-free guard and full-report regressions.
`az bicep build` compiles locally. `kubectl kustomize` renders locally.
Neither command contacts a deployed target.
The tooling has no new npm dependency.

No versioned release-manifest component changes in this slice.
The fresh changeset has empty frontmatter because these are infrastructure
definitions and tooling, not a new service or library release.
No manual version bump or publishing pipeline occurs.

## Vendor references

- [Azure Monitor Private Link configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-configure)
- [Private Link design](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-design)
- [Private Link security](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-security)
- [OpenTelemetry exporter configuration](https://learn.microsoft.com/en-us/azure/azure-monitor/app/opentelemetry-configuration)
- [Azure Monitor Entra authentication](https://learn.microsoft.com/en-us/azure/azure-monitor/app/azure-ad-authentication)
- [Log Analytics access](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/manage-access)
- [PostgreSQL Entra principal management](https://learn.microsoft.com/en-us/azure/postgresql/security/security-manage-entra-users)
- [PostgreSQL managed-identity connection](https://learn.microsoft.com/en-us/azure/postgresql/security/security-connect-with-managed-identity)
- [PostgreSQL access control](https://learn.microsoft.com/en-us/azure/postgresql/security/security-access-control)
- [Npgsql security](https://www.npgsql.org/doc/security.html).

Vendor documentation supports the definitions. It is not deployed proof.
