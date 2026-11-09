# Deploy Agentweaver to Kubernetes

This directory contains the v1 Kustomize source for the Agentweaver workloads.
The root overlay defines nine service Deployments and Services, ServiceAccounts,
runtime ConfigMaps, the Projects & Config provider catalog, namespace controls,
network policies, and the Portal Web HTTPRoute. It references a separately
managed Gateway, listener, certificate, and public DNS.

The root pins ten product images, including the optional AgentHost runtime
profile. The profile includes its configure endpoint and keeps the existing
pending-turn limit of four. Environment supplies the matching AgentHost image
tuple and bootstrap settings. The operator must provide `agenthost-tls`; the
deployment helper checks its Kubernetes TLS Secret type but does not create or
inspect certificate data. AgentHost runs only in selected Sandboxes, not as a
shared Deployment. The Broker migration Job and acceptance Foundation Probe
are separate overlays.

For versioning and image builds, see the [release guide](../../releases/README.md).
For the Azure prerequisites and installation order, see
[Deploy Agentweaver to Azure](../../docs/guide/deploy-to-azure.md). Use the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
for ConfigMap keys, defaults, and value sources.

## Before applying

Select the approved cluster context and complete the environment-specific
prerequisites:

1. Select the published image tags or digests in an operator-supplied JSON manifest. GHCR is the default registry.
2. Complete the workload runtime ConfigMaps and Projects & Config provider
   catalog. Follow the runtime configuration reference; do not put credentials
   or private keys in ConfigMaps.
3. Create the operator-owned Secrets and backend CA ConfigMap referenced by
   the workloads. When using AgentHost, provide `agenthost-tls` as a
   `kubernetes.io/tls` Secret for the configured HTTPS endpoint. The installer
   checks only the Secret's name and type; verify its certificate and key
   through the approved operator process. Complete the managed certificate
   procedure so AKS creates `identity-broker-tls`.
4. Confirm the database and Entra workload identities are ready. The deploy
   command runs the selected service migrations before it applies workloads.
5. Run the infrastructure deployment so that the cluster has the `kata` node
   pool. RuntimeClass `kata-vm-isolation` needs a Ready node in this pool.
6. After the product Services exist, configure the separate managed Gateway
   and routing resources using the Azure operator guide.

The deployment command does not provision Azure resources, certificates,
runtime configuration, or external permissions. It creates only the
cluster-scoped workspace StorageClass in `cluster/` when that class is missing.

## Kustomize layout

| Path | Purpose |
| --- | --- |
| `base/` | Product namespace, ServiceAccounts, Deployments, Services, and network policies. |
| `cluster/` | Cluster-scoped workspace StorageClass, excluded from the root bundle. The deploy command creates it when it is missing. |
| `runtime-configs/` | Non-secret ConfigMap inputs for service hosts and Environment settings. |
| `provider-catalogs/` | Projects & Config provider catalog snapshot and ConfigMap. |
| `profiles/` | Optional runtime profiles, including the AgentHost runtime ConfigMap. |
| `proposed/` | Proposed Agent Sandbox RBAC rules, excluded from the active Kustomize bundle. |
| `migrations/identity-broker/` | Separate Broker migration ServiceAccount and one-shot Job. |
| `migrations/p1-postgres/` | PostgreSQL migration resources applied by the database maintenance runner. |
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
images automatically to GHCR. To select ACR images, add `--registry <acr-login-server>`;
use the login server form `<name>.azurecr.io`. Sign in with Azure CLI and run
`az acr login --name <acr-name>` first. The local identity must already have
pull access to the selected registry. This option applies to images selected
by the manifest or `--image-tag`; omitted manifest images keep their live
references and registries. `az acr login` stores Docker credentials in the
configured credential store; remove them with `docker logout <acr-login-server>`
when finished.

Before a migration or Kubernetes write, the helper checks that the live
`identity-broker-runtime-config-v3` ConfigMap exactly matches the rendered
Broker map. The check covers its API version, kind, name, namespace,
`immutable` flag, `data`, and `binaryData`. The Broker Deployment must also
reference this map. A missing, malformed, or different map stops the command
before writes. The error names changed fields or keys, not values. If the check
passes, the helper leaves this unchanged ConfigMap out of the final apply. It
does not create, replace, or repair the Broker map; that requires a separate
reviewed design.

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

### Cluster checks and install smoke check

Before any write, the command checks the cluster prerequisites for sandboxes:

- StorageClass `agentweaver-workspace-azurefile` uses `file.csi.azure.com` and
  the required mount options. If the class is missing, the command creates it
  from `cluster/workspace-storageclass.yaml` before the migrations. If it
  exists with other values, the command stops.
- RuntimeClass `kata-vm-isolation` exists, and at least one Ready,
  schedulable node matches its node selector. If not, the command stops and
  tells you to run the infrastructure deployment, which creates the `kata`
  node pool.

After all Deployments are ready, the command starts a short-lived pod with the
AgentHost labels, the sandbox RuntimeClass, the exact AgentHost image, and a
1 GiB workspace claim. The AgentHost network policies apply to this pod. The
pod has no ServiceAccount token, Secret, or settings. It checks that
`environment.agentweaver.svc.cluster.local` resolves, that a TCP connection to
Environment on port 8443 opens, and that the workspace
volume supports write, read, symbolic links, and UID 1000 ownership. A failure
or a 10-minute timeout fails the command with the cause. The command always
deletes the pod and claim.

The caller needs these extra permissions:

