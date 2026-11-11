# Infrastructure and deployment

## 1. Introduction and Goals

Use this page to understand the checked-in Azure and Kubernetes deployment
paths. Infrastructure provisioning, artifact publication, and runtime rollout
are separate operations. This page describes source and tooling; it does not
report a live rollout.

A successful build does not prove that a cluster deployment or end-to-end
product acceptance succeeded.

## 2. Constraints

Deployment requires an explicitly approved target, operator identity and exact
source/image selection. It does not bootstrap project roles, widen resource
permissions or provision missing credentials. Database migration authority
remains separate from runtime identities.

## 3. Context and Scope

Azure supplies infrastructure and workload identities; Kubernetes supplies
placement and network/resource APIs; GHCR and GitHub Packages hold distinct
artifact types. Operators authorize provisioning and rollout. A source
manifest describes intended composition, not the state of any one environment.

## 4. Solution Strategy

The source separates foundation provisioning, immutable image publication and
migration-first workload rollout. This keeps runtime identities narrow and
rollouts reproducible, but requires prepared identity, DNS, certificates and
database grants before workloads can become ready.

## 5. Building Block View

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
| `.github/workflows/v1-integration-images.yml` | Direct GHCR image builds for service changes |
| `.github/workflows/v1-release-pack.yml` | NuGet package publication after component version changes |
| `scripts/release/deploy-from-commit.mjs` | Exact-source intermediate acceptance deployment from a detached worktree |
| `scripts/release/deploy-kustomize.mjs` | Local migration-first Kustomize deployment |

The Azure acceptance verifier is `scripts/azure/verify-acceptance.mjs`.

### Keep source and deployment boundaries

- The image workflow tags each direct GHCR push with its Git commit and
  reports the registry digest.
- The local deployment command resolves those tags to digests, runs the
  existing migration Jobs, then renders and applies the root Kustomize overlay.
- `npm run azure:deploy-from-commit` checks all ten images against an ancestor of
  `origin/v1`, then delegates their immutable digests to the same deployment
  helper from a detached worktree at that source commit.
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
`migrations/p1-postgres/` overlay under the database migration identity. The local
`npm run deploy:kustomize` command prepares migration Jobs for the selected
service images and waits for them to succeed before applying the root
workload overlay. Separately, the database maintenance runner's
`--migrations-only` mode leaves application Deployments unchanged and skips
the image-order gate. The normal standalone `npm run db:maintenance` mode retains its
schema-ordering and readiness checks.
`npm run db:maintenance:setup` prepares the identities, role assignments, and
GitHub environment for the separate database-maintenance workflow. It also
applies and verifies that workflow identity's narrow Kubernetes Role and
RoleBinding; it creates no custom Azure role. See
[Database maintenance](../guide/database-maintenance.md).

The Events & Sessions migration Job includes every Sessions and Messaging option
from its runtime map because the host validates them before it enters
`--migrate`. The Orchestrator migration branch runs before normal provider
settings are loaded.

Orchestrator migration 032 adds Source Control pull request records. The
reviewed PostgreSQL grant plan includes the migration and its exact runtime table
grant, and the maintenance Job applies and records it with the other planned
migrations. Startup accepts a complete pre-032 schema during rollout, so the
project pull request list returns `503 pull_request_records_unavailable` until
the Job applies the plan. See the
[Azure tooling notes](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md).

### Understand the Azure foundation

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

## 6. Runtime View

### Plan and deploy from an exact commit

Before planning or deploying, use a clean checkout with a full source commit
admitted by `origin/v1`, and select the dedicated target.

`scripts/azure/lib/git.mjs` resolves the checkout and validates the selected
source SHA. It compares committed infrastructure inputs and computes the
source tree and input hash. `plan.mjs` checks the target and ownership before
building an Azure what-if request. `deploy.mjs` defaults to a nonexecuting summary. Its write path requires
`--execute`, checks the selected account and dedicated target, runs what-if,
and rechecks source inputs before mutation. The existing P0 target preflight does not
enumerate resources or verify their resulting properties.

