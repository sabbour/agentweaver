# Azure foundation resources

Use this reference to review the v1 Azure foundation defined in
`infra/bicep/main.bicep`. Bicep is Azure's declarative infrastructure language;
AKS means Azure Kubernetes Service. The template defines a dedicated network
and cluster, Entra-only PostgreSQL, and Key Vault, Blob Storage, and Azure
Monitor resources. PostgreSQL, Key Vault, and Blob use private networking;
Azure Monitor uses a private query path and an explicit ingestion path.
Blob creates private `platform-artifacts` and `session-material` containers.

These definitions do not show that anyone deployed the resources, that private
DNS resolves, or that a service has live access.

For the Broker, initial schema bootstrap is separate from runtime table access.
The migration Job applies the canonical table grants as the migration owner,
and read-only bootstrap verification checks those grants after migration.
See [Broker database permissions](../reference/runtime-configuration#identity-broker-migration-history-and-grants).
These steps do not change the resource topology shown below.

The Orchestrator and Events & Sessions migrations use the separate P1 migration
identity. The Events & Sessions Job repeats its Sessions and Messaging settings
from the runtime map because the host validates them before `--migrate`.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'" alt="Structural Azure topology and evidence map. AKS has a public API endpoint with Kubernetes RBAC, managed Entra, Azure RBAC, and local accounts disabled. The dedicated VNet has separate AKS-node, delegated PostgreSQL, and private-endpoint subnets; Key Vault and Blob private endpoints and the in-VNet AMPLS private endpoint attach to the private-endpoint subnet, while AMPLS remains global. Blob has private platform-artifacts and session-material containers. The Foundation Probe Job exercises exact-version Key Vault secret redemption, a Blob object-store round trip, and Entra-authenticated PostgreSQL effects. Workspace and App Insights are in eastus2. Bicep grants the probe identity Key Vault Secrets User at the vault, Storage Blob Data Contributor at the storage account, Log Analytics Reader at the workspace, and Monitoring Metrics Publisher at App Insights; the Events & Sessions identity receives Storage Blob Data Contributor only on session-material. The separate P1 migration Jobs use their migration identity; the Events & Sessions Job mirrors its runtime Sessions and Messaging settings because the host validates them before migration. No live deployment or P0 completion is claimed." />
  </a>
  <figcaption>Structural resource and evidence dependencies. Bicep and Kustomize definitions are not a deployment record; the figure does not claim live readiness or P0 completion.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-foundation.drawio'">Open editable draw.io source</a></p>

The following sequence shows the verifier's time-ordered, read-only checks after the probe Job completes. It does not launch the Job or deploy resources. The P1 PostgreSQL migration Jobs are separate; the Events & Sessions Job validates its provider settings before migration.

<figure class="aw-diagram" tabindex="0">
  <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'">
    <img :src="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'" alt="After a Foundation Probe Job completes, the read-only verifier checks the source-bound registry manifest against the Job image, pod image, and pulled imageID before reading and validating the native receipt. It then queries Azure Monitor for fresh correlated evidence. Missing or mismatched evidence remains blocked. The P1 PostgreSQL migration Jobs are separate from this probe; Events & Sessions validates provider settings before running migrations." />
  </a>
  <figcaption>Ordered acceptance evidence checks, not a deployment sequence. Only complete source-, target-, image-, identity-, receipt-, and telemetry-bound evidence sets <code>deployedAcceptance</code>; the result does not claim broader P0 readiness.</figcaption>
</figure>
<p class="aw-diagram-links"><a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.png'">Open full-size PNG</a> · <a :href="'/agentweaver/v1/diagrams/flagship/v1-azure-acceptance.drawio'">Open editable draw.io source</a></p>

## Region placement

The intended v1 P0 placement is:

| Resource | Region | Notes |
| --- | --- | --- |
| AKS, private VNet and subnets, PostgreSQL Flexible Server, Key Vault, Blob Storage, and Private Endpoints | `eastus2euap` | PostgreSQL is Entra-only and uses `Standard_B2s`. |
| Log Analytics workspace and Application Insights | `eastus2` | Provider metadata advertises these Monitor resources in East US 2, not East US 2 EUAP. |
| Azure Monitor Private Link Scope (AMPLS) | `global` | Monitor resources can link to the global AMPLS. |
| AMPLS Private Endpoint | `eastus2euap` | It is in the EUAP VNet/subnet; the endpoint region follows its VNet. |

The root Bicep parameters bind that placement and the Entra administrator:

| Input | Requirement |
| --- | --- |
| `location` | Set to `eastus2euap` for the VNet and core resources. The Bicep default is the resource-group location; reviewed P0 parameters must select the intended region. |
| `monitorLocation` | Required; set to `eastus2` for Log Analytics and Application Insights. |
| `postgresEntraAdminObjectId` | Required directory object ID for the PostgreSQL Entra administrator. |
| `postgresEntraAdminPrincipalName` | Required directory principal name for that administrator. |
| `postgresEntraAdminPrincipalType` | Required; `User`, `Group`, or `ServicePrincipal`. `Unknown` is rejected, and a group is not required. |

Supply real administrator values only through reviewed operator parameters; documentation and checked-in examples keep placeholders.

Provider metadata advertises AKS, including API version `2026-07-02-preview`,
Key Vault, Storage, VNets, Private Endpoints, and PostgreSQL `Standard_B2s` in
`eastus2euap`. East US 2 EUAP has no Retail Prices API meters, so this page
makes no EUAP cost quote. Standard `eastus2` rates are only a planning proxy.
This region map is not deployment evidence and adds no 0.x product scope.

## Blob containers and data access

The storage module creates private `platform-artifacts` and `session-material`
containers. Both disable public access. The Foundation Probe identity keeps its
account-scoped Storage Blob Data Contributor role for acceptance checks.
The Events & Sessions identity receives Storage Blob Data Contributor on the
`session-material` container only; it has no account-scoped Blob data role.
These are source-defined scopes, not evidence of live containers or grants.

## AKS and Application Routing preview

The full-foundation Bicep source uses `Microsoft.ContainerService/managedClusters@2026-07-02-preview`. It explicitly enables Kubernetes RBAC, separately from Azure RBAC, and configures native `webAppRouting` with `nginx.defaultIngressControllerType` set to `'None'`. This source does not select an NGINX controller or create an application route.

The template configures the native managed Gateway API installation and
Application Routing implementation. Its Gateway class is `approuting-istio`.
The class name describes Azure's internal implementation, not a service-mesh
addon or an Istio API requirement.
The root Kustomize bundle supplies the exact-host Portal Web HTTPRoute, which
references the shared `https` listener but does not create the Gateway or
listener. The separate default-off routing installer uses native managed
certificates, one v1 HTTPS listener for the wildcard managed domain, the
Broker/Web/Gateway API/MCP HTTPRoutes, and verified backend HTTPS. It requires
explicit approved Gateway namespace and security-policy inputs. The product
namespace remains restricted.
All four routes attach to that listener, while each HTTPRoute retains its exact
Broker or Portal hostname and its existing paths and backend. This keeps both
hosts' routes available when HTTP/2 connection coalescing reuses a connection
across the certificate-covered origins; unknown hostnames still have no matching
route. The shared listener routes `/` to Web, `/api/v1` plus exact
`/openapi/v1.json` to Gateway, and exact `/mcp` plus the MCP protected-resource
metadata path to MCP. Gateway and MCP use operator-owned TLS Secrets with
service-DNS certificates signed by the public CA in the
`service-backend-ca` ConfigMap. These source routes do not prove live traffic.
The issuer comes from the admitted route hostname.
Public callback changes default to read-only append/no-op plans.
An append requires separate explicit registration and exact-URI confirmation.
The installer fresh-reads the selected application and preserves all other properties.
Only the optional ordinary-DNS and trusted-HTTPS health checks prove Broker readiness.
See [the operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#application-routing-source-setting).

Separate default-off installer operations create initial Broker signing
material, retained key-ring storage, and explicit public acceptance-client
configuration. Existing state remains unchanged.
The installer can align only the existing route hostname fields without
changing Gateway placement or namespace policy.
An intended route-derived issuer does not prove DNS, trusted TLS, or health.
The Probe installer derives the existing identity, federation, and Monitor
configuration without creating credentials or broader roles.

When `appRoutingDnsZoneResourceIds` is empty, the full-foundation template requests AKS's managed default domain. The `appRoutingDomain` output reports `managedDefaultRequested: true` and reads `domainName` from `aks.properties.ingressProfile.webAppRouting.defaultDomain.domainName`. The source does not construct a hostname. The preview API describes an autogenerated domain with a signed TLS certificate; offline validation does not prove preview registration, regional support, or HTTPS serving.

When the array contains custom zone IDs, the full-foundation template disables the managed default domain. It accepts one to five unique existing public or private DNS zone IDs in the exact authorized subscription. The guard rejects malformed, cross-subscription, and PrivateLink zone IDs. Public zones can be in one resource group and private zones in another; all zones of each kind must share one resource group.

For an existing P0 foundation, `aks-redeploy.bicep` reads the AKS-managed
domain and issuer through an `existing` resource reference. It does not deploy
`Microsoft.ContainerService/managedClusters`, so the generated domain name and
Kubernetes RBAC setting are not removed by a partial AKS update. This
AKS-only path does not change routing DNS configuration; use the full-foundation
template only for its documented initial-deployment scope. If custom DNS means
AKS omits `defaultDomain`, the output reports `false` and `null` rather than
failing.

AKS supplies a separate `appRoutingIdentity` with `resourceId`, `clientId`, and
`objectId`. Bicep scopes Key Vault Certificate User to the existing vault.
It grants each configured DNS zone only its matching public or private DNS
Zone Contributor role at that exact zone. The Bicep source defines these role assignments; they are not live role grants.
The routing identity is separate from the Foundation Probe identity.

The template configures the Key Vault CSI provider and secret rotation with `enableSecretRotation: 'true'` and `rotationPollInterval: '2m'`. These values are template settings; the addon is not active in Azure.

## Limited runtime identity setup

`infra/bicep/p1-runtime-identities.bicep` is separate from the P0 foundation topology.
It uses the existing resource group location and the existing AKS OIDC issuer.
The resource group and cluster locations must match before deployment.
Its fixed service list is Projects & Config, Environment, Knowledge,
Events & Sessions, and Orchestrator.
Each identity trusts only its named ServiceAccount in `agentweaver`.

The template grants the Events & Sessions identity Storage Blob Data Contributor
on the exact `session-material` container. It creates no database grant,
migration identity, certificate, storage account, server, cluster, or node pool.
It does not reuse or change Broker and Probe privileges.
The output provides observed runtime client and principal IDs for workload configuration.
Identity creation and federation do not prove database access or service readiness.
The [operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#limited-p1-runtime-identities)
describes the separate approval boundary.

For an existing resource group that already has the Events & Sessions identity
but lacks its configured container and grant, the focused
`infra/bicep/p1-session-material-access.bicep` template creates only the private
container and its container-scoped role assignment. The operator command and
approval boundary are documented in the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#session-material-container-repair).

The P1 PostgreSQL initializer is an explicit operator action, not part of an
infrastructure deployment. `scripts/azure/bootstrap-p1-postgres.mjs` checks
the fixed target, current PostgreSQL administrator, and all five live P1
principal IDs before it starts the temporary tunnel. It runs the Identity
Broker and P1 database stages through the same loopback-only tunnel and removes
only its run-owned transport resources. The command creates no Azure role
assignment and deploys no product workload. Its receipt proves operator
initialization only, not runtime identity or deployment success.
The initializer rejects incompatible existing privileges without changing
them. Before grants commit, it validates planned row-lock mutation guards and
reads back each installed trigger/function against the exact table, trigger
type, function source/owner, and runtime EXECUTE restriction. Its role stage
checks all five mappings before commit. Its grant checks preserve existing
ownership and permit only exact runtime grants. The source checks functions by
owner and effective EXECUTE privileges, not counts alone.
Its source catalog includes 38 Core migration versions (1 through 16, 18
through 33, and 35-40), with version 17 excluded and reserved version 34
skipped, 11 Events versions, and 11 Environment versions. The initializer
compares that catalog with each migrator's compiled version list. The reviewed
offline plan hash is
`df16526c756df42a3c0ba8534fea10bf5d019c6b5ca6e8e083b91963aa7e66ea`.
It contains 121 tables, 14 row-lock columns, and 37 exact function signatures.
Events migration 010 adds `session_object_inventory` with exactly
`SELECT, INSERT, UPDATE` runtime access.
Migration 011 protects the immutable `messaging_provider_bindings` row lock
with only a column-scoped `UPDATE (run_id)` grant; its trigger rejects every
update.
Orchestrator migration 037 adds immutable child-spawn authorization receipts
and consumption records. They bind a policy allow to the exact intent, actor,
current root fence, confirmed plan step, accepted selection revision, and
provider options revision. The runtime role has only `SELECT, INSERT` on both
tables. The owner-local consumer checks and consumes the same intent hash
inside the caller's transaction.
Orchestrator migration 038 adds the Source Control pull-request operation
ledger. The runtime plan grants exactly `SELECT, INSERT, UPDATE` on this table.
The migration is optional during readiness until its table and ledger entry
are applied.
Orchestrator migration 039 adds the dedicated append-only runtime evidence
ledger with exactly `SELECT, INSERT` runtime rights and no column-scoped
UPDATE. Its owner-only trigger function rejects row UPDATE/DELETE and
statement TRUNCATE; initializer readback verifies both guards. Readiness
accepts a complete pre-039 schema, but evidence reads and writes return
`database_update_pending` until the migration is applied. This source plan
does not authorize live migration, grants, or deployment.
Orchestrator migration 040 adds `p1_run_request_provenance` and
`p1_invocation_bindings`. Their runtime access is `SELECT, INSERT`, plus only
`UPDATE (provenance_id)` and `UPDATE (core_invocation_id)` respectively so
PostgreSQL permits row locks. Their triggers still reject every row update,
delete and table truncate. The reviewed grant plan and initializer verify
these guards; no setup-ledger table access is added to Projects.
Readiness accepts the complete schema before 040 for ordinary interactive
runs. P1 request provenance and binding require 040. This source change does
not apply the migration or grants to a live database.
Environment schema version 12 adds `environment.p1_creation_links` for
invocation-bound creation. Its planned runtime rights are `SELECT, INSERT,
UPDATE`, with no `DELETE` or `TRUNCATE`. An owner trigger protects the retained
binding and reservation identity and allows only one transition from `Pending`
to a finalized result. The finalized provider reference is distinct from a
reserved generation. This migration and the updated grant-plan hash are
source-only; this change does not apply them or authorize a live grant.
The Environment runtime bootstrap profile replacement store requires a separate
`SELECT, INSERT` grant on `environment.runtime_bootstrap_profile_replacements`.
Its planned access is exactly `SELECT, INSERT`; it adds no `UPDATE`, `DELETE`,
or default privileges.
Only casting authority can receive direct runtime EXECUTE.
Restricted-role behavior and actual PostgreSQL catalog behavior still require
separate database proof; source tests do not execute the initializer.
For an initialized database, `--inspect` reads roles, schemas, migration
history, and grants without writing. `--apply-pending-migrations` applies only
missing Core and Events versions up to explicit maximum versions, then the
exact plan grants. It fails closed if the source catalog extends past those
maximums.
`--apply-projects-config-grants` applies only the planned Projects & Config
runtime grants after that service's migration Job; the migration runner calls
it automatically. It checks the live history and objects against the source
first, and commits only an exact grant readback.
The separate continuation mode admits only the exact Projects-only prefix,
checks ordered EF migration histories and every service's migration inventory,
and migrates the other schemas without runtime object grants. Its pre-grant
checks allow only owner ACLs or the known default `PUBLIC EXECUTE` on planned
non-runtime functions while service schemas remain unavailable to runtime
roles. It rejects unplanned views and materialized views, and checks their
relation and column ACLs. The final grant transaction removes those public
function grants and applies only the planned casting-function grant.
These source changes do not change the P0 Azure topology.
See the [operator initialization procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#p1-postgresql-operator-initialization),
the [initializer source limits](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#p1-postgresql-initializer-source-proposal)
and the [source-test boundary](../guide/testing).

### P1 migration identity

`infra/bicep/p1-postgres-migration-identity.bicep` defines one more identity,
`agentweaver-id-p1-postgres-migration`, federated only to the
`p1-postgres-migration` ServiceAccount in `agentweaver`. It has no Azure
resource role. After a one-time ownership handover it owns the `orchestrator`
and `events_sessions` schemas, so migration Jobs run in the cluster under their
own egress policy instead of through an operator tunnel. The
[database maintenance runner](../guide/database-maintenance.md) deploys this
template, applies the ServiceAccount and egress policy, performs the handover
once, and runs the migration Jobs.

`infra/bicep/db-maintenance-ci-identity.bicep` defines the identity that the
database maintenance workflow uses, `agentweaver-id-db-maintenance`. GitHub
Actions signs in as it only from the `v1-db-maintenance` environment. It can
read the resource group, get cluster credentials, and manage the maintenance
Kubernetes objects in the `agentweaver` namespace. It cannot deploy Azure
resources or assign roles, so it uses the migration identity as it is and
never runs the handover. `npm run db:maintenance:setup`, run once by the
PostgreSQL Entra administrator, creates both identities, runs the handover, and
configures the GitHub environment.

## Network and acceptance checks

The canonical Identity PostgreSQL grant definition includes the four runtime
grant tables. The existing runtime role can read and insert audit rows and can
update grant heads through CAS. It cannot delete these records, update audit
rows, write migration history, or create schema objects. The verifier checks
all 14 exact tables and their migration-role ownership.
Tests use disposable restricted PostgreSQL principals for this source definition.
It creates no new Azure identity or live role grant and does not run the operator bootstrap.

AKS exposes a public API endpoint protected by managed Entra authentication and Azure RBAC; local accounts are disabled. The cluster keeps Azure Linux nodes, Cilium networking, OIDC/workload identity, and ACNS security with observability disabled. The source leaves `kubernetesVersion` empty. Runtime acceptance requires Kubernetes 1.29 or later.

The base NetworkPolicy denies traffic by default. The Foundation Probe overlay grants its own egress. The base does not provide telemetry exporter egress.

The base Cilium policies allow internal owner calls by exact service DNS name on
the service HTTPS port. The shared Broker-issuer policy also allows its selected
workloads to reach the `identity-broker` Gateway Pods on TCP 443. Cilium
kube-proxy replacement maps the public issuer address to those Pods inside the
cluster, so an FQDN-only rule cannot authorize that hairpin path. Events &
Sessions and Projects & Config have separate endpoint rules for the same
Gateway. Identity Broker has exact DNS and endpoint rules for its Projects,
Orchestrator, and Environment owner calls. Orchestrator and Environment also
allow Broker ingress on TCP 8443 for those requests.

The separate Identity Broker migration Job uses its own Cilium policy. Its
Kustomize overlay resolves both the DNS rule and PostgreSQL TCP 5432 rule to
`agentweaver-pg.postgres.database.azure.com`, matching the root runtime policy.
This source binding does not prove that the live policy is accepted or that the
database is reachable.

The Foundation Probe Job has a dedicated workload identity. It exercises Key
Vault, Blob, PostgreSQL, and telemetry without redeeming through the Identity
broker. A separate read-only consumer checks the completed Job and pod. It
verifies the exact registry image and validates the native receipt. After the
Job completes, the consumer queries Azure Monitor for matching stored
telemetry. Configuration observations remain separate from runtime evidence.

These definitions and diagrams are not deployment evidence. Live role grants, addon activation, DNS zones, certificates, controllers, routes, and HTTPS serving are not verified by this page. The consumer has only been validated with local fixtures; it has not run against Azure or AKS. See [Azure acceptance](../guide/azure-acceptance) for the evidence boundary and deployment approval requirements.
