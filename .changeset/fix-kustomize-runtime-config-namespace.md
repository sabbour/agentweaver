---
"agentweaver": patch
---

Fix `agentweaver-runtime-config` ConfigMap deploying to the wrong Kubernetes namespace (`default` instead of `agentweaver`), which caused `azure:deploy-from-local`/`azure:upgrade` to fail with `CreateContainerConfigError: configmap "agentweaver-runtime-config" not found` on the API, MCP, and worker deployments and the AgentHost SandboxTemplate.

The production Kustomize overlay (`k8s/overlays/production/kustomization.yaml`) generates this ConfigMap directly via `configMapGenerator`, but had no top-level `namespace:` transformer of its own. `k8s/base/kustomization.yaml`'s `namespace: agentweaver` transformer only applies to resources pulled in via `resources: - ../../base`, not to generators declared in the overlay itself, so the generated ConfigMap silently fell back to whatever namespace `kubectl apply` defaults to. Added `namespace: agentweaver` to the overlay's kustomization.yaml, and a regression test asserting every namespace-scoped resource in the built manifest set carries `namespace: agentweaver`.
