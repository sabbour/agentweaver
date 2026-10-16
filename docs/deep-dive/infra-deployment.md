# Infrastructure and deployment

## The mental model

Infrastructure, artifact publication, and runtime rollout are separate operations.

Bicep defines Azure resources. Kubernetes manifests define workload and network objects. Release tooling verifies source and artifact provenance before rendering or applying a runtime composition. A successful build is not a deployment receipt, and a deployment receipt is not end-to-end product acceptance.

This page describes the checked-in deployment paths. It does not report a live rollout.

## Where this lives

- `infra/bicep/main.bicep` and `modules/` — dedicated foundation resources.
- `infra/bicep/aks-redeploy.bicep` — bounded AKS replacement path.
- `infra/bicep/p1-runtime-identities.bicep` — separate runtime-identity setup.
- `deploy/k8s/base/`, `migrations/`, and `acceptance/` — namespace, Broker runtime, migration Job, and Foundation Probe.
- `deploy/k8s/routing/identity-broker/` — managed Gateway routing and backend TLS definitions.
- `deploy/k8s/profiles/` — separately selected runtime profiles.
- `scripts/azure/plan.mjs`, `deploy.mjs`, and `lib/git.mjs` — exact-source infrastructure planning and deployment.
- `scripts/azure/verify-acceptance.mjs` — source-bound Foundation Probe verification.
- `scripts/release/pack.mjs`, `publish.mjs`, and `registry.mjs` — artifact preparation, publication, and registry evidence.
- `scripts/release/deploy.mjs`, `deploy-integration.mjs`, and `runtime-list.mjs` — release/integration rollout and shared workload rendering.
- `.github/workflows/v1-integration-images.yml` and `v1-release-pack.yml` — manually dispatched artifact workflows.

## Core concepts and invariants

- Source identity includes the exact commit and tree, not just a branch or mutable image tag.
- Runtime rollout consumes verified published images. It does not build replacements during deployment.
- Shared services and per-Sandbox AgentHost placement have separate lifecycles.
- Missing runtime prerequisites stop deployment. The rollout does not create new credentials or grant itself access.

## What the foundation provisions

`main.bicep` targets a dedicated resource group. It defines:

- A VNet with separate AKS-node, delegated PostgreSQL, and private-endpoint subnets.
- AKS with managed Entra access, OIDC and workload identity, Azure RBAC, and Cilium networking.
- Entra-only PostgreSQL Flexible Server.
- RBAC-based Key Vault and private Blob storage for platform artifacts.
- Log Analytics, Application Insights, and Azure Monitor Private Link Scope.
- Private endpoints, DNS links, workload identities, federation, and scoped role assignments.

Key Vault and Blob disable public network access. PostgreSQL uses the delegated private network. Monitor disables public query access while allowing its configured ingestion path. The template does not create a workspace Azure Files share or install the Agent Sandbox controller.

Reviewed parameters place core resources in `eastus2euap` and Monitor service resources in `eastus2`. The Monitor private endpoint stays in the VNet region. See [Azure architecture](../architecture/azure.md) for the resource and region contract.

## Deploying infrastructure from a commit

`scripts/azure/lib/git.mjs` resolves a clean checkout and validates the selected full source SHA against the admitted `origin/v1` history. It compares committed infrastructure input bytes and computes the source tree and input hash.

`plan.mjs` checks the dedicated target and ownership, then builds an Azure what-if request. `deploy.mjs` defaults to a nonexecuting summary. Its write path requires `--execute`, checks the target, runs what-if, and rechecks source inputs before mutation.

The full foundation path accepts an empty dedicated P0 resource group. The AKS-only path preserves existing foundation resources and checks autoscaler state. Source SHA, tree, and input hash bind the deployment arguments and returned outputs.

Namespace bootstrap, database setup, Broker runtime/state, routing, and acceptance inputs are separately selected operations. They are not implied by compiling Bicep. The acceptance verifier checks exact target, image, source, receipt, identity, and fresh Monitor evidence after the Probe Job completes.

## Kubernetes workload boundaries

The base contains namespace limits, default-deny networking, service accounts, and the Broker Deployment and private HTTPS Service. The Broker migration Job is separate from ordinary runtime startup.

Projects and Events runtime profiles require explicit reviewed inputs, existing TLS/configuration references, immutable product images, and prepared database permissions. Rendering does not create those prerequisites.

Routing uses the native `approuting-istio` Gateway class, explicit Gateway namespace policy, managed certificates, and backend TLS policies. Public route definitions do not replace trusted TLS, DNS, or owner health checks.

The shared runtime `List` contains nine service Deployment/Service pairs. AgentHost is the tenth product image, but runs per Sandbox rather than as a shared Deployment.

## Integration images and release deployment

The integration workflow takes an exact `v1` source SHA, prepares ten source-tagged service images, and separates preparation from approved publication. Its images-only receipt retains the draft manifest and source tree. `deploy-integration.mjs` verifies that receipt and registry provenance before rendering or applying the composition.

The release workflow prepares independently versioned packages and images from an exact source. Publication is a separate confirmed operation. `deploy.mjs` consumes an already-published `v1.x.y` release, its manifest, and publication receipt:

1. Verify the clean product checkout, release source, manifest, and publication identity.
2. Verify digest-pinned GHCR images through the registry API. Verify the measured AgentHost image and compressed pull size.
3. Render the checked-in runtime `List`, or validate an explicit runtime manifest.
4. Verify the exact Azure target, current Kubernetes context, existing runtime configuration, TLS, identities, and policies.
5. Run client-side dry-run. Without `--execute`, stop before applying.
6. Apply the validated workloads and save an apply checkpoint.
7. Recheck source, images, and target; wait for rollouts and verify ready Pods and pulled image IDs.

The tool does not create missing credentials, identities, routes, namespaces, migrations, or databases. Partial publication requires a verified completion proof; it is not accepted as a complete release.

## Failure and resume behavior

Source drift, wrong target, missing prerequisites, mutable images, and provenance mismatches stop rollout. `--resume` can reuse the completed apply checkpoint, but it repeats current source, registry, target, rollout, Pod, and health checks.

Infrastructure success followed by bootstrap failure remains a partial result. A rendered profile or ready shared workload does not prove per-Sandbox execution or full API/UI/MCP acceptance.

## Related pages

- [Azure architecture](../architecture/azure.md)
- [Release reference](../reference/releases.md)
- [Kubernetes source and prerequisites](https://github.com/sabbour/agentweaver/blob/v1/deploy/k8s/README.md)
- [Azure operator interface](https://github.com/sabbour/agentweaver/blob/v1/scripts/azure/README.md)
- [Services and release design](https://github.com/sabbour/agentweaver/blob/v1/docs/architecture/design/services-and-release.md)
- [Testing strategy](./testing-strategy.md)
