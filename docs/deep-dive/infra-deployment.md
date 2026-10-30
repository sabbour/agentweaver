# Infrastructure and deployment

Use this page to understand the checked-in Azure and Kubernetes deployment
paths. Infrastructure provisioning, artifact publication, and runtime rollout
are separate operations. This page describes source and tooling; it does not
report a live rollout.

A successful build does not prove that a cluster deployment or end-to-end
product acceptance succeeded.

## Find deployment source

| Source | Responsibility |
| --- | --- |
| `infra/bicep/main.bicep` and `modules/` | Dedicated foundation resources |
| `infra/bicep/aks-redeploy.bicep` | Existing-cluster workload-identity federation refresh |
| `infra/bicep/p1-runtime-identities.bicep` | Separate runtime-identity setup |
| `infra/bicep/p1-session-material-access.bicep` | Focused private container and Events data-role repair |
| `deploy/k8s/base/`, `migrations/`, and `acceptance/` | Namespace, Broker runtime, migration Job, and Foundation Probe |
| `deploy/k8s/routing/identity-broker/` | Managed Gateway routing and backend TLS |
| `deploy/k8s/profiles/` | Separately selected runtime profiles |
| `scripts/azure/` | Exact-source infrastructure planning and deployment |
| `scripts/release/` | Component version planning, Kustomize image helpers, and shared registry reads |
| `.github/workflows/v1-integration-images.yml` | Direct GHCR image builds for v1 service changes |
| `.github/workflows/v1-release-pack.yml` | NuGet package publication after component version changes |
| `.github/workflows/v1-deploy.yml` | Migration-first Kustomize deployment |

The Azure acceptance verifier is `scripts/azure/verify-acceptance.mjs`.

## Keep source and deployment boundaries

- The image workflow tags each direct GHCR push with its Git commit and
  reports the registry digest.
- The deployment command resolves those tags to digests, runs the existing
  migration Jobs, then renders and applies the root Kustomize overlay.
- Shared services and per-Sandbox AgentHost placement have separate lifecycles.
- Missing runtime prerequisites stop deployment. Rollout does not create new
  credentials or grant itself access.

