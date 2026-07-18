# Sandbox setup

This reference covers the AKS setup used by the deployment scripts. The live in-cluster model is a single AgentHost warm pool managed by the upstream `agent-sandbox` controller.

## Components

| Component | Source | Purpose |
|---|---|---|
| `agent-sandbox` controller | `scripts/aks/10-create-cluster.sh` | Installs CRDs in API group `extensions.agents.x-k8s.io`. |
| `SandboxTemplate/agentweaver-agent-host` | `k8s/sandbox-template-agenthost.yaml` | Defines the Kata-isolated AgentHost pod: image, service account, workspace PVC, config, A2A port `8088`. |
| `SandboxWarmPool/agentweaver-agent-host` | `k8s/sandbox-warmpool-agenthost.yaml` | Keeps AgentHost pods pre-warmed for fast run startup. |
| `SandboxClaim` | created per run by the API/worker | Binds one warm AgentHost pod for a run, then releases it on completion/suspend. |

## Install order

The canonical workflow provisions and deploys the sandbox pieces through the
root package scripts (`pnpm run` shown; `npm run` is equivalent):

```bash
pnpm run infra:deploy      # installs the controller + CRDs
pnpm run release:images    # publishes agentweaver-agent-host
pnpm run release:deploy    # applies the template/warm pool and verifies it
```

`release:deploy` applies `sandbox-template-agenthost.yaml` before
`sandbox-warmpool-agenthost.yaml`; the warm pool depends on the template by
name. For targeted recovery, the [AKS deployment runbook](../guide/deployment-aks.md#running-an-individual-step)
shows how to run individual steps, including `gen-a2a-mtls-certs`.

## AgentHost pod behavior

1. Warm pods start without run context and wait in standby.
2. A run creates a `SandboxClaim` referencing warm pool `agentweaver-agent-host`.
3. The controller binds a warm pod and reports it in `status.sandbox.name`.
4. The API/worker calls AgentHost `/configure` with run/user/token context.
5. Agent turns run over A2A on port `8088`.
6. Releasing the claim deletes the used pod; the warm pool replenishes it.

Per-run context is not injected through `SandboxClaim.spec.env`; doing so bypasses warm-pool adoption in controller v0.5.0. Static config lives in the template and `configmap-agenthost.yaml`.

## Required configuration

| Setting | Value |
|---|---|
| Namespace | `agentweaver` by default (`NAMESPACE`) |
| AgentHost warm pool ref | `agentweaver-agent-host` |
| RuntimeClass | `kata-vm-isolation` |
| AgentHost image | `${ACR_LOGIN_SERVER}/agentweaver-agent-host:${AGENTHOST_IMAGE_TAG}` |
| Key Vault URI | `https://${KEYVAULT_NAME}.vault.azure.net/` |
| Workspace | PVC `agentweaver-workspace`, mounted at `/workspace` |

The AgentHost image is built by `pnpm run release:images` from
`apps/Agentweaver.AgentHost/Dockerfile`. It must publish with `--runtime
linux-x64 --self-contained false` so the Copilot native runtime is included.

## Verify

```bash
kubectl api-resources --api-group=extensions.agents.x-k8s.io
kubectl get runtimeclass kata-vm-isolation
kubectl get sandboxtemplate,sandboxwarmpool -n agentweaver
kubectl get pods -n agentweaver -l app.kubernetes.io/component=agent-host
node scripts/run-os-script.mjs 40-verify
```

Expected resources:

```text
sandboxtemplate.extensions.agents.x-k8s.io/agentweaver-agent-host
sandboxwarmpool.extensions.agents.x-k8s.io/agentweaver-agent-host
```

## Troubleshooting

| Symptom | Check |
|---|---|
| Warm pods do not appear | `kubectl describe sandboxwarmpool agentweaver-agent-host -n agentweaver` |
| Pods stay Pending | `kubectl get runtimeclass`, `kubectl describe node`, and `katapool` capacity |
| Image pull failure | image tag matches `AGENTHOST_IMAGE_TAG` and ACR is attached to AKS |
| `/configure` or A2A fails | NetworkPolicies allow API/worker to AgentHost TCP `8088`; run `node scripts/run-os-script.mjs 40-verify` |
| Token fetch fails | service account `agentweaver-agent-host` has workload identity federation and Key Vault access |
