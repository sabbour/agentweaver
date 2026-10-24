# Azure P0 operator tooling

These dependency-free tools define and inspect infrastructure.
They do not deploy a service or prove workload identity.
The [Azure P0 infrastructure specification](../../docs/specs/azure-p0-infrastructure.md)
describes source receipts, target guards, and the separate
[Foundation Probe](../../docs/specs/foundation-probe.md) runtime gate.

## Offline validation

Issue sync adds Orchestrator `027_periodic_issue_sync.sql`, requiring the
backlog contract's 026, and Identity Broker
`20261021000100_AddIssueSyncGrants`. They are **unapplied code-only migrations**.
Orchestrator requires exactly `SELECT, INSERT, UPDATE` on `issue_sync_projects`
and `issue_sync_receipts`; Identity requires exactly `SELECT, INSERT, UPDATE`
on `identity_broker.issue_sync_grants`, and `SELECT, INSERT` only on
`identity_broker.issue_sync_mint_audit`. No new DELETE, default privilege,
schema CREATE or authority function EXECUTE is granted. These database grants,
the new P1 plan hash, service TLS configuration, and live application-level
issue-sync grant creation remain separate approval items. The Projects & Config
runtime role receives exactly `SELECT, INSERT, UPDATE` on
`casting_generation_requests`; this grant is included in the same reviewed plan.
Source validation does not perform a migration, grant or deployment action.
The offline plan hash for this source catalog is
`ea1303e02ca0381735704de35b2ddf61e2e484d5ce64633d2fbc45170fd01f9b`.
It is not an approval receipt. The runner uses its explicit registered version
list, not contiguous numbering; the absent 017 does not require a
placeholder. The Core list is 1-16, 18-33, and 036; reserved versions 034 and
035 may be skipped. Version 027 requires 026 only.
Version 028 adds the append-only `guardrails_evaluations` table. It requires
027 and needs exactly `SELECT, INSERT` on `orchestrator.guardrails_evaluations`
plus the `reject_guardrails_evaluation_mutation()` trigger function. It is an
**unapplied code-only migration**.
Version 029 adds the `spend_budget_reservations` and
`spend_budget_accounting_cursors` tables for spend budget enforcement. It
requires 028 and needs exactly `SELECT, INSERT, UPDATE` on both tables, with no
DELETE. It is an **unapplied code-only migration**.

Orchestrator migration `032_source_control_pull_request_records.sql` adds the
`source_control_pull_requests` table for the project pull request list. It
requires migration 012 and is part of the reviewed Core catalog and default P1
maintenance plan. The source-bound plan hash covers migration 032 and its exact `SELECT, INSERT,
UPDATE` grant on
`orchestrator.source_control_pull_requests`. The Orchestrator migration Job
applies it before 033 and verifies the complete ledger and table. Startup still
accepts a complete pre-032 schema during rollout, but the pull request list
returns `503 pull_request_records_unavailable` until the maintenance Job applies
the plan.

Migration `033_priority_link_repository_identity.sql` requires only 024, not
032. It adds nullable provider and repository columns while preserving legacy
rows. In one transaction it checks the data, creates separate unique indexes
for identified and number-only links, then drops only the obsolete target
index. No rows, tables, columns, schemas or grants are removed.

Deploy the compatible owner images first on schema 031, then apply 033.
The new owner accepts both complete shapes in startup and readiness verification.
On 031, issue and pull request link saves return `database_update_pending`
instead of losing repository identity; existing operations continue.
On 033 those saves work. Older images reject 033 and are not a rollback target.
The source-owned machine-readable compatibility list is
`services/orchestrator/Agentweaver.Orchestrator/schema-compatibility.json`.
Migration `036_maf_root_model_turn_admissions.sql` adds an append-only owner
record for coordinator-root model-turn admission. Root generation fails closed
until the table and its immutability trigger are applied. It is optional to
readiness during rollout and has no mutation grant; the runtime role receives
only `SELECT, INSERT`. The table references run/session records from version 001
and coordinator decisions from version 002; its immutability trigger reuses the
function introduced in version 021, which is its prerequisite. The migration
runner applies the registered catalog in ascending order and permits the
reserved gaps at 034 and 035.
The Core catalog and plan include 032, 033, and 036; the maintenance Job applies
the included versions in order.
Existing grants on the Priority link table cover the added columns.

Orchestrator migration `026_backlog_task_content.sql` adds task content,
open/closed lifecycle and a unique repository issue identity. It requires
migration 021 and has no schema dependency on migration 025, which requires
only migration 002. The migrator applies missing registered versions in
ascending order. It takes an
`ACCESS EXCLUSIVE` lock on `backlog_tasks` and blocks writes until commit.
It adds no tables or runtime privileges: existing `SELECT, INSERT, UPDATE`
grants on `backlog_tasks` and `backlog_projects` remain sufficient.
The Core version list changes the PostgreSQL plan hash. Applying migration
026, reconciling existing grants, and accepting the new exact plan hash are
separate, unapplied operator steps that require approval. Source checks do
not authorize or perform them.

Orchestrator migration `030_backlog_task_origin.sql` adds the
`backlog_plan_promotions` and `backlog_task_origins` tables. They record
each promotion of a confirmed work plan into backlog tasks and the origin of
each promoted task. The migration does not alter `backlog_tasks`. Its foreign
keys take `SHARE ROW EXCLUSIVE` locks on `backlog_projects`,
`backlog_tasks`, and `coordination_sessions`, which block writes to those
tables until commit. The plan grants the orchestrator role exactly
`SELECT, INSERT` on both new tables, with no `UPDATE`, `DELETE`, or default
privileges. Migration 030 must be applied before the promotion routes work.
Applying it, granting those privileges, and accepting the new exact plan hash
are separate operator steps that require approval.

Run these commands from the repository root:

```powershell
npm run test:azure
az bicep build --file infra\bicep\main.bicep --stdout
az bicep build --file infra\bicep\aks-redeploy.bicep --stdout
kubectl kustomize deploy\k8s\base
npm run release:validate
npm run test:release
```

No Azure account, credentials, or cluster connection is necessary.
Tests use fake transports and generated local fixtures.
The only real Azure CLI test reads local CLI version information.

## Identity Broker post-migration permissions

The initial Broker database bootstrap creates the migration-owned
`identity_broker` schema and gives the runtime schema `USAGE`. Before tables
exist, `IDENTITY_POSTGRES_RUNTIME_GRANTS_PENDING` is expected; it does not
prove runtime readiness.

