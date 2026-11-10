# Dedicated Azure infrastructure definitions

These files define infrastructure; their presence does not prove that resources,
permissions, or workloads are deployed.

`main.bicep` composes a dedicated VNet, AKS with a public API endpoint and OIDC/workload identity,
Entra-only PostgreSQL, Key Vault, Blob, and Azure Monitor.
Key Vault and Blob have Private Endpoints and private DNS.
Blob creates private `platform-artifacts` and `session-material` containers.
PostgreSQL has a delegated subnet and private DNS.
The full-foundation template explicitly enables Kubernetes RBAC separately from
Azure RBAC and requests AKS Application Routing's managed default domain when
no custom DNS zones are configured.
`aks-redeploy.bicep` is a separate, narrow refresh for an existing foundation.
It reads the existing AKS issuer, identity, and Application Routing domain. It
updates only the three existing workload-identity federated credentials and
creates or updates the `kata` node pool. It does not PUT the AKS resource or
change its routing, RBAC, network, data-service, private-DNS, or monitoring
configuration.
If AKS omits `defaultDomain` for custom DNS, its diagnostic output uses
`false` and `null`.

The foundation parameter example keeps the VNet, AKS, PostgreSQL, Key Vault,
Blob, and their private endpoints in `eastus2euap`. Its required
`monitorLocation` places
Log Analytics and Application Insights in `eastus2`; the AMPLS remains global,
and the Monitor Private Endpoint stays in the VNet's `location`. PostgreSQL
continues to use the delegated subnet in the primary `location`; no cross-region
database subnet is introduced. In this layout, the AKS Azure Monitor add-on is
not available; see [Canary-region clusters](#canary-region-clusters).
The reviewed `monitorLocation` can also match `location` when the required
Azure resources are available in that region.

AKS is not a private cluster: `apiServerAccessProfile.enablePrivateCluster` is
fixed to `false`. Managed Entra authentication and Azure RBAC are enabled and
local accounts remain disabled. This is distinct from enabling a public FQDN
on a private API endpoint.

The AKS control-plane identity receives Network Contributor on the dedicated AKS subnet.
The role assignment uses its system-assigned principal ID and depends on cluster creation.
It does not grant permissions at VNet, resource-group, or subscription scope.
The compiled-template test checks the principal, role, scope, and dependency.
These definitions do not apply a live role assignment.

The AMPLS uses private query and Open ingestion.
Both the workspace and Application Insights explicitly disable public query.
Both enable ingestion. Open does not override private DNS or provide fallback.
The Private Endpoint depends on both resource associations.
Its zone group uses all five documented Monitor DNS zones.
The Blob zone is shared with the dedicated storage Private Endpoint.

Private Logs queries require an approved VNet-connected Job, VM, or VPN and
correct private DNS. An ARM query cannot use Private Link.
The default-deny Kubernetes base does not provide exporter egress.
The foundation probe Job overlay owns explicit egress and executable runtime proof.

## Kata node pool

AKS has two node pools:

| Pool | Mode | VM size | Autoscale | Purpose |
| --- | --- | --- | --- | --- |
| `system` | System | `Standard_D2s_v5` | 2-3 nodes | Product services and AKS add-ons. |
| `kata` | User | `Standard_D4s_v5` | 1-2 nodes | Sandbox pods that use RuntimeClass `kata-vm-isolation`. |

The `kata` pool uses Azure Linux with `workloadRuntime: KataVmIsolation`
(AKS Pod Sandboxing). SSH access to its nodes is disabled. The pool has no
taint, because sandbox pods do not have a toleration. Other pods can also run
on a `kata` node, which can reduce the space for sandboxes.

The pool keeps at least one node. A node scale-up takes longer than the
30-second sandbox scheduling budget, so a sandbox cannot wait for a new node.

Cost: `Standard_D4s_v5` Linux pay-as-you-go in `eastus2` is USD 0.192 per hour
(Azure retail price, November 2026). That is about USD 140 per node per month,
plus the managed OS disk. The pool has a maximum of two nodes. Check the
[Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/)
for your region and agreement.

Quota: the `system` pool can use up to 6 vCPUs and the `kata` pool up to 8
vCPUs, both in the DSv5 family. Make sure that the regional DSv5 vCPU quota of
the subscription is at least 14. Otherwise the autoscaler cannot add nodes.

Both `main.bicep` and `aks-redeploy.bicep` create or update the pool with
`modules/aks-kata-pool.bicep`. The module is a nested deployment, so the
top-level what-if does not show it. For the AKS-only scope, `npm run azure:plan`
adds the pool declaration to `nestedDeploymentChanges.agentPools`. To add the
pool to an existing cluster, run the AKS-only deployment. You do not need a
manual `az aks nodepool add`.

The agent pool PUT does not set a node count, so a later deployment keeps the
autoscaler's current count. The OS SKU, workload runtime, VM size, and subnet
of a node pool cannot change. If an existing pool named `kata` has other
values, the deployment fails. Delete that pool, then run the deployment again.

After the deployment, a Ready node has the label
`kubernetes.azure.com/kata-vm-isolation=true`:

```powershell
kubectl get nodes --selector kubernetes.azure.com/kata-vm-isolation=true
```

`npm run deploy:kustomize` checks for this node before it writes to the
cluster.

## Azure Monitor add-on

Every installation enables the AKS Azure Monitor add-on (Container Insights).
You cannot turn it off. The per-run egress audit needs the add-on to forward
ACNS container network logs to the `ContainerNetworkLogs` table. The only
exception is a cluster in a canary region; see
[Canary-region clusters](#canary-region-clusters).

The templates do not declare the add-on. After each successful
`scripts/azure/deploy.mjs --execute` run, for both `main.bicep` and
`aks-redeploy.bicep`, the installer does these steps:

1. It reads `addonProfiles.omsagent` from `<prefix>-aks`.
2. If the add-on is enabled with the `<prefix>-law` workspace and managed
   identity authentication (`useAADAuth`), the installer adopts it. It makes
   no change.
3. If the add-on is not enabled, the installer reads the cluster region and
   the `<prefix>-law` region. If the cluster is in a canary region and the
   workspace is not, it skips the add-on (see
   [Canary-region clusters](#canary-region-clusters)). Otherwise it runs
   `az aks enable-addons --addons monitoring` with `<prefix>-law`, managed
   identity authentication, and the `<prefix>-ampls` private link scope. Azure
   CLI creates the data collection rule, the data collection endpoint, and the
   association.
4. It reads the profile again and requires the add-on with `<prefix>-law`.

The installer stops if the add-on sends to another workspace or uses
shared-key authentication. It does not disable monitoring. Disable the add-on,
then run the deployment again. If the enable step fails, the error starts with
`Infrastructure deployment succeeded, but Azure Monitor add-on setup failed`.
You can run the deployment again.

The `aks-redeploy.bicep` what-if still has no managed-cluster change. The
add-on step is a separate Azure CLI call. Use `main.bicep` only for an initial
deployment. If you run it on an existing cluster, the AKS PUT can remove the
add-on; the same run then enables it again.

The identity that runs the installer needs these permissions in the resource
group: `Microsoft.ContainerService/managedClusters/read` and `write`,
`Microsoft.Insights/dataCollectionRules/*`,
`Microsoft.Insights/dataCollectionEndpoints/*`,
`Microsoft.Insights/dataCollectionRuleAssociations/*`,
`Microsoft.Insights/privateLinkScopes/scopedResources/*`, and
`Microsoft.OperationalInsights/workspaces/read` and `sharedKeys/action`. The installer
grants no roles.

`npm run deploy:kustomize` checks that DaemonSet `ama-logs` in `kube-system`
is ready on every scheduled node before it writes to the cluster. The
deployment identity needs `get` on `daemonsets.apps/ama-logs` in
`kube-system`.

### Canary-region clusters

A cluster in a canary region (a region name that ends in `euap`, for example
`eastus2euap`) cannot use the add-on when the workspace is in a production
region. The example parameters use this layout: AKS in `eastus2euap` and
`<prefix>-law` in `eastus2`. Azure Monitor has these limits:

- The Container Insights data collection rule must be in the workspace region.
  Azure rejects a rule in another region with `InvalidWorkspace`.
- A canary-region cluster can only use a data collection rule in
  `eastus2euap`. A rule in a production region fails with
  `InvalidAssociation` ("Rule Id ... does not exist").
- Log Analytics workspaces may not be available in canary regions for your
  subscription.
- Shared-key authentication for Container Insights was retired on
  2026-09-30.

For this layout, the installer does not run `az aks enable-addons`. It prints
a warning, and the receipt contains `azureMonitorAddon` with `action` set to
`unsupported-region`, `clusterLocation`, and `workspaceLocation`. If the
add-on is already enabled, the installer adopts or stops as usual.

`npm run deploy:kustomize` then accepts a missing `ama-logs` DaemonSet only
when it can list the nodes, the list is not empty, and every node has a
`topology.kubernetes.io/region` label that ends in `euap`. It prints a
warning and continues. It needs `list nodes` at cluster scope for this check.
If `ama-logs` exists, it must be ready.

In this layout, Container Insights data, `ContainerNetworkLogs`, and the
egress audit data that depends on them are not collected. To collect them,
put the cluster in a production region, or use a workspace in `eastus2euap`
if Log Analytics is available there for your subscription.

Cost: Log Analytics charges for the ingested Container Insights data in
`<prefix>-law` (`PerGB2018`, 30-day retention). The add-on also runs one
`ama-logs` pod on each node and one `ama-logs-rs` pod. Cost depends on the log
volume. Check the
[Azure Monitor pricing](https://azure.microsoft.com/pricing/details/monitor/)
for your region and agreement.

## Local compilation

Run the local compiler:

```powershell
az bicep build --file infra\bicep\main.bicep --stdout
az bicep build --file infra\bicep\aks-redeploy.bicep --stdout
az bicep build --file infra\bicep\p1-runtime-identities.bicep --stdout
az bicep build --file infra\bicep\p1-session-material-access.bicep --stdout
```

This command requires no Azure login.
The parameter examples contain placeholders and are not deploy-ready.
The deployment tooling accepts tracked reviewed JSON parameters, not arbitrary
files or caller-supplied source templates. The dedicated AKS update uses
`aks-redeploy.bicep` with `parameters/p0-aks-redeploy.approved.json`; use the
full `main.bicep` composition only for an approved initial deployment into an
empty managed-resource target.
It injects exact `sourceSha` and `sourceHash` parameters and reads matching outputs.

Neither deployment path grants a human AKS role or accepts an operator object
ID. The AKS control-plane identity retains Network Contributor only on the
dedicated AKS subnet. Infrastructure deployment does not create Kubernetes
namespaces or apply workload manifests. The existing-cluster path gets the issuer URL and the managed Application
Routing domain from AKS without redeploying the cluster. It updates only the
existing probe, broker, and migration federated credentials. Their ServiceAccount subjects and
`api://AzureADTokenExchange` audience remain unchanged. The guard verifies all
three existing records before deployment and blocks if one is missing or has
different bindings.
The parent what-if does not show resources inside the nested federation module.
The AKS-only preflight lists the three credential create-or-update declarations,
but does not query existing credentials to distinguish creates from updates.

## Identity and PostgreSQL

The `foundation-probe` UAMI and ServiceAccount are reserved for the foundation probe
acceptance-only Job. Identity is the product host; Relay is a persistence
library and release is a CLI. The identity module defines separate
`identity-broker` runtime and `identity-broker-migration` UAMIs and federated
ServiceAccounts. Only the runtime identity receives Key Vault Secrets User on
the exact existing vault. The migration identity receives no Azure resource
role. Named outputs preserve the probe DTO and expose the two Identity
identities separately.

The PostgreSQL Entra administrator definition does not create a runtime
database principal, schema, or table.
The reviewed parameter file supplies its exact object ID, principal name, and
supported `principalType` (`User`, `Group`, or `ServicePrincipal`). `Unknown`
is rejected. A directory group is not required; use the existing approved
operator principal when its native type is supported.
`postgres-bootstrap.sql` defines a separately approved admin procedure.
It maps the UAMI **principalId**, not clientId, through the native
`pgaadauth_create_principal_with_oid` function while connected to `postgres`.
It separates the runtime role from the approved migration/schema owner.

The generic persistence procedure requires `runtime_role`, `principal_oid`,
`database`, `service_schema`, and `migration_role` psql variables. Identity
uses separate `postgres-identity-bootstrap.sql` and
`postgres-identity-runtime-grants.sql` definitions. The Identity bootstrap
requires the two operator-selected role names, their UAMI principal object IDs,
and the database name. It creates `identity_broker` under the migration role
and grants the runtime role schema USAGE only.

After the separate `identity-broker-migration` Job applies EF migrations, the
Identity runtime-grants file allows DML only on the current Identity/OpenIddict
tables and SELECT on the migration history. It gives runtime no schema CREATE,
database administration, default table privileges, or migration-registry
writes. Neither Identity SQL file runs from tooling, CI, or ordinary host
startup. Existing role memberships and inherited/PUBLIC rights require an
audit; `NOINHERIT` alone does not prevent `SET ROLE`.

The Identity host uses native asynchronous Npgsql password-provider callbacks
with its explicit `WorkloadIdentityCredential` for each new physical
connection. Startup verifies the schema and migration state but never applies
migrations or provisions database roles. The
[infrastructure specification](../../docs/specs/azure-p0-infrastructure.md)
records the full acceptance boundary. No live database or Azure operation is
part of these definitions.

The Events & Sessions runtime identity definition grants that identity
Storage Blob Data Contributor only on the private `session-material` container.
It does not grant that identity access to the storage account or other
containers.

`p1-postgres-migration-identity.bicep` defines the database migration identity,
`agentweaver-id-p1-postgres-migration`, and one federated credential for the
`agentweaver:p1-postgres-migration` ServiceAccount. It assigns no Azure role.
Its parameters are `namePrefix`, `tags`, and the 40-character `sourceSha`. The
[database maintenance runner](../../docs/guide/database-maintenance.md) deploys
it to the existing resource group. A rerun changes nothing.

`db-maintenance-ci-identity.bicep` defines the existing v1 workflow identity
`agentweaver-id-db-maintenance` and the dedicated
`agentweaver-id-projects-config-migration` identity. GitHub Actions uses OIDC
from the `v1-db-maintenance` environment; the workflow identity retains
Reader on the resource group and AKS Cluster User on the cluster, with no
custom Azure role. The migration identity receives no Azure resource role and
is federated only to the `projects-config-migration` ServiceAccount in
`agentweaver`. The setup command applies and verifies the namespace Role and
RoleBinding for the workflow identity plus the migration ServiceAccount
annotation. `npm run db:maintenance:setup -- --v1-deploy-only --execute`
performs that one-time setup for the shared database-maintenance workflow
identity and namespace permissions. It skips the maintenance runner's
`prepareOnly` step, which prepares the PostgreSQL migration identity, its
ServiceAccount and egress policy, and the schema ownership handover. It sets
`AZURE_DB_MAINTENANCE_CLIENT_ID` in the GitHub environment used by that
workflow; it does not configure a hosted AKS deployment workflow.

The Projects & Config migration identity must also be mapped to PostgreSQL as
a schema owner. Its role needs database `CONNECT` and ownership only of the
`projects_config` schema and its exact planned objects; it must not own the
database, retain database `CREATE`, or access other schemas. The one-time
Azure setup does not change PostgreSQL ownership. Do not use the
DDL-restricted runtime principal or the PostgreSQL migration identity.

The Projects & Config migration Job uses the projected Workload Identity
token, applies EF migrations, then reconciles the reviewed runtime grants in
the same migration operation. Ownership and object-inventory checks fail
closed before grants; grant application and least-privilege readback are
transactional. Extend and review the plan when new runtime permissions are
needed; do not use blanket/default grants.

The plan preserves the project role-assignment boundary: only `INSERT` on
`project_role_assignments` and `authority_audit`, plus `UPDATE` on
`state`, `revision`, `revoked_by`, and `revoked_at`; it grants no `DELETE` and
no writes on `tenant_memberships`. See
[Runtime authority writes](../../docs/reference/runtime-configuration.md#runtime-authority-writes).