The `--new-foundation` path instead requires an empty, already tagged group
with no deployment history. It verifies a separate AKS managed group and
compares two create-only what-if results. It retains the Monitor add-on step.
The [deployment guide](../guide/deploy-to-azure#use-a-new-separate-nonproduction-group)
states the single-operator and recovery limits.

The full-foundation path applies the full template. The AKS-only path reads the
existing cluster and refreshes its three workload-identity federation records
without PUTting AKS or redeploying other foundation resources. Source SHA, tree,
and input hash bind the deployment arguments and returned outputs.

Namespace bootstrap, database setup, Broker runtime/state, routing, and
acceptance inputs are separate operations; compiling Bicep does not imply them.
The acceptance verifier checks exact target, image, source, receipt, identity,
and fresh Monitor evidence after the Probe Job completes.

## 7. Deployment View

### Understand Kubernetes workload boundaries

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

AgentHost Sandbox pods can reach only the internal owners, the Identity Broker,
and DNS. The deploy helper adds the HTTPS host of each BYOK model binding in the
rendered AgentHost runtime map to the `agenthost-model-egress` policy. If the map
has a GitHub Copilot (`HostedCopilot`) binding, the helper also adds the exact
GitHub Copilot hosts. See
[Configure an Azure OpenAI BYOK model](../guide/configure-byok-azure-openai.md#deploy-the-change).

That source inventory includes the retained MCP compatibility workload and
ingress. It does not make MCP a supported product or acceptance surface.
Gateway/API, browser and native .NET CLI remain the supported client paths.

### Build and publish

The image workflow builds all ten images used by the root Kustomize overlay
when service code, Docker build inputs, or component versions change. AgentHost,
Identity Broker, and Orchestrator use Dockerfiles; Web builds its frontend and
host; the other six images use .NET container publishing. Each image is pushed
directly to GHCR with a `sha-<commit>` tag. The workflow summary lists each
registry digest and the AgentHost compressed pull size.
For an ACR candidate, `npm run azure:build-acr-candidate` uses those Docker
and .NET build paths from a detached exact-source worktree. It downloads the
pinned OPA binary when the source includes the ACS package and builds
Orchestrator with its Dockerfile. For an older commit without the package, the
builder skips the OPA download and uses .NET container publishing for
Orchestrator. It pushes the same ten `linux/amd64` images to ACR and verifies
the source commit, Git tree, component version, digest, and AgentHost compressed
pull size. This local command does not replace the automatic GHCR workflow.

### ACS runtime assets

The Orchestrator and AgentHost source builds include the Linux-x64
`AgentControlSpecification` package from a checked-in local feed. The root
`NuGet.Config` maps only this package to `eng/acs/nuget`; other packages restore
from nuget.org. Each Dockerfile restores and publishes with the `linux-x64`
runtime identifier, verifies the native library hash, and runs an image probe
before the build can finish.

Each image build downloads OPA `0.70.0` from the official OPA site, checks its
SHA-256, and places it at `/app/opa`. The image sets `ACS_OPA_PATH=/app/opa`.
The probe runs inside each final image. It loads the image's native library
and checks OPA allow, deny, and fail-closed behavior. It does not use a
fallback OPA path. For package source, build pins, and hashes, see the
[ACS runtime asset record](https://github.com/sabbour/agentweaver/blob/v1/eng/acs/README.md).

This source build does not activate ACS or change stored policy data. It does
not show a live deployment or policy authorization.

The package workflow runs separately when `releases/foundation.json` changes.
It packs versioned contracts and libraries and pushes NuGet packages to GitHub
Packages. The native CLI archive workflow remains separate. These workflows
do not transfer image archives or require image publication receipts or
source-claim gates.

### Migration-first workload rollout

The workload command runs locally from an approved operator machine with an
interactive Azure CLI identity and the intended Kubernetes context. It selects
image repositories using the operator-supplied JSON manifest documented in
the [Kustomize guide](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md#select-images-with-a-manifest);
GHCR is the default registry. `--registry` selects another supported registry
for images in the manifest; omitted repositories retain their live image
references and registries. ACR OCI reads exchange an Azure CLI login token for
a pull-scoped token in memory. The helper does not print or store that token.
The legacy
`--image-tag` form updates all ten images. Use the
[release runbook](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
for the approved operator procedure; this view explains its boundaries rather
than duplicate login, target selection and mutation steps.
An omitted AgentHost image must already be pinned to the digest in its retained
runtime tuple. A tag or mismatched digest stops deployment before writes;
select AgentHost explicitly to update it.

#### First-install Broker dependency order

The Kustomize helper requires the exact immutable
`identity-broker-runtime-config-v3` ConfigMap before its first cluster write.
It does not create the map. The full Broker runtime bootstrap waits for aligned
routes, while full routing requires the private MCP Service that Kustomize
creates. This creates a first-install dependency loop.

Break the loop with this order: certificate-only routing, the ConfigMap-only
stage, Kustomize deployment, then full identity routing after the Services
exist. The ConfigMap-only stage checks the target-rendered source, target
identity and federation, both managed certificates, the existing callback,
and create permission. It creates only the immutable ConfigMap. It does not
create a Service, route, or policy. Do not run full Broker runtime bootstrap
after Kustomize in this sequence; its source policy hash does not match the
policy that Kustomize renders. See the
[operator procedure](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md#bootstrap-the-first-install-broker-configmap).

The command resolves selected image references to immutable digests, runs
only the selected services' migrations with runtime-grant reconciliation,
renders `deploy/k8s`, applies the namespaced resources to the selected context,
and waits for all nine Deployments. Any failed migration prevents the root
overlay apply. The local operator identity needs the required AKS and namespace
permissions; this command neither provisions Azure resources nor grants
access.

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
The separate first-time operator initializer also uses this dedicated owner:
it preflights SET-role access before creating runtime roles, then creates the
Projects schema, migrations, and reviewed grants in one transaction.

Azure provisioning, certificates, runtime configuration, and Gateway routing
remain separate prerequisites and are not changed by the workload rollout.

## 8. Crosscutting Concepts

Workload identity authenticates a process to its backend; Projects roles and
owner grants authorize product actions. Neither replaces the other.
TLS trust, fixed owner roots, default-deny policies and per-resource scopes
constrain transport and resource access. Database grants remain explicit:
startup checks do not repair them.

Images, NuGet packages, native CLI archives and site output have different
publication paths. A package version or image digest does not prove a running
workload, complete acceptance or CLI/API compatibility.

## 9. Architecture Decisions

The [platform ADR](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0001-platform-architecture.md)
remains Proposed. [ADR 0002](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/decisions/0002-project-issue-sync-delegation.md)
records accepted/source-implemented delegation, not deployed issue sync.
The [services and release design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
provides additional design context, not a second operational runbook.

## 10. Quality Requirements

| Context and stimulus | Affected block and response | Measure | Verification state |
| --- | --- | --- | --- |
| A selected migration fails | Deployment helper stops before root overlay apply | No workload apply after failed prerequisite Job | Controlled deployment-tool tests |
| Runtime grant inventory differs from the reviewed plan | Migration owner rejects transactional reconciliation | No blanket/default grant substitution | Grant/migration tests; no live permission claim |
| Operator omits an image repository from the manifest | Deployment helper preserves its live image reference | Only selected image changes | Manifest/deployment-helper tests |

These are source contracts, not rollout-time or availability SLOs.

## 11. Risks and Technical Debt

Wrong target context can mutate the wrong workload. Operators must follow
exact-source planning and verify the selected context rather than infer it
from a successful render. A stopped rollout can leave migrations applied
without updated workloads; schema compatibility and rollout recovery remain
owner responsibilities.

Source checks cannot establish private DNS, TLS handshakes, CSI flush, network
datapath behavior or paid model execution. Those require separately approved
environment-specific observations.

## 12. Glossary

- **Provisioning:** create or configure infrastructure resources.
- **Publication:** make a package/image/archive available in its registry.
- **Deployment:** apply software/configuration to an execution environment.
- **Acceptance:** verify the required behavior against exact source, identity
  and resource evidence; narrower health or read checks are not equivalent.

## Related content

- [Azure architecture](../architecture/azure.md)
- [Release reference](../reference/releases.md)
- [Kubernetes source and prerequisites](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Azure operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
- [Services and release design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
- [Testing strategy](./testing-strategy.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