The Broker migration Job is a separate write boundary: the integrated
deployment runs it before the root workload apply, applying migrations and
canonical explicit table grants as the migration owner. Initial bootstrap
success alone does not prove post-migration runtime access.
Read-only bootstrap verification checks the table grants without repairing
them. See [Broker database permissions](../reference/runtime-configuration#identity-broker-migration-history-and-grants).
Its separate Kustomize overlay resolves migration DNS and TCP 5432 egress to
the exact `agentweaver-pg.postgres.database.azure.com` host; the guarded root
release apply does not include that Job or policy.

The Orchestrator and Events and Sessions migrations run as Jobs from the
`migrations/p1-postgres/` overlay under the P1 migration identity. The
integrated `npm run deploy:kustomize` command runs Broker/P1 migrations, then
Projects & Config migration, before applying the root overlay. The underlying
The deployment command runs the P1/Broker runner with `--migrations-only`, so
it does not apply an early service rollout or enforce the legacy image-order
gate; the single root Kustomize apply updates workloads after all migration
Jobs succeed. The normal standalone `npm run db:maintenance` mode retains its
schema-ordering and readiness checks.
`npm run db:maintenance:setup` creates the identities, role assignments, and
GitHub environment that the command and its workflow need. Setup also applies
and verifies a narrow Kubernetes Role and RoleBinding for the workflow identity;
it creates no custom Azure role. See
[Database maintenance](../guide/database-maintenance.md).

The Events & Sessions migration Job includes every Sessions and Messaging option
from its runtime map because the host validates them before it enters
`--migrate`. The Orchestrator migration branch runs before normal provider
settings are loaded.

Orchestrator migration 032 adds Source Control pull request records. The
reviewed P1 database plan includes the migration and its exact runtime table
grant, and the maintenance Job applies and records it with the other planned
migrations. Startup accepts a complete pre-032 schema during rollout, so the
project pull request list returns `503 pull_request_records_unavailable` until
the Job applies the plan. See the
[Azure tooling notes](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md).

## Understand the Azure foundation

`main.bicep` is an Azure infrastructure-as-code template. It targets a
dedicated resource group and defines:

- A virtual network (VNet) with separate AKS node subnets, delegated
  PostgreSQL subnets, and private-endpoint subnets.
- AKS with managed Microsoft Entra access, OpenID Connect (OIDC) and workload
  identity, Azure role-based access control (RBAC), and
  Cilium networking.
- Microsoft Entra-only PostgreSQL Flexible Server.
- RBAC-based Key Vault and private Blob storage with private
  `platform-artifacts` and `session-material` containers.
- Log Analytics, Application Insights, and Azure Monitor Private Link Scope.
- Private endpoints, DNS links, workload identities, federation, and scoped
  role assignments.

Key Vault and Blob disable public network access. PostgreSQL uses the delegated
private network. Monitor disables public query access while allowing its
configured ingestion path. The template does not create a workspace Azure
Files share or install the Agent Sandbox controller.
The Foundation Probe has account-scoped Blob data access for its acceptance
round trip. The Events & Sessions identity receives Storage Blob Data
Contributor on `session-material` only. The focused repair template can add
that container and role assignment without redeploying the foundation; follow
the separate operator approval and target checks in the
[Azure operator guide](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#session-material-container-repair).

Reviewed parameters place core resources in `eastus2euap` and Azure Monitor
service resources in `eastus2`. The Monitor private endpoint stays in the VNet
region.
See [Azure architecture](../architecture/azure.md) for the resource and region
contract.

## Plan and deploy from an exact commit

Before planning or deploying, use a clean checkout with a full source commit
admitted by `origin/v1`, and select the dedicated target.

`scripts/azure/lib/git.mjs` resolves the checkout and validates the selected
source SHA. It compares committed infrastructure inputs and computes the
source tree and input hash. `plan.mjs` checks the target and ownership before
building an Azure what-if request. `deploy.mjs` defaults to a nonexecuting summary. Its write path requires
`--execute`, checks the selected account and dedicated target, runs what-if,
and rechecks source inputs before mutation. The target preflight does not
enumerate resources or verify their resulting properties.

The full-foundation path applies the full template. The AKS-only path reads the
existing cluster and refreshes its three workload-identity federation records
without PUTting AKS or redeploying other foundation resources. Source SHA, tree,
and input hash bind the deployment arguments and returned outputs.

Namespace bootstrap, database setup, Broker runtime/state, routing, and
acceptance inputs are separate operations; compiling Bicep does not imply them.
The acceptance verifier checks exact target, image, source, receipt, identity,
and fresh Monitor evidence after the Probe Job completes.

## Understand Kubernetes workload boundaries

The base contains namespace limits, default-deny networking, service accounts,
and the Broker Deployment and private HTTPS Service. The Broker migration Job
is separate from ordinary runtime startup.

Projects and Events runtime profiles require explicit reviewed inputs,
existing TLS and configuration references, immutable product images, and
prepared database permissions. Rendering does not create these prerequisites.

Routing uses the native `approuting-istio` Gateway class, explicit Gateway
namespace policy, a shared wildcard-domain HTTPS listener, exact-host HTTPRoutes,
managed certificates, and backend TLS policies. Public route definitions do
not replace trusted TLS, DNS, or owner health checks.

The shared runtime `List` contains nine service Deployment/Service pairs.
AgentHost is the tenth product image, but it runs per Sandbox instead of as a
shared Deployment.

## Build and publish

The v1 image workflow builds all ten images used by the root Kustomize overlay
when service code, Docker build inputs, or component versions change. AgentHost
and Identity Broker use their existing Dockerfiles and build contexts, Web
builds its frontend and host, and the other seven images use .NET container
publishing. Each image is pushed directly to GHCR with a `sha-<commit>` tag.
The workflow summary lists each registry digest and the AgentHost compressed
pull size.

The package workflow runs separately when `releases/foundation.json` changes.
It packs versioned contracts and libraries and pushes NuGet packages to GitHub
Packages. The native CLI archive workflow remains separate. These workflows
do not transfer image archives or require image publication receipts or
source-claim gates.

## Deploy the Kustomize overlay

Dispatch `.github/workflows/v1-deploy.yml` on `v1` with a `sha-<commit>` image
tag produced by the image workflow. It runs `npm run deploy:kustomize`, which
resolves ten immutable digests, runs Broker/P1 then Projects & Config
migrations, renders `deploy/k8s`, applies it to the selected context and
`agentweaver` namespace, and waits for all nine Deployments. Any failed
migration prevents the root overlay apply.

The workflow requires `AZURE_DB_MAINTENANCE_CLIENT_ID` in the
`v1-db-maintenance` GitHub environment and uses the existing GitHub OIDC
identity. Run `npm run db:maintenance:setup -- --v1-deploy-only --execute`
once to provision its federation, the approved namespace Role/RoleBinding, and
the Projects & Config migration Workload Identity ServiceAccount. The deploy
identity needs AKS Cluster User access and the namespace-scoped verbs listed
in the [release reference](../reference/releases.md). The deploy command does
not read Secrets or manage the Namespace.

Projects & Config migration uses a dedicated Kubernetes Workload Identity. The
`projects-config-migration` ServiceAccount uses the
`agentweaver-id-projects-config-migration` identity, federated with audience
`api://AzureADTokenExchange` and subject
`system:serviceaccount:agentweaver:projects-config-migration`. The deploy
command verifies its client-ID annotation before creating any migration Job.
Provision the matching PostgreSQL Entra principal with `CONNECT` and ownership
only of the `projects_config` schema and its exact planned objects; it must not
own the database, retain database `CREATE`, or access other schemas. The
one-time setup command does not change PostgreSQL ownership. No access token is
stored in the migration Job.

The Projects & Config migration applies EF migrations and then reconciles the
reviewed runtime-grant plan in the same migration operation. It verifies the
schema-owner identity, object inventory, and least-privilege grants
transactionally; a mismatch fails before rollout. Extend and review the plan
when new objects need runtime access; do not use blanket/default grants.

The workflow must exist on the repository's default branch to appear in the
manual dispatch UI. Azure provisioning, certificates, runtime configuration,
and Gateway routing remain separate prerequisites and are not changed by the
workload rollout.

## Related content

- [Azure architecture](../architecture/azure.md)
- [Release reference](../reference/releases.md)
- [Kubernetes source and prerequisites](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Azure operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
- [Services and release design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
- [Testing strategy](./testing-strategy.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
