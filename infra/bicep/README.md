# Dedicated Azure P0 definitions

This directory defines infrastructure. It is not a deployed environment.
No Azure provisioning, secret reads, permission changes, or cost-bearing operation
occurred for this change.

`main.bicep` composes a dedicated VNet, AKS with OIDC/workload identity,
Entra-only PostgreSQL, Key Vault, Blob, and Azure Monitor.
Key Vault and Blob have Private Endpoints and private DNS.
PostgreSQL has a delegated subnet and private DNS.

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
acceptance-only Job. The operator CLI is not that identity.
Identity is the product host. Relay is a library, and release is a CLI.
This directory does not invent runtime identities for those libraries or CLIs.

The PostgreSQL Entra administrator definition does not create a runtime
database principal, schema, or table.
`postgres-bootstrap.sql` defines a separately approved admin procedure.
It maps the UAMI **principalId**, not clientId, through the native
`pgaadauth_create_principal_with_oid` function while connected to `postgres`.
It separates the runtime role from the approved migration/schema owner.

The procedure requires `runtime_role`, `principal_oid`, `database`,
`service_schema`, and `migration_role` psql variables.
It fails on existing/conflicting principals or schemas rather than silently
changing ownership. It does not run from tooling or CI.
Existing role memberships and inherited/PUBLIC rights require an audit.
The runtime role cannot run the current `PostgresOutbox.MigrateAsync`.
The approved migration principal must apply migrations first.
`postgres-runtime-grants.sql` then defines only the required persistence table
grants. Runtime cannot edit the migration registry.
#1784 owns grants for its domain-effect table and sequences, if necessary.

The [specification](../../docs/specs/1777-azure-p0-infrastructure.md) records
native token scope, password-provider requirements, source guards, and acceptance
evidence. No runtime PostgreSQL credential code enters this slice.
