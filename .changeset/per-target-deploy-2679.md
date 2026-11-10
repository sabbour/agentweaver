---
"Agentweaver.Projects.Config": patch
---

Add `--resource-group <target>` to `db:maintenance:setup`, `bootstrap-p1-postgres.mjs`, `db:maintenance` (`--prepare-only` and `--release-lock` only), `db:maintenance:projects-config`, `deploy-kustomize.mjs`, and `azure:deploy-from-commit`. The default `agentweaver` target behaves as before. A separate target uses its own PostgreSQL plan principals, Azure identity names, and `deploy/k8s/targets/<target>.json` text replacements, and the deployment stops unless the selected Kubernetes context is the target's AKS API server. Fix the Projects Config `InternalsVisibleTo` entry so the P1 PostgreSQL initializer builds.
