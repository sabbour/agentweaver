# Infrastructure and deployment

Use this page to understand the checked-in Azure and Kubernetes deployment
paths. Infrastructure provisioning, artifact publication, and runtime rollout
are separate operations. This page describes source and tooling; it does not
report a live rollout.

A successful build is not a deployment receipt, and a deployment receipt is
not end-to-end product acceptance.

## Find deployment source

| Source | Responsibility |
| --- | --- |
| `infra/bicep/main.bicep` and `modules/` | Dedicated foundation resources |
| `infra/bicep/aks-redeploy.bicep` | Bounded AKS replacement path |
| `infra/bicep/p1-runtime-identities.bicep` | Separate runtime-identity setup |
| `deploy/k8s/base/`, `migrations/`, and `acceptance/` | Namespace, Broker runtime, migration Job, and Foundation Probe |
| `deploy/k8s/routing/identity-broker/` | Managed Gateway routing and backend TLS |
| `deploy/k8s/profiles/` | Separately selected runtime profiles |
| `scripts/azure/` | Exact-source infrastructure planning and deployment |
| `scripts/release/` | Artifact preparation, publication, provenance, and rollout |
| `.github/workflows/v1-integration-images.yml` and `v1-release-pack.yml` | Manually dispatched artifact workflows |

The Azure acceptance verifier is
`scripts/azure/verify-acceptance.mjs`. Release deployment uses
`scripts/release/deploy.mjs`, `deploy-integration.mjs`, and `runtime-list.mjs`.

## Keep source and deployment boundaries

- Source identity includes the exact commit and tree, not only a branch or
  mutable image tag.
- Runtime rollout consumes verified published images; it does not build
  replacements during deployment.
- Shared services and per-Sandbox AgentHost placement have separate lifecycles.
- Missing runtime prerequisites stop deployment. Rollout does not create new
  credentials or grant itself access.

## Understand the Azure foundation

`main.bicep` is an Azure infrastructure-as-code template. It targets a
dedicated resource group and defines:

- A virtual network (VNet) with separate AKS node subnets, delegated
  PostgreSQL subnets, and private-endpoint subnets.
- AKS with managed Microsoft Entra access, OpenID Connect (OIDC) and workload
  identity, Azure role-based access control (RBAC), and
  Cilium networking.
- Microsoft Entra-only PostgreSQL Flexible Server.
- RBAC-based Key Vault and private Blob storage for platform artifacts.
- Log Analytics, Application Insights, and Azure Monitor Private Link Scope.
- Private endpoints, DNS links, workload identities, federation, and scoped
  role assignments.

Key Vault and Blob disable public network access. PostgreSQL uses the delegated
private network. Monitor disables public query access while allowing its
configured ingestion path. The template does not create a workspace Azure
Files share or install the Agent Sandbox controller.

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

The full foundation path applies the full template; the AKS-only path updates
AKS without redeploying the foundation resources. The AKS-only path reads the
system node-pool count used as a deployment input. Source SHA, tree, and input
hash bind the deployment arguments and returned outputs.

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
namespace policy, managed certificates, and backend TLS policies. Public route
definitions do not replace trusted TLS, DNS, or owner health checks.

The shared runtime `List` contains nine service Deployment/Service pairs.
AgentHost is the tenth product image, but it runs per Sandbox instead of as a
shared Deployment.

## Prepare and publish images

The integration workflow takes an exact `v1` source SHA, prepares ten
source-tagged service images, and separates preparation from approved
publication. Its images-only receipt retains the draft manifest and source
tree. `deploy-integration.mjs` verifies that receipt and registry provenance
before rendering or applying the composition.

The release workflow prepares independently versioned packages and images
from exact source. Publication is a separate confirmed operation. Deployment
consumes an already-published `v1.x.y` release, its manifest, and publication
receipt.

## Deploy a published release

Before deployment, provide a clean product checkout, a published release with
its source-bound manifest and receipt, and an Azure/Kubernetes target with
required runtime configuration and TLS already present.

1. Verify the checkout, release source, manifest, and publication identity.
2. Verify digest-pinned GitHub Container Registry (GHCR) images through the
   registry API, including the measured AgentHost image and compressed pull
   size.
3. Render the checked-in runtime `List`, or validate an explicit runtime
   manifest.
4. Verify the Azure target, Kubernetes context, runtime configuration, TLS,
   identities, and policies.
5. Run client-side dry-run. Without `--execute`, stop before applying.
6. Apply validated workloads and save an apply checkpoint.
7. Recheck source, images, and target; wait for rollouts; verify ready Pods and
   pulled image IDs.

The tool does not create missing credentials, identities, routes, namespaces,
migrations, or databases. Partial publication requires a verified completion
proof; it is not accepted as a complete release. The one-use `v1.0.0`
continuation is consumed after publishing the 11 missing outputs and verifying
the full 23-output release. See the [release reference](../reference/releases.md)
for status and the [release operations guide](https://github.com/sabbour/agentweaver/blob/v1/releases/README.md)
for its evidence contract.

## Resume and diagnose rollout

Source drift, a wrong target, missing prerequisites, mutable images, and
provenance mismatches stop rollout. `--resume` can reuse the completed apply
checkpoint, but repeats current source, registry, target, rollout, Pod, and
health checks.

Infrastructure success followed by bootstrap failure remains a partial
result. A rendered profile or ready shared workload does not prove per-Sandbox
execution or full API, UI, and MCP acceptance.

## Related content

- [Azure architecture](../architecture/azure.md)
- [Release reference](../reference/releases.md)
- [Kubernetes source and prerequisites](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Azure operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
- [Services and release design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
- [Testing strategy](./testing-strategy.md)
- [Runtime configuration reference](../reference/runtime-configuration.md)
