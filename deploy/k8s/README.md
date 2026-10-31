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

1. Select the immutable `sha-<commit>` tag produced by the automatic v1 image workflow.
2. Complete the workload runtime ConfigMaps and Projects & Config provider
   catalog. Follow the runtime configuration reference; do not put credentials
   or private keys in ConfigMaps.
3. Create the operator-owned Secrets and backend CA ConfigMap referenced by
   the workloads. Complete the managed certificate procedure so AKS creates
   `identity-broker-tls`.
4. Confirm the database and Entra workload identities are ready. The deploy
   command runs the Broker and P1 migration Jobs before it applies workloads.
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
npm run deploy:kustomize -- --image-tag sha-<40-character-commit> --execute
```

The command resolves all ten tags to GHCR digests, runs the existing Broker
and P1 migrations, runs Projects & Config migration, renders the root overlay,
applies it to the selected `kubectl` context in namespace `agentweaver`, then
waits for the nine Deployments. A migration failure stops before the root
overlay is applied. Run it from the operator machine after selecting the
approved Azure CLI subscription and AKS kubeconfig context; it uses the local
interactive Azure CLI identity, not a GitHub-hosted deployment identity.

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
after wiping or replacing the store.