The Broker migration Job applies migrations and then the embedded
`infra/bicep/postgres-identity-runtime-grants.sql` as the migration identity.
The Job explicitly configures `IdentityBroker__Migration__RuntimeRole` as
`agentweaver-id-identity-broker`. The grant transaction checks ownership of
20 named tables, applies only their approved permissions, and keeps
`identity_broker.__ef_migrations_history` read-only for the runtime. A rerun
with a corrected image also applies grants when there are no pending migrations.
The guarded operator bootstrap applies the same definition after migration.

Read-only `--verify-identity-postgres-bootstrap` verifies role and schema
boundaries and, once history exists, the same post-migration table permissions.
It fails on missing history `SELECT` and never applies a grant.

For the exact repair SQL, identity, and approval boundary, see
[Identity Broker migration history and grants](../../docs/reference/runtime-configuration.md#identity-broker-migration-history-and-grants).
Merging a source fix does not repair the existing database. A separately
approved migration Job rerun must use an image containing the fix; an old
image does not gain the new grant step. The guarded bootstrap can apply the
existing canonical grants without a new Broker image, but it reconciles all
listed table grants and uses temporary cluster resources. Neither path is
authorized by a successful source test or by merging a pull request.

## Limited P1 runtime identities

`infra/bicep/p1-runtime-identities.bicep` is separate from the full foundation deployment.
It uses the existing resource group location and the `agentweaver-aks` OIDC issuer.
Before applying it, verify that the resource group and cluster locations match.
It creates only the Projects & Config, Environment, Knowledge, Events & Sessions,
and Orchestrator runtime identities and their exact ServiceAccount federations.
Every federation names its service in `agentweaver` and accepts only
`api://AzureADTokenExchange`.
The Events & Sessions identity receives Storage Blob Data Contributor only on
the existing private `session-material` container.
The target preflight does not enumerate existing resources or compare
federated-credential bindings after deployment.
Supply the existing resource group tags and the exact reviewed source SHA.

This template defines no database grant, migration, certificate, server,
storage account, cluster, or node pool change. It does not alter Broker, Probe,
or migration identities.
Applying it requires approval for these five identities and workload links.
A created identity does not prove database access or a ready service.

### Session-material container repair

Use `infra/bicep/p1-session-material-access.bicep` only when the approved
`agentweaver` resource group already contains the `agentweaverblob` storage
account and `agentweaver-id-events-and-sessions` identity, but is missing the
private `session-material` container or its container-scoped role assignment.
This focused template creates only that container and the Events identity's
Storage Blob Data Contributor assignment on it. It does not create or update
identities, federations, the storage account, networking, or workloads.

After separate operator approval, apply it with:

```powershell
az deployment group create `
  --subscription 90ab3701-83f0-4ba1-a90a-f2e68683adab `
  --resource-group agentweaver `
  --name agentweaver-session-material-access `
  --template-file infra\bicep\p1-session-material-access.bicep `
  --parameters namePrefix=agentweaver
```

This command is an operator procedure, not a command run by local source checks
or by merging a pull request. The template requires the storage account and
identity to exist before deployment.

### P1 PostgreSQL initialization and migration plan

The recorded continuation receipt uses plan hash
`a056bf074940b858b7eb05c78c7e96e2e6663d5b7ea291d99997eaccbbf073c8`.
This hash identifies a historical plan. It does not prove that the live
database matches that plan or the current migration catalog. Use `--inspect`
to read the live migration versions, ownership, grants, and planned relations.
Use `--apply-pending-migrations` only for separately approved updates, as
described in [Applying later approved migrations](#applying-later-approved-migrations).

Never run `--initialize` against an existing database. That mode requires an
empty target. A failed receipt also does not prove that a database transaction
rolled back. Inspect the live database before retrying any database operation.

Initialization requires `--initialize` with full source and Core merge SHAs,
the exact source plan hash, and a new receipt path under that source copy's
`Temp` directory. It requires a clean source checkout whose `HEAD` and fetched
`origin/v1` both match the supplied source SHA. It validates the reviewed Core
merge and required migration wiring, and recomputes the plan hash. It then
requests an Azure CLI token, connects to verify the exact operator and fresh
target, and runs the permission preflight before role creation. These checks
block unsafe writes; they do not prevent credential requests or database
connections. The CLI arguments are technical guards, not approval.
The source validates all planned row-lock mutation guards and, before
committing grants, reads back each installed trigger and function to verify the
exact table, trigger type, function body/owner, and absence of runtime EXECUTE.
The source catalog requires Core versions 1 through 16 and 18 through 33.
These are 31 registered versions; versions 17 and 32 remain excluded.
Version 22 adds the run completion time. It needs no new grant because the
runtime role already has table-level UPDATE on `accepted_runs`.
Version 23 adds durable interactive requests, cancellation receipts,
`cancelled_at`, and the `cancelled` state. Its runtime grants are
`SELECT,INSERT,UPDATE` on `project_run_requests` and `SELECT,INSERT` on
`project_run_cancellations`. Its execution-state CHECK validation scans
`accepted_runs`. Its `ACCESS EXCLUSIVE` lock blocks reads and writes to `accepted_runs`
until the migration transaction commits. It doesn't alter `sessions` or build indexes on
existing tables. Review the table size and plan a write pause.
Version 24 adds private Chief of Staff conversations and Priorities in four new,
empty tables. It doesn't alter or scan existing tables. Its runtime grants are
`SELECT,INSERT` on `chief_of_staff_conversations`, `SELECT,INSERT,UPDATE` on
`chief_of_staff_priority_lists` and `chief_of_staff_priorities`, and
`SELECT,INSERT,DELETE` on `chief_of_staff_priority_links`.
Version 25 adds the empty `user_notification_dismissals` table. Its only
predecessor is version 2, which creates `coordinator_gates`. It doesn't alter or
scan existing tables. Its runtime grant is `SELECT,INSERT` on that table.
Version 26 adds backlog task content columns and a unique repository issue index.
It requires version 21, adds no tables or grants, and blocks writes to
`backlog_tasks` until commit.
Version 30 adds the empty `backlog_plan_promotions` and `backlog_task_origins`
tables. It requires version 26 and doesn't alter existing tables. The migrator
applies it after version 29 because it follows catalog order. Its foreign keys
take `SHARE ROW EXCLUSIVE` locks that block writes to `backlog_projects`,
`backlog_tasks`, and `coordination_sessions` until commit. Its runtime grant is
`SELECT,INSERT` on each new table.
Version 31 adds the empty `user_notification_promotion_dismissals` table.
It stores caller dismissals of backlog promotion notices and has a foreign key
to `backlog_plan_promotions`, so it requires migrations 025 and 030.
The foreign key takes a `SHARE ROW EXCLUSIVE` lock on
`backlog_plan_promotions` until commit. Its runtime role needs only
`SELECT,INSERT` on the new table.
Events requires versions 1 through 10 and preserves the earlier version-2 layout.
The initializer compares each plan catalog with the migrator's compiled
`SupportedVersions` list instead of matching migrator source text.
The plan lists 115 tables and nine exact row-lock columns.
Applying migrations or grants, or running initialization with the new plan hash,
requires separate approval.
The plan contains five principal object IDs from the approved target snapshot.
The initializer does not query Azure to refresh those IDs. Review the plan and
its hash against the approved deployment before initialization; unresolved
markers are rejected before credential requests.
The Environment runtime bootstrap profile replacement store receives exactly
`SELECT, INSERT` on `environment.runtime_bootstrap_profile_replacements`. It
receives no `UPDATE`, `DELETE`, or default privileges. The source catalog lists
29 exact function signatures and grants runtime `EXECUTE` only to the casting
authority.
The five new update guards reject changes with SQLSTATE `55000`.
Three history guards use SQLSTATE `P0001` and block updates, deletes, and truncates.

### Continuing from a verified Projects-only prefix

`--continue-after-projects-config` is a separate continuation mode for a database
whose five runtime roles already exist and whose Projects & Config schema is the
only migrated service schema. It requires the exact prior source, plan hash, and
receipt bytes, and writes a new receipt rather than replacing the old one.
Before creating another service schema, the continuation validates the exact
Projects migration history, object inventory, ownership, ACLs, and setup guards.
It rejects unplanned views and materialized views, including views that could
expose owner-only data after runtime schema access is granted.
Before the final grant transaction, planned non-runtime functions may have only
their exact owner grant, or that grant plus PostgreSQL's default `PUBLIC EXECUTE`;
the casting function must remain owner-only. Any grant to a runtime or other
role is rejected. Runtime roles have no schema access at this point. The final
transaction revokes `PUBLIC EXECUTE` from every planned function and grants
direct EXECUTE only to the planned casting role, then checks the complete ACL
state before commit.

The continuation migrates the remaining four service schemas without runtime
table, sequence, or function grants. It verifies each exact migration inventory
and owner-owned object inventory before applying the one final grant transaction.
A failure stops the sequence and preserves the earlier receipt. These source
checks and disposable PostgreSQL tests do not authorize or prove live database
execution.

Run the continuation checks against a disposable PostgreSQL container:

```powershell
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresContinuationPostgresTests
```

### Applying later approved migrations

For routine updates, use the
[database maintenance runner](../../docs/guide/database-maintenance.md)
(`npm run db:maintenance`). It runs the migrations as in-cluster Jobs and needs
no tunnel. `npm run db:maintenance:setup` prepares it once: it creates the
migration and workflow identities, runs the schema ownership handover, and
configures the GitHub environment, then applies and checks the workflow
identity's namespace-scoped Kubernetes Role and RoleBinding. It creates no
custom Azure role definition. Without `--execute` it only checks. After the
handover, the operator no longer owns the P1 schemas, and the two modes below
fail their ownership checks.

Use these modes for a database that is already initialized. Neither needs a
prior receipt, recreates roles or schemas, resets data, or reruns the Identity
Broker bootstrap. Both still require a clean checkout whose `HEAD` matches the
fetched `origin/v1`. The script passes that commit to the initializer as the
source, together with the plan hash computed from it.

`--inspect` is read-only. Both database sessions run with
`default_transaction_read_only=on`, and the tool checks that setting before it
reads. It reports the plan principals, Entra service principals, database
grants, and Identity Broker history. For each service it reports schema owner
and ACL, migration history, planned, pending, and unplanned versions, relations
with runtime privileges, and missing planned relations. It writes no receipt and
ends with `P1_POSTGRES_INSPECTION_FINISHED writes=false`. It takes no Core
merge SHA.

```powershell
node scripts\azure\bootstrap-p1-postgres.mjs --inspect --execute
```

`--apply-pending-migrations` requires `--max-core-version` and
`--max-events-version`. Each maximum must equal the highest version in the plan,
and the plan must equal the migrator's compiled catalog. A source with a later
migration therefore fails closed instead of applying it, because the migrators
apply every missing catalog version. Before any write, the mode checks:

- the five runtime roles, object-ID mappings, and operator memberships;
- each runtime role's exact database `CONNECT`;
- each service schema's owner and ACL;
- that every existing relation is planned, operator-owned, and within its
  planned runtime privileges;
- that Core and Events histories are exact prefixes of the plan, with no gaps
  or unplanned versions. Other services must already match the plan exactly.

Add `--dry-run` to stop after these checks; it prints the pending versions and
`P1_POSTGRES_PENDING_MIGRATIONS_DRY_RUN writes=false`. Without `--dry-run`, the
mode applies only the pending versions and verifies each migrated history and
object inventory. It then applies the exact plan grants in one transaction,
reads back every ACL before commit, and writes a new receipt under `Temp`. It
ends with `P1_POSTGRES_PENDING_MIGRATIONS_APPLIED`. That marker does not prove
runtime identity or deployment.

```powershell
node scripts\azure\bootstrap-p1-postgres.mjs --apply-pending-migrations --dry-run --max-core-version <highest-core> --max-events-version <highest-events> --core-merge-sha <sha> --execute
node scripts\azure\bootstrap-p1-postgres.mjs --apply-pending-migrations --max-core-version <highest-core> --max-events-version <highest-events> --core-merge-sha <sha> --execute
```

Run `--inspect` again after applying, and prove each new version from the live
history.

The source preflight rejects incompatible PUBLIC privileges before the first write.
It also rejects default grants to unrelated principals and an inherited-only
database CONNECT grant option. It does not revoke existing privileges to proceed.
Database access lists use effective grants, including PostgreSQL defaults when
the catalog access list is NULL. Only the exact new-role CONNECT additions are
permitted. Existing owners, grantors, grant options, schemas, Broker, Probe,
functions, and default privileges must remain unchanged.

Before role creation commits, the source checks all five object-ID mappings,
service principal types, role flags, and one existing operator ADMIN membership
per mapped role. It captures each membership's grantor OID from the catalog and
requires that exact OID to remain unchanged through the role setup.
All five mappings must use the reviewed tenant.
The administrator and MFA-required integer flags must each equal zero.
Missing flags and any nonzero integer fail the source checks.
The operator membership has ADMIN but neither INHERIT nor SET permission.
Function checks require the operator owner and the exact effective EXECUTE grants.
Only the existing casting authority function can receive runtime EXECUTE.

The role changes set `NOINHERIT` and disable membership `INHERIT` and `SET`.
They do not repeat `ADMIN TRUE`: PostgreSQL 16 rejects granting ADMIN back to
the grantor that originally granted it. If that grantor is not the operator,
the source removes only the temporary non-ADMIN membership it creates under
the operator's own grantor identity. It never impersonates the original grantor.
Final readback must show exactly one membership with the original grantor OID,
ADMIN true, and INHERIT and SET false; otherwise the role transaction rolls back.
The source does not rewrite SUPERUSER,
CREATEDB, CREATEROLE, REPLICATION, or BYPASSRLS, because PostgreSQL 16 requires
matching privileges even to set those flags false. Their false state is verified
by readback before COMMIT, and any unexpected true flag rejects the transaction.
The receipt records fixed stage names (`role_creation`, `role_flag_change`,
`role_membership_change`, `role_membership_cleanup` when needed, `role_readback`,
`role_commit`) before each step,
alongside the SQLSTATE of a failure and never raw errors or payloads.

Pure catalog tests compare the registered resources, function signatures, and
guard definitions. They also check partial-stage reporting and failure-receipt
errors. A separate disposable Testcontainers PostgreSQL test runs the exact
preflight and protected-boundary SQL used by the initializer against
Broker-shaped trigger functions and rejected privilege cases. It checks that
the safe functions' ACLs stay unchanged, restricted runtime calls and trigger
creation fail, and a classification change invalidates the protected boundary.
The same fixture runs the complete production post-create sequence as a
non-superuser CREATEROLE operator. It preserves the real membership grantor OID,
checks the exact single-row ADMIN/INHERIT/SET state after the membership GRANT
and cleanup, and rejects each unsafe reserved flag before commit.
These tests do not execute the initializer, connect to Azure, or prove live
database state. The catalog test requires a Docker-compatible container engine.
Do not retry or reset partially committed database state.
Any database execution and reconciliation require separate approval.

Run the source-only checks without starting a database:

```powershell
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresInitializerTests
node --test scripts\azure\tests\p1-postgres-plan.test.mjs
```

Run the production catalog SQL checks against a disposable PostgreSQL container:

```powershell
dotnet test tests\Agentweaver.FoundationProbe.Tests --filter FullyQualifiedName~P1PostgresInitializerPreflightPostgresTests
```

## System pool autoscaling

The full-foundation template enables autoscaling on the `system` pool with
explicit `nodePoolMinCount=2` and `nodePoolMaxCount=3` parameters. The VM size
remains `Standard_D2s_v5`; the initial foundation starts with two nodes.

The AKS-only path validates the same bounds against the approved full-foundation
parameters, but it does not read, configure, or change the pool. It does not
submit a node count to what-if or deployment. Its receipt reports no submitted
count and does not prove scale-up or regional capacity.
The two-node baseline remains unchanged. Three nodes can increase node compute
cost by 50%, plus per-node service charges. Quota headroom does not prove regional capacity.

## ACNS security source setting

AKS opts into Advanced Container Networking Services (ACNS) with security
enabled and observability disabled, the security-only configuration needed for
fully qualified domain name (FQDN) egress filtering. Runtime Kubernetes must
be version 1.29 or later.
The template leaves `kubernetesVersion` empty so AKS selects its currently
supported default rather than pinning an older version.

ACNS has per-node/hour charges with a cluster-wide effect; security-only is not
documented as free. This source setting does not apply to a cluster or prove
FQDN enforcement. Applying it requires separate explicit approval of the exact
target and cost, a current regional quote, and a guarded, reviewed update. No
Azure operation is part of source validation.

## Application Routing source setting

AKS uses `Microsoft.ContainerService/managedClusters@2026-07-02-preview` for
its managed default domain. This API version is in preview.

The full-foundation template applies `appRoutingDnsZoneResourceIds` when it
creates AKS. The existing-cluster AKS-only template does not accept DNS-zone
inputs or PUT the managed-cluster resource. It reads the current
`defaultDomain.enabled` and `defaultDomain.domainName` values so a refresh of
workload-identity federation cannot remove AKS-generated routing state. When
custom DNS means AKS omits `defaultDomain`, the output uses `false` and `null`.

When `appRoutingDnsZoneResourceIds` is empty, the full-foundation template
requests the managed default domain. AKS returns its assigned `domainName`.
The template reads this value from the AKS output. It does not construct a hostname. The preview
describes an autogenerated domain with a signed TLS certificate. Offline
validation does not prove feature registration, regional support, or HTTPS
serving. Bicep reports BCP081 because its installed schema has no type metadata
for this preview API. The command still compiles the template.

When the array contains IDs, the full-foundation template requests custom DNS zones and turns
off the managed default domain. Use one to five existing public or private
application DNS zone IDs from the exact authorized subscription. Put all public
zones in one resource group. Put all private zones in one resource group. The
two resource groups can differ. The source guard validates the zone IDs. It
rejects malformed, duplicate, PrivateLink, cross-subscription, and ungrouped
zone IDs. The deployment passes the reviewed IDs to AKS; target preflight does
not read or inventory the referenced zones.

The AKS-generated Application Routing identity receives DNS Zone Contributor
on each public zone or Private DNS Zone Contributor on each private zone.
These role assignments use the exact zone scope and the identity object ID.
The existing vault grants Key Vault Certificate User to that same identity.
The Bicep template defines no DNS record or certificate resource.

The template configures the managed Gateway API installation with
`installation: 'Standard'` and the Application Routing Istio implementation
with `mode: 'Enabled'`. The Gateway class is `approuting-istio`.
The template keeps the default nginx controller type at `None`.
It contains no nginx controller, IngressClass, or Ingress resource.
It does not remove an existing controller.

The separate candidate manifests under `deploy/k8s/routing/identity-broker`
define a managed certificate, HTTPS Gateway, HTTPRoute, and BackendTLSPolicy.
They are not part of ordinary namespace bootstrap.
The managed certificate supplies a wildcard domain through its status.
The route uses `agentweaver` as its single-label prefix, not the zone root.
One HTTPS listener uses that wildcard domain; Broker and Portal HTTPRoutes
retain their exact hostnames and paths on the shared listener. This allows
HTTP/2 connection coalescing between the certificate-covered hosts without
creating a route for unknown hostnames. The public issuer must match the
hostname on the admitted Broker route.

The Gateway requires a namespace policy compatible with its managed pods.
AKS currently rejects a Gateway customization that sets a pod or container
security context. Its published
[customization allow list](https://learn.microsoft.com/azure/aks/istio-gateway-api#deployment-customization-allow-list-fields)
does not include these fields.
A managed pod without an explicit seccomp profile cannot satisfy the P0
namespace's `restricted` policy.
This incompatibility blocks routing acceptance.
The source does not authorize a policy downgrade or a change to managed
Deployments. A different namespace requires separate approval and scoped
route attachment, certificate references, and network rules.

The installer has a separate default-off `--bootstrap-identity-routing` option.
It requires explicit `--identity-gateway-namespace agentweaver-gateway`,
`--identity-gateway-security-policy <approved-policy>`, and
`--identity-upstream-client-id <public-client-guid>` inputs.
The policy must be `restricted` or `baseline`. Neither value is a default.
These flags do not constitute approval.
An existing namespace must have matching ownership and policy labels.
The installer refuses to change its policy.
The P0 namespace retains restricted enforcement, audit, and warning labels.

For a fresh product install, first run the certificate-only stage before
`release:deploy-bootstrap --execute`. It creates the approved Gateway
namespace when needed, applies the native managed certificates in both
namespaces, waits for them to become Available, and returns their managed
hostnames. It does not require or read the MCP Service and does not apply a
Gateway, route, policy, or Cilium ingress rule.

If a release-bootstrap attempt has already applied the root bundle, a retry
also accepts its single `web-application` HTTPRoute when no Gateway or
BackendTLSPolicy exists. The route must target Gateway `identity-broker` in
`agentweaver-gateway`, select its shared `https` listener, and match the managed
Portal host, `/` path, and Web Service backend. An explicit parent port must be
443. Extra routes and any other partial routing state fail closed.

```powershell
node scripts\azure\deploy.mjs `
  --resource-group agentweaver `
  --template infra\bicep\aks-redeploy.bicep `
  --parameters infra\bicep\parameters\<approved-aks-only-parameters>.json `
  --subscription <subscription-id> `
  --allowed-subscription <subscription-id> `
  --tenant <tenant-id> `
  --allowed-tenant <tenant-id> `
  --identity-gateway-namespace agentweaver-gateway `
  --identity-gateway-security-policy <approved-policy> `
  --identity-upstream-client-id <public-client-guid> `
  --bootstrap-identity-routing `
  --identity-routing-certificates-only `
  --execute
```

This command uses the guarded AKS-only deployment path and requires the same
source, target, and permission checks as other Azure writes. The certificate
stage is a separate, default-off option. It requires
`--bootstrap-identity-routing` and cannot be combined with Broker readiness
verification. Explicit callback registration can be combined with this stage
when the exact route-derived URI is confirmed.

After the release bootstrap creates the product Services, run the same
guarded command again without `--identity-routing-certificates-only`. The full
phase first verifies that the MCP Service is the exact private HTTPS backend,
then applies the Gateway, Broker/Web/Gateway API/MCP HTTPRoutes, corresponding
BackendTLSPolicies, and narrow Cilium ingress policies. All routes attach to
the shared wildcard-domain `https` listener, which sends `/` to Web, `/api/v1`
plus exact `/openapi/v1.json` to Gateway, and exact
`/mcp` plus `/.well-known/oauth-protected-resource/mcp` to MCP over HTTPS.
The full phase rejects a missing or changed MCP Service before its first
routing write.
Gateway and MCP backend TLS use operator-owned
`gateway-tls` and `mcp-tls` Secrets, validated against the public `ca.crt` in
the existing `service-backend-ca` ConfigMap. Their leaf certificates must
match the exact in-cluster service DNS names. The installer reads back the
native routes and BackendTLSPolicies; it does not create or rotate these
service certificates. It adds no mesh addon, Istio API resource, injection label,
custom DNS zone, role assignment, application registration, or signing key.
It waits for the Gateway and route conditions with bounded commands.
The installer reads the public issuer from the admitted route hostname.
Current-generation conditions and exact certificate, listener, parent,
controller, backend, and namespace references must match.

Before an identity routing or Broker runtime deployment, the installer checks
the enabled Azure account, exact AKS cluster, managed Application Routing
domain, and existing Entra application. It derives
`https://agentweaver.<managed-domain>/signin-oidc` from the AKS domain before
the what-if or any infrastructure write. This also works before the first
certificate or route exists. Create the platform and its managed domain first;
the installer does not infer a callback for a missing cluster or custom domain.

The existing application must have the exact client ID in the selected tenant,
`AzureADMyOrg` single-tenant mode, public-client flow enabled, and the exact
route-derived callback in `publicClient.redirectUris`. The Broker requests only
the OpenID Connect scopes `openid`, `profile`, and `email`. It does not need an
Agentweaver API permission or consent grant. The tooling never creates an app
registration. A read-only plan makes no Azure or application changes. On
execution, preflight reports missing settings and stops before infrastructure
changes.

After separate approval, use `--register-identity-broker-callback` with
`--confirm-identity-broker-callback <exact-route-callback-uri>` and
`--execute`. The confirmation must match the URI derived from AKS. This explicit
option can be used with the certificate-only first-install stage or full routing.
The installer rechecks the selected tenant, subscription, client ID, and
application object ID at the write boundary. Its PATCH changes only
`signInAudience`, `isFallbackPublicClient`, and `publicClient.redirectUris`.
It appends the callback without removing existing public redirects and leaves
Web and SPA redirects, roles, scopes, API permissions, and consent grants
unchanged. It reads the application again to verify the settings. Native
permission errors remain explicit and redacted.
It never grants permissions, creates an application, or creates a client secret.

A routing receipt sets `routingConfigured: true` and `runtimeVerified: false`.
It is not runtime acceptance.
After the Broker starts with the approved issuer and required runtime inputs,
`--verify-identity-broker-readiness` also checks `/health/live` and `/health/ready`.
Both requests use ordinary DNS, trusted HTTPS, and the Gateway's observed IP.
It then opens HTTP/2 sessions to the observed Gateway IP in both directions,
using Broker SNI with Portal authority and Portal SNI with Broker authority;
each response must negotiate `h2` and return `200`. This verifies the route
behavior when a browser coalesces the certificate-covered hosts. No insecure
TLS, DNS override, or redirect following is permitted.
Failure prevents a successful installer receipt.
The routing option does not create the Broker's runtime ConfigMap, signing
Secret, key-ring PVC, OAuth registrations, or runtime Deployment.
Its receipt supplies `runtimeConfigInputs.IdentityBroker__Issuer` and
`runtimeConfigInputs.IdentityBroker__ExternalProvider__ClientId` for the approved
runtime ConfigMap. These inputs do not include a client secret.
The receipt explicitly reports `runtimeConfigMutationExecuted: false`.

### Initial Broker state and acceptance configuration

The separate `--bootstrap-identity-broker-state` option creates initial
`identity-broker-signing` material and the retained `identity-broker-key-ring`
PVC only when absent. It requires the .NET 10 SDK on the installer host.
The installer generates an RSA-3072 application certificate and a random PFX
password. This certificate signs tokens and protects the durable key ring.
It is not the public HTTPS certificate.
Private material passes from process memory to Kubernetes stdin.
It never appears in arguments, files, logs, or receipts.

Existing signing material and storage remain unchanged.
An incomplete Secret blocks installation without replacement.
A PVC without its original signing Secret requires recovery of that Secret.
The PVC remains application state, not a disposable acceptance fixture.
An existing compatible Secret with no PVC permits creation of the missing PVC.
This operation never rotates credentials or deletes state.

The default-off `--align-existing-identity-broker-hostname` option changes only
hostname fields on the existing owned P0 Gateway, HTTPRoute, and BackendTLSPolicy.
The hostname comes from the available native DefaultDomainCertificate.
UID and resource-version tests prevent overwriting a concurrent change.
Certificate references, namespaces, route attachment, backend ports, policies,
and permissions remain unchanged.
An unprogrammed Gateway remains `routingBlocked: true`.
This option does not authorize a namespace-policy change.

The separate `--bootstrap-identity-broker-runtime` option requires these
explicit inputs:

| Input | Purpose |
| --- | --- |
| `--identity-upstream-client-id` | Existing public Entra application with the exact native callback already registered. |
| `--identity-broker-image` | Approved immutable Broker image in GHCR. |
| `--identity-acceptance-run-id` | UUID that names the run-owned public client. |
| `--identity-acceptance-redirect-uri` | Explicit `http://127.0.0.1:<port>/callback` or IPv6 loopback callback. |

The installer derives the runtime identity and federation from native Azure
resources. It creates an immutable nonsecret ConfigMap for the explicit public
acceptance client. Its scopes match the admitted Broker test contract.
It creates missing ServiceAccount, Service, egress policy, and Deployment
objects from existing source manifests. Existing objects must match the
source configuration and remain unchanged.
It creates no Entra application, client secret, or permission grant.

Before runtime installation, this option invokes the guarded Identity PostgreSQL
bootstrap with the exact native runtime and migration principals.
After migration, the bootstrap applies the existing canonical grants to nine
explicit tables and grants SELECT-only access to the migration history.
It rejects missing tables, a different table owner, or unexpected privileges.
Without migration history, it reports pending grants and does not apply them.
Read-only bootstrap verification never applies grants.
The Broker container build copies only the canonical runtime-grants SQL file
into the build stage for this embedded resource.
The runtime phase invokes the bootstrap again even when initial PostgreSQL
setup already produced a receipt.
It never substitutes an earlier pending-grants receipt for the runtime check.

Deployment comparison ignores the deprecated Pod `serviceAccount` alias only
when it equals the explicit, nonempty `serviceAccountName`.
A different alias or a missing explicit name remains a configuration mismatch.

The runtime issuer comes from the independently observed owned route spec.
This is intended configuration even when the Gateway is not programmed.
The receipt reports `configurationOnly: true` and `runtimeVerified: false`.
Public DNS, trusted TLS, health, and user authorization require separate
actual observations. The existing public readiness checks remain unchanged.

### Foundation Probe installation inputs

Directory application reads use `az ad app show --id <exact-client-id>`.
This command does not accept `--subscription`.
Routing verifies the selected native account and tenant before this read.
ARM and Kubernetes operations retain their explicit target bindings.

The default-off `--bootstrap-foundation-probe-inputs` option verifies the
existing Probe identity and exact federation record. It creates a missing
ServiceAccount from the admitted manifest.
It reads the existing Application Insights component through the Azure
management API and creates the missing `foundation-probe-monitor` Secret.
The service-owned connection string stays in process memory and Kubernetes
stdin. Receipts contain only resource metadata.
An existing different Secret or ServiceAccount blocks replacement.
The installer also admits exact Logs query DNS names in the Probe egress policy.
It reads the owned AMPLS endpoint, reciprocal NIC, API member, and private DNS record.
All must identify the same private address in the dedicated subnet.
Only that observed `/32` address receives TCP port 443 access.
Logs queries have no public-address or wildcard FQDN allowance.
The installer creates a missing policy or replaces the exact prior five-name policy.
Replacement retains its UID and uses its resource version to reject concurrent changes.
A different policy blocks replacement. The installer checks the resulting policy.
This option does not create a vault writer role, resolve a latest secret
version, bootstrap PostgreSQL, or claim that a Probe Job succeeded.

The separate default-off `--bootstrap-foundation-probe-postgres` option creates
the Probe fixture in the existing `agentweaver` database.
It verifies the exact native Probe principal before opening the existing
run-owned PostgreSQL transport.
The operator uses Entra authentication through loopback with `VerifyFull`.
The Probe runtime role has access only to its `foundation_probe` fixture.
It has no schema creation rights or access to `identity_broker`.
Principal registration and mapping verification use the `postgres` database.
Schema creation and fixture migrations use one transaction in `agentweaver`.
The two database phases are not atomic together.
After a fixture failure, a retry can reuse only the exact non-admin service
principal with the expected role attributes.
A schema without its role, a different principal, or unsafe privileges blocks
the operation.
The receipt reports fixture setup, not a successful Probe run.
Temporary transport resources are removed by exact name and ownership.

The AKS `azureKeyvaultSecretsProvider` addon is enabled. Secret rotation uses
`enableSecretRotation: 'true'` and `rotationPollInterval: '2m'`. Both values
are strings. No live addon activation, deployment, DNS record, or certificate
creation occurred.

## Future approved target

CAUTION: Do not run a plan, deployment, bootstrap, or write diagnostic without
separate explicit approval for its dedicated Azure target and effects.
Strings on the allow list and `--execute` are technical guards, not approval.

The resource group must exist with matching ownership tags after approved bootstrap.
The preflight checks the selected enabled account, tenant, dedicated resource
group ID and name, ownership tags, and configured Key Vault name. Every Azure
command explicitly binds the selected subscription. It does not enumerate
resource groups or compare existing resource properties, federated credentials,
role assignments, Private Endpoints, or Private DNS links.

The reviewed JSON parameters bind the existing PostgreSQL Entra administrator
object ID, name, and supported type (`User`, `Group`, or `ServicePrincipal`);
they do not require a new group. The P0 example keeps `location` for VNet/AKS/
PostgreSQL and subnet-bound resources separate from required
`monitorLocation` for Log Analytics and Application Insights.
The selected deployment's source SHA, Git tree, and infrastructure hash are
checked against its exact successful receipt. Older deployment-history entries
do not have to be repacked or contain retrofitted outputs.

The source must be a clean full HEAD commit equal to the locally fetched
admitted `origin/v1` tip. Unreviewed descendants fail closed.
Only the tracked `main.bicep` and `aks-redeploy.bicep` templates and their
dedicated tracked JSON parameter files under `infra/bicep/parameters` are
supported. Use `main.bicep` for a full-foundation deployment. Use the AKS-only
template to update AKS without redeploying the VNet, PostgreSQL, Key Vault,
Blob, private DNS, or Monitor resources.
The checked-in examples contain placeholders and cannot run.
The selected JSON parameters must bind prefix, tenant, owner, and cost center.
The input hash covers all tracked infrastructure files.
`sourceTree` is the 40-character Git tree SHA for the source commit.
`sourceHash` is the separate SHA-256 infrastructure input hash.
For acceptance, `--expected-sha` must identify the deployed source commit. The
deployed commit may be an ancestor of the clean verifier HEAD. It must exist in
that reviewed history, and every tracked `infra/bicep` input must match its contents;
the deployment name, tree, and input hash are then checked against that exact
source. The report keeps `candidate.verifierSha` separate from the deployed
`candidate.sourceSha`, so a docs-only verifier advance does not rewrite the
provenance of an already deployed artifact.

The guarded deployment does not grant human AKS access or accept an operator
object ID. Service-identity permissions and the existing subnet Network
Contributor assignment remain scoped to their required resources.

The AKS-only template reads the issuer URL and managed routing-domain state
from the deployed cluster without PUTting AKS. It updates the three P0
workload-identity federated credentials and preserves
their ServiceAccount subjects and `api://AzureADTokenExchange` audience. Its
source-bound receipt covers only the federation refresh and is not a full-foundation
receipt for the Foundation Probe acceptance collector.

After separate approval, the operator command shape is:

```powershell
node scripts\azure\deploy.mjs --resource-group agentweaver --parameters infra\bicep\parameters\approved.json --subscription <id> --allowed-subscription <id> --tenant <id> --allowed-tenant <id>
```

The default template is `infra\bicep\main.bicep` for a full-foundation
deployment. To refresh workload-identity federation in an existing P0 foundation, select the
dedicated template and parameter file explicitly:

```powershell
node scripts\azure\deploy.mjs --resource-group agentweaver --template infra\bicep\aks-redeploy.bicep --parameters infra\bicep\parameters\p0-aks-redeploy.approved.json --subscription <id> --allowed-subscription <id> --tenant <id> --allowed-tenant <id>
```

Without `--execute`, this command produces an offline infrastructure summary.
It makes no Azure call.
The target preflight does not inspect resource inventory or guarantee that a
full-foundation deployment preserves existing resources. Select the template
that matches the intended change and review its what-if output.
For either template, `plan.mjs` accepts the same selection and target
arguments for a guarded live read-only what-if. `deploy.mjs --execute` performs
the guarded what-if before Incremental create. The source guard requires a
clean HEAD equal to the fetched admitted `origin/v1` tip; deployment does not
accept a caller-selected `--expected-sha`.
For the AKS-only template, the top-level what-if does not show resources inside
the nested workload-identity federation deployment. The preflight lists the
three federated identity credentials that the nested deployment will create or
update. It does not query existing credentials to distinguish creates from
updates.
Bicep source parameters and outputs bind the receipt to exact reviewed inputs.
No unsupported deployment tags occur.

The guarded infrastructure deployment does not create Kubernetes namespaces or
grant human AKS permissions. Namespace preparation is a separate, explicitly
invoked operation and requires authorization already provided outside this
deployment flow. The infrastructure deployer never falls back to an admin
kubeconfig or applies Kubernetes manifests.

Initial Identity PostgreSQL setup has a separate default-off
`--bootstrap-identity-postgres` option. It is available after either an
approved full foundation deployment or an AKS-only redeployment to the exact
existing P0 target.
The bootstrap uses the reviewed PostgreSQL host and administrator inputs and
reads the two Identity Broker principal IDs required by database setup. It
does not re-read PostgreSQL server or Entra administrator properties. After
namespace setup, it starts a temporary TCP-only proxy pod and a loopback-only
`kubectl port-forward`. The internal proxy is part of the solution build and
publishes with locked dependency restore. Its pod runs on the pinned public
ASP.NET 10 base image, not a historical Broker product image.
The operator host runs the Broker's guarded bootstrap
command with the exact PostgreSQL FQDN, runtime and migration principal IDs,
and approved Entra administrator. The Azure CLI Entra token stays on the
operator host; Npgsql uses `VerifyFull` with the PostgreSQL FQDN as TLS target
host. The installer removes only its exact run-owned pod and ConfigMap, stops
its port-forward process, and deletes its temporary local files. It does not
create a public route, app registration, secret, or Azure role assignment.
Ordinary `--execute` does not run this initializer. A failed bootstrap or
transport cleanup is reported and is not converted into a successful receipt.

### P1 PostgreSQL operator initialization

`bootstrap-p1-postgres.mjs` initializes the `agentweaver` database on the
existing PostgreSQL server after the five P1 runtime identities and the
Identity Broker identities exist.
Without `--execute`, it prints the source-bound target and role plan without
calling Azure. Execution requires the exact reviewed Core merge SHA:

```powershell
node scripts\azure\bootstrap-p1-postgres.mjs `
  --core-merge-sha <reviewed-core-merge-sha>
node scripts\azure\bootstrap-p1-postgres.mjs `
  --core-merge-sha <reviewed-core-merge-sha> `
  --execute
```

The command requires a clean checkout whose `HEAD` and fetched `origin/v1`
match the reviewed source SHA. Before opening a tunnel, it checks the selected
account and dedicated resource group, validates the approved PostgreSQL Entra
administrator inputs, and reads the two Identity Broker principal IDs required
by database bootstrap. It does not re-read PostgreSQL server or administrator
metadata or refresh the five P1 plan IDs from Azure. It then runs the Identity
Broker bootstrap and P1 initializer through one temporary loopback-only
PostgreSQL tunnel. The tunnel remains open through both database stages, and
the owned Pod, ConfigMap, port-forward, isolated kubeconfig, proxy files, and
temporary files are removed by the operator script's cleanup path. The P1
receipt remains under the source checkout's `Temp` directory for audit.

This command initializes PostgreSQL only. It does not deploy Bicep or product
workloads and creates no Azure role assignment or other Azure access grant.
It requires the operator's existing AKS access and PostgreSQL Entra
administrator authority; stop if either permission is unavailable rather than
creating a grant. The P1 receipt records operator initialization only, not
runtime identity or deployment proof. The initializer reads back its planned
roles and grants before commit; perform a separate read-only catalog check
before treating the setup as ready.

For the plan and migration requirements used by this command, see
[P1 PostgreSQL initialization and migration plan](#p1-postgresql-initialization-and-migration-plan).

The TCP proxy accepts `--key-vault <host>` only when the host exactly matches
the URI configured by `IdentityBroker__SecretRedemption__VaultUri`. The URI
comes from the reviewed Bicep `keyVaultName` parameter; the proxy does not
contain a compiled-in vault name. It relays TCP from port 8443 to that host on
port 443.
The loopback forward requires port 15443 and the P0 namespace.
The PostgreSQL argument and port 5432 remain unchanged.
The proxy receives encrypted TLS bytes, not an operator credential.
The local SDK must keep the real vault URI, SNI, and system certificate validation.
This mode does not grant access or create, read, or delete a secret.
Fixture writes and soft deletion need separate approval and owned cleanup evidence.

## Acceptance

`verify-acceptance.mjs` accepts the selected infrastructure source/target arguments,
plus `--expected-sha` and `--deployment-name`. It checks the exact source-bound
deployment, target ownership, observed AKS security settings, and exact
workload-identity federation configuration. AKS-only collection also requires
`--foundation-expected-sha` and the exact original full-foundation receipt.
The current selected AKS issuer can differ from the historical foundation issuer.
These configuration checks do not prove that a Job ran or exchanged a token.

Successful deployment outputs include `foundationProbeIdentity` and `foundationResources`.
The tooling checks their exact dedicated resource IDs, endpoints, namespace, ServiceAccount, and workspace GUID.
The named identity contains distinct `clientId` and `principalObjectId` fields.
Parallel identity arrays do not select the probe principal.
The Bicep outputs also name the separate `identityBrokerRuntimeIdentity` and
`identityBrokerMigrationIdentity`. The source guard checks their exact resource
IDs, namespace, ServiceAccount names, and distinct client/principal IDs. Only
the runtime identity receives Key Vault Secrets User on the exact vault; the
migration identity receives no Azure resource role. These outputs do not
provision PostgreSQL principals, apply schema grants, alter the strict
`foundationProbeIdentity` DTO, or prove a deployed workload.
The workspace GUID is not its ARM resource ID.

The optional Foundation Probe evidence collector can be run with the explicit
read-only collection option:

```powershell
node scripts\azure\verify-acceptance.mjs --resource-group agentweaver --parameters infra\bicep\parameters\approved.json --subscription <id> --allowed-subscription <id> --tenant <id> --allowed-tenant <id> --expected-sha <40-hex-sha> --deployment-name agentweaver-<first-12-sha> --collect-runtime-evidence --kube-context <approved-context> --image-reference <registry>/<repository>:<tag> [--image-receipt artifacts\images\foundation-probe.json]
```

The image reference is a tag, not a digest. The receipt defaults to the
source-bound image receipt downloaded from the CI artifact. The existing image
verifier pulls and inspects that registry image. It checks the local image
config digest and source labels, then derives the registry manifest digest from
Docker's repository digests. The Job's immutable image reference and the pod's
reported pulled image ID must both match that manifest digest. A local config
digest is never used as a registry manifest digest.

The collector uses only fixed read-only Kubernetes operations. It checks the
selected context's API server against the observed AKS FQDN. Then it reads the
named Job, its pod, the ServiceAccount, the immutable target ConfigMap, and the
completed pod's logs. It does not list unrelated resources, run commands in a
pod, create resources, or delete resources. The Job must have one completion,
no retries, and a 360-to-420-second deadline. The observed pod must belong to
that Job by UID, run in the exact namespace with the expected ServiceAccount,
have the Azure workload-identity projection, and terminate with exit code 0.

The immutable target ConfigMap must contain the strict runtime `ProbeTarget` for
the exact reviewed SHA, Git tree, infrastructure hash, deployment, identities,
resources, database target, and read-only Key Vault secret name/version.
The native probe receipt must preserve the exact federation issuer, subject,
and audience, plus the provider pins. It must record successful exact-version
Key Vault redemption and owned PostgreSQL effect/inbox/outbox results. It must
also record the owned Blob generation, ETag, content hash, and confirmed
conditional cleanup. The receipt cannot attest to
the pod UID, pulled image, or process exit code.

Only after the Job is independently observed complete does the consumer query
the exact workspace from the deployment output. The fixed Logs query searches
`AppDependencies` and `AppRequests`, not `AppTraces`, and matches the probe
trace/span with `probe.source_sha`, `probe.source_tree`, and the 32-character
nonce. A fresh matching row is required; exporter flush alone is not proof of
stored telemetry. The Probe host uses `AlwaysOnSampler` for its acceptance span.
An unrecorded span fails with `telemetry_activity_not_recorded` before fixture
operations. This setting does not change sampling in other Azure Monitor hosts.
Caller-supplied digests, booleans, trace IDs, and secret
versions do not satisfy the gate. Failed terminal pods retain only probe failure
codes and types on the allow list; raw logs and credential-shaped diagnostics are
not copied into the report.

This optional collector reports configuration and probe evidence separately.
The report passes only when every required check passes; otherwise the command
exits 1. This source-only consumer was validated with fake transports and local
fixtures. It does not perform live Azure, Kubernetes, or registry reads during
source validation, and it does not deploy, publish, or close the full P0
shipping checklist. It is supplemental, not the P0 shipping smoke-test gate.
The required practical checks are documented in [Azure acceptance](../../docs/guide/azure-acceptance.md).
The observed ACNS/Cilium settings are configuration evidence only; the consumer
does not claim that a Cilium policy is effectively enforced.

## Authentication prerequisites

AKS exposes a public API endpoint and uses managed Entra authentication and
Azure RBAC, with local accounts disabled. A public FQDN on a private API does
not satisfy this configuration. Provision operator connectivity and Kubernetes
API permissions outside the tooling. Cluster-dependent scripts check the
required permissions before they write resources and fail with an actionable
message when access is missing. Neither Bicep nor the scripts create, require,
or verify a human AKS Cluster Admin assignment; the tooling never grants a
person AKS Cluster Admin.

The probe identity has Log Analytics Reader on the exact workspace.
It has Monitoring Metrics Publisher on the exact Application Insights resource.
These roles support Logs queries and authenticated exporter ingestion.
They do not prove token exchange, private connectivity, role propagation, or complete probe permissions.
No role assignment occurred during offline validation.

Key Vault Secrets User remains read-only.
The probe requires a separately approved fixture with an exact secret name and version.
Probe secret creation, deletion, and purge are not permitted by this contract.
Purge protection remains enabled.
The executable runtime probe must use this read-only fixture before cloud acceptance.

The exported Blob diagnostic is operator-only and requires separate write approval.
It creates a local fixture under `artifacts\azure`, uses a generated owned blob,
and removes only that observed generation with its exact ETag in `finally`.
Primary and cleanup failures both remain visible.
The CLI does not enable this diagnostic.
