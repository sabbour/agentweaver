---
---

Keep environment BYOK model bindings out of the deployment source. The agentweaver models byok plan command can now write a model bindings file
with --overlay-output. The Kustomize deploy helper and the exact-commit deploy helper merge this file with --model-bindings, remove a live BYOK
binding with --remove-model-binding and a new revision, stop when the Azure rate card would change without a new revision, and restart
Events & Sessions when its runtime map changes.
No product component version changed.
