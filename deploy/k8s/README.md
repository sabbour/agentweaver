# Deploy Agentweaver to Kubernetes

This directory contains the v1 Kustomize source for the Agentweaver workloads.
The root overlay defines nine service Deployments and Services, ServiceAccounts,
runtime ConfigMaps, the Projects & Config provider catalog, namespace controls,
network policies, and the Portal Web HTTPRoute. It references a separately
managed Gateway, listener, certificate, and public DNS.

The root also pins ten product images, including the optional AgentHost runtime
profile. The AgentHost profile remains incomplete until its Sandbox endpoint,
queue limit, and operator-owned TLS Secret are configured. The Broker
migration Job and acceptance Foundation Probe are separate overlays.

For versioning and image builds, see the [release guide](../../releases/README.md).
For the Azure prerequisites and installation order, see
[Deploy Agentweaver to Azure](../../docs/guide/deploy-to-azure.md). Use the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
for ConfigMap keys, defaults, and value sources.

## Before applying

Select the approved cluster context and complete the environment-specific
prerequisites:

1. Select the published GHCR image tags or digests in an operator-supplied JSON manifest.
2. Complete the workload runtime ConfigMaps and Projects & Config provider
   catalog. Follow the runtime configuration reference; do not put credentials
   or private keys in ConfigMaps.
3. Create the operator-owned Secrets and backend CA ConfigMap referenced by
   the workloads. Complete the managed certificate procedure so AKS creates
   `identity-broker-tls`.
4. Confirm the database and Entra workload identities are ready. The deploy
   command runs the selected service migrations before it applies workloads.
5. After the product Services exist, configure the separate managed Gateway
   and routing resources using the Azure operator guide.

The deployment command does not provision Azure resources, certificates,
runtime configuration, or external permissions.

## Kustomize layout

| Path | Purpose |
| --- | --- |
| `base/` | Product namespace, ServiceAccounts, Deployments, Services, and network policies. |
| `runtime-configs/` | Non-secret ConfigMap inputs for service hosts and Environment settings. |
| `provider-catalogs/` | Projects & Config provider catalog snapshot and ConfigMap. |
| `profiles/` | Optional runtime profiles, including the incomplete AgentHost profile. |
| `migrations/identity-broker/` | Separate Broker migration ServiceAccount and one-shot Job. |
| `migrations/p1-postgres/` | P1 migration resources applied by the database maintenance runner. |
| `routing/identity-broker/` | Portal Web route and Cilium ingress policies included by the root bundle. |
| `acceptance/foundation-probe/` | Supplemental acceptance probe resources. |
| `kustomization.yaml` | Root product bundle and image pins. |

The root bundle does not create the managed Gateway, certificates, public DNS,
PostgreSQL databases, or operator-owned Secrets. Migration Jobs and acceptance
resources remain separate from the product overlay.

