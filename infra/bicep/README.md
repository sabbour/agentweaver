# Dedicated Azure P0 definitions

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
`aks-redeploy.bicep` is a separate, narrow identity-federation refresh for an
existing P0 foundation. It reads the existing AKS issuer, identity, and
Application Routing domain; it updates only the three existing workload-identity
federated credentials. It does not PUT the AKS resource or change its routing,
RBAC, network, data-service, private-DNS, or monitoring configuration.
If AKS omits `defaultDomain` for custom DNS, its diagnostic output uses
`false` and `null`.

The P0 parameter example keeps the VNet, AKS, PostgreSQL, Key Vault, Blob, and
their private endpoints in `eastus2euap`. Its required `monitorLocation` places
Log Analytics and Application Insights in `eastus2`; the AMPLS remains global,
and the Monitor Private Endpoint stays in the VNet's `location`. PostgreSQL
continues to use the delegated subnet in the primary `location`; no cross-region
database subnet is introduced.

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
The #1784 Job overlay owns explicit egress and executable runtime proof.

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

The `foundation-probe` UAMI and ServiceAccount are reserved for the #1784
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
[infrastructure specification](../../docs/specs/1777-azure-p0-infrastructure.md)
records the full acceptance boundary. No live database or Azure operation is
part of these definitions.

The P1 runtime identity definition grants the Events & Sessions identity
Storage Blob Data Contributor only on the private `session-material` container.
It does not grant that identity access to the storage account or other
containers.

`p1-postgres-migration-identity.bicep` defines the P1 migration identity,
`agentweaver-id-p1-postgres-migration`, and one federated credential for the
`agentweaver:p1-postgres-migration` ServiceAccount. It assigns no Azure role.
Its parameters are `namePrefix`, `tags`, and the 40-character `sourceSha`. The
[database maintenance runner](../../docs/guide/database-maintenance.md) deploys
it to the existing resource group. A rerun changes nothing.

`db-maintenance-ci-identity.bicep` defines the identity that the database
maintenance workflow signs in as, `agentweaver-id-db-maintenance`. Its only
federated credential trusts GitHub Actions jobs that use the
`v1-db-maintenance` environment. It assigns Reader on the resource group, AKS
Cluster User on the cluster, and no custom Azure role. The setup script applies
a Kubernetes Role and RoleBinding only in the `agentweaver` namespace. The Role
allows the maintenance Jobs, pod logs, ConfigMaps, Leases, ServiceAccounts,
Cilium network policies, and the `orchestrator` and `events-and-sessions`
Deployments used by the runner. The identity cannot deploy Azure resources,
assign roles, or connect to PostgreSQL. Its parameters are `namePrefix`,
`tags`, `sourceSha`, `githubRepository`, and `githubEnvironment`.
`npm run db:maintenance:setup` deploys the identity and checks the Azure
assignments, then applies and reads back the Kubernetes authorization.

Projects & Config applies migrations as its schema owner. After migration
`20261009021400_AddCastingAuthorityLockFunction` is applied, an approved
operator can grant the runtime role `EXECUTE` on the exact casting
authority-lock function and the project role-assignment write set:

```powershell
psql "$PROJECTS_CONFIG_DATABASE_URL" `
  -v runtime_role="approved_projects_config_runtime" `
  -f infra\bicep\postgres-projectsconfig-runtime-grants.sql
```

The file defines these data-table grants for the project role-assignment API:

- `INSERT` on `projects_config.project_role_assignments`.
- `UPDATE (state, revision, revoked_by, revoked_at)` on
  `projects_config.project_role_assignments`.
- `INSERT` on `projects_config.authority_audit`.

It grants no `DELETE` and no write access to `tenant_memberships`. The API
allows these writes only for a current TenantAdmin and audits each change in
the same transaction. Don't add other privileges to enable row locking. The
function is owned by the migration/schema owner, returns authorization status
only, and is not executable by `PUBLIC`. The SQL file is an operator
definition, not an automatic grant or deployment. Each live grant needs a
separate approval. See
[Runtime authority writes](../../docs/reference/runtime-configuration.md#runtime-authority-writes).
