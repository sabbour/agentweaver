# Dedicated Azure P0 definitions

This directory defines infrastructure. It is not a deployed environment.
No Azure provisioning, secret reads, permission changes, or cost-bearing operation
occurred for this change.

`main.bicep` composes a dedicated VNet, AKS with a public API endpoint and OIDC/workload identity,
Entra-only PostgreSQL, Key Vault, Blob, and Azure Monitor.
Key Vault and Blob have Private Endpoints and private DNS.
PostgreSQL has a delegated subnet and private DNS.

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
```

This command requires no Azure login.
The parameter examples contain placeholders and are not deploy-ready.
The deployment tooling accepts tracked reviewed JSON parameters, not arbitrary
files or caller-supplied source templates.
It injects exact `sourceSha` and `sourceHash` parameters and reads matching outputs.

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