## Deploy

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
```

The command resolves selected published images to GHCR digests, runs
migrations for selected services, renders the root overlay, applies it to the
selected `kubectl` context in namespace `agentweaver`, then waits for the nine
Deployments. A migration failure stops before the root overlay is applied.
Run it from the operator machine after selecting the approved Azure CLI
subscription and AKS kubeconfig context; it uses the local interactive Azure
CLI identity, not a GitHub-hosted deployment identity. Builds still publish
images automatically to GHCR.

Projects & Config's runtime identity is DDL-restricted. The migration Job uses
Kubernetes Workload Identity, not a token Secret. The one-time setup provisions
the `projects-config-migration` ServiceAccount annotation and the
`agentweaver-id-projects-config-migration` federated identity. A separate
PostgreSQL setup maps that identity to a principal with `CONNECT` and ownership
only of the `projects_config` schema and its exact planned objects; it must
not own the database, retain database `CREATE`, or access other schemas. The
deploy command checks the ServiceAccount annotation before creating any
migration Job and fails closed if it is missing.

The migration Job applies EF migrations, then reconciles the reviewed
runtime-grant plan transactionally. Runtime privilege errors keep the rollout
from reporting success.

## Verify workloads

Inspect the selected context, workloads, and Pods:

```powershell
kubectl config current-context
kubectl --context $context --namespace agentweaver get deployments,services
kubectl --context $context --namespace agentweaver get pods
```

Check each required Deployment rollout:

```powershell
kubectl --context $context --namespace agentweaver rollout status deployment/identity-broker
kubectl --context $context --namespace agentweaver rollout status deployment/projects-config
kubectl --context $context --namespace agentweaver rollout status deployment/knowledge
kubectl --context $context --namespace agentweaver rollout status deployment/events-and-sessions
kubectl --context $context --namespace agentweaver rollout status deployment/orchestrator
kubectl --context $context --namespace agentweaver rollout status deployment/environment
kubectl --context $context --namespace agentweaver rollout status deployment/gateway
kubectl --context $context --namespace agentweaver rollout status deployment/mcp
kubectl --context $context --namespace agentweaver rollout status deployment/web
```

Confirm that the Portal Web HTTPRoute references the approved managed Gateway
listener and that routes are programmed when routing is enabled. Run the
authorized HTTPS and request checks in
[Azure acceptance](../../docs/guide/azure-acceptance.md); a ready Pod alone
does not prove an end-to-end user workflow.

## Runtime configuration

Runtime ConfigMaps contain non-secret values. Most are immutable, so
Kubernetes rejects changes to their data. Review and apply the intended values
before deploying workloads. Follow the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
when changing an existing installation. For Knowledge, preserve the stable
resource label `agentweaver.knowledge` and increase `ResourceGeneration` only
after wiping or replacing the store. Keep the Projects private Broker
run-binding audience, certificate/key paths, and leaf pin together: the root
bundle may replace its immutable ConfigMap during a selective image deployment,
even when the Projects image is omitted.

## Select images with a manifest

The local deploy command accepts an operator-supplied JSON file mapping GHCR
repository names to published `sha-<40-character-commit>` tags or
`sha256:<64-hex-digest>` references:

```json
{
  "images": {
    "agentweaver-web": "sha-<40-character-commit>",
    "agentweaver.identity.broker": "sha256:<64-hex-digest>"
  }
}
```

Run the same deployment helper with that file:

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
```

The v1 image workflow pushes tags and lists digests in its run summary; it does
not produce an image-manifest artifact. This file is only the image selection
input, not a publication receipt or source-claim gate. Its repository names
match the GHCR names in the image workflow and are unrelated to the
component-version manifest at `releases/foundation.json`.

Include only the images to update. For omitted repositories, the helper reads
the current references from the live Deployments, including the AgentHost
image configured in the Environment Deployment, and pins them to their
resolved digests. It fails if any omitted image cannot be read or validated;
it never falls back to the checked-in Kustomize pins. A full ten-image
manifest also works when no live Deployment refs are available. The existing
`--image-tag sha-<commit>` form remains available to update all ten images.

Only selected images with schema migrations run migration Jobs: Broker runs
the Broker Job; Environment, Orchestrator, and Events & Sessions each run
their matching P1 Job; Projects & Config runs its migration Job. Web, Gateway,
MCP, Knowledge, and AgentHost updates do not trigger these deployment
migration Jobs. The root Kustomize bundle is still applied and all nine
Deployment rollouts are checked.

If the rendered `projects-config-runtime-config` data changes, the helper
checks delete permission scoped to `configmap/projects-config-runtime-config`
before migrations. It saves the existing replica count on the exact
`projects-config` Deployment, stops that Deployment, waits for its Pods to
terminate and for the 70-second immutable-ConfigMap cache window, then deletes
and recreates only `projects-config-runtime-config`. It restores the saved
replica count before applying the workloads and checking all rollouts.

If replacement, workload apply, or rollout fails, the helper keeps a recovery
annotation on `projects-config`. Rerun the same deployment command to resume
the stop/wait/replace/resume sequence; if the ConfigMap is absent, the helper
keeps the Deployment stopped until the retry recreates it. Do not clear the
annotation manually.
