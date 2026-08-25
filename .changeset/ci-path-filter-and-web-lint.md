---
"agentweaver": patch
---

Scope the CI `changes` path filter to `.github/workflows/ci.yml` instead of `.github/workflows/**`, so editing an unrelated workflow (agent-host-maintenance, docs-drift, publish-images, squad-*) no longer runs the entire .NET, web, Node toolchain, docs and diagram matrix. Applies the same scoping to `areasForPaths` in `scripts/ci/validate.mjs` so local and CI classification stay in sync. Also removes the `Web lint` job, an echo-only stub that could never fail and billed a full minute on every web change; lint still runs in `Web tests`.
