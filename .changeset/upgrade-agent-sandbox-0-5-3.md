---
'agentweaver': patch
---

Bump the `agent-sandbox` controller pin (kubernetes-sigs/agent-sandbox) from `v0.5.0` to
`v0.5.3` in `scripts/azure/steps/10-create-cluster.mjs`. v0.5.2 renamed the core install
asset from `manifest.yaml` to `sandbox.yaml`, so the script's default manifest URL is
updated to match; the `SANDBOX_CONTROLLER_MANIFEST_URL` override remains available for
anyone pinning an older controller version. No user-facing behavior change is expected.