| Scope | Permissions |
| --- | --- |
| Cluster | `list nodes`; `create storageclasses` only when the class is missing. |
| Namespace `agentweaver` | `create`, `get`, `list`, and `delete` on `pods` and `persistentvolumeclaims`; `get pods/log`. |

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
before deploying workloads. The AgentHost profile and Environment Deployment
options must agree on the Sandbox image tuple. The Environment ConfigMap names
the AgentHost profile and TLS Secret; these must match the reviewed resources.
Follow the
[runtime configuration reference](../../docs/reference/runtime-configuration.md)
when changing an existing installation. For Knowledge, preserve the stable
resource label `agentweaver.knowledge` and increase `ResourceGeneration` only
after wiping or replacing the store. Keep the Projects private Broker
run-binding audience, certificate/key paths, and leaf pin together: the root
bundle may replace its immutable ConfigMap during a selective image deployment,
even when the Projects image is omitted.

### Environment BYOK model bindings

Do not add an environment BYOK model binding to this source. Keep the binding
in a model bindings file outside the repository, and give the file to each
deploy:

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --model-bindings <model-bindings.json> --execute
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --remove-model-binding <reference> --execute
```

`agentweaver models byok plan --overlay-output` writes the file. The helper
adds its bindings and Azure rate card to the AgentHost, Events & Sessions, and
Projects & Config runtime maps. It stops if the file does not match the
product model bindings, if the deploy would remove or change a live BYOK
binding without `--remove-model-binding` and a new revision, or if the deploy
would change the live Azure rate card without a new revision. It restarts
Events & Sessions when its runtime map changes. See
[Configure an Azure OpenAI BYOK model](../../docs/guide/configure-byok-azure-openai.md).

## Select images with a manifest

The local deploy command accepts an operator-supplied JSON file mapping image
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

Run the deployment helper with that file. It uses GHCR unless you select an
ACR login server:

```powershell
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --execute
npm run deploy:kustomize -- --image-manifest <path-to-image-manifest.json> --registry <acr-login-server> --execute
```

The v1 image workflow pushes tags and lists digests in its run summary; it does
not produce an image-manifest artifact. This file is only the image selection
input, not a publication receipt or source-claim gate. Its repository names
match the repository names in the image workflow and are unrelated to the
component-version manifest at `releases/foundation.json`.

Include only the images to update. The `--registry` option applies only to
repositories in the manifest. For omitted repositories, the helper reads the
current references and registries from the live Deployments, including the
AgentHost image configured in the Environment Deployment, and pins them to
their resolved digests. It fails if any omitted image cannot be read or validated;
it never falls back to the checked-in Kustomize pins. A full ten-image
manifest also works when no live Deployment refs are available. The existing
`--image-tag sha-<commit>` form remains available to update all ten images.
If you omit AgentHost, its live Environment image must already use the digest
in the retained runtime tuple. A tag or a different digest stops deployment
before writes; select AgentHost explicitly to update it.

When a Projects & Config migration completes, its Job retains a `Succeeded`
Pod that matches the Deployment selector. During Projects runtime ConfigMap
activation, the helper ignores only a Pod with phase `Succeeded`, the
`agentweaver.io/migration: projects-config` label, the
`projects-config-migration` ServiceAccount, and a controlling `batch/v1` Job
owner with a nonempty UID and a name that matches
`projects-config-migration-<8 lowercase hex>`. The Pod must not reference
`projects-config-runtime-config` through `envFrom.configMapRef` or
`env.valueFrom.configMapKeyRef` in a regular, init, or ephemeral container, or
through a ConfigMap volume or projected ConfigMap volume. Any other selected
Pod remains a blocker, including a terminal or terminating workload Pod. The
helper does not delete Jobs or Pods or change the migration completion gate.

After all blocking Pods stop, the helper waits 70 seconds for the immutable
ConfigMap cache window, replaces the map when needed, and restores the
Deployment replicas. If activation, workload apply, or rollout fails after
Projects activation starts, do not clear the Projects recovery annotation.
Rerun the same deployment command. If the ConfigMap is absent, the helper
keeps Projects & Config stopped until a retry recreates it.

Only selected images with schema migrations run migration Jobs: Broker runs
the Broker Job; Environment, Orchestrator, and Events & Sessions each run
their matching migration Job; Projects & Config runs its migration Job. Web, Gateway,
MCP, Knowledge, and AgentHost updates do not trigger these deployment
migration Jobs. The root Kustomize bundle is still applied and all nine
Deployment rollouts are checked.

The helper checks permissions before replacing any changed immutable runtime
ConfigMap. Its allow-list includes `agenthost-runtime-v1` and
`environment-runtime-config`; it uses UID and resource-version preconditions
when deleting each observed map, then verifies the replacement data. When the
Environment map changes, it records the Environment replica count, stops that
Deployment, waits for its Pods to terminate and for the 70-second
immutable-ConfigMap cache window, then replaces the map. A change to only the
AgentHost ConfigMap does not restart Environment.

If Environment replacement, workload apply, or rollout fails, the helper
keeps a recovery annotation on `environment`. Rerun the same deployment
command to resume the stop/wait/replace/resume sequence; if the ConfigMap is
absent, the helper keeps the Deployment stopped until the retry recreates it.
Do not clear the annotation manually.

Before any writes, the helper checks the effective Agent Sandbox permissions
of the Environment ServiceAccount and the metadata-only type of
`agenthost-tls`. These checks do not grant permissions or create Secrets. The
proposed rules in `proposed/environment-agent-sandbox-rbac.yaml` are not part
of the active Kustomize bundle and are never applied by this command.
