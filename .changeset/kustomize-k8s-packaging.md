---
"agentweaver": minor
---

Migrate the AKS deployment manifests under `k8s/` from flat, envsubst-rendered YAML to a Kustomize-based `base/` + `overlays/production/` layout.

`scripts/azure/steps/30-deploy.mjs` now builds the full production overlay via `kubectl kustomize` (kubectl's built-in Kustomize support -- no separate `kustomize` binary required) instead of the old hand-rolled `lib/render.mjs` envsubst renderer, then re-groups the combined build back into the same staged apply order (identity/RBAC/quota/PVCs, network policies, services/gateway/routes, sandbox template, deployments, worker) it has always used. Dynamic values (image tags, the public HOST-derived URLs, workload-identity IDs, Key Vault/Tenant IDs, hostnames) are now injected via Kustomize's `images:` transformer, a `configMapGenerator` (`agentweaver-runtime-config`), and `replacements:` patches instead of textual placeholder substitution.

Manifests not part of the automated deploy (one-off migration Jobs, example-only Secrets, the app-code `SandboxClaim` template) moved to `k8s/reference/` and are excluded from the Kustomize base. No new tool prerequisite is required: `kubectl apply -k` / `kubectl kustomize` cover this migration's needs.
