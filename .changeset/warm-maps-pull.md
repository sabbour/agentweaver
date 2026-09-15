---
"agentweaver": patch
---

Best-effort ACR timeout handling now warns and continues when the caller allows failure.
This prevents a hung Azure CLI provenance lock from stopping a deployment after the registry operation can already have completed.
