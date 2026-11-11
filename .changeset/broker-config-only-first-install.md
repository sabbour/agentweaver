---
"Agentweaver.Identity.Broker": patch
---

Add a guarded first-install stage that creates the exact immutable Broker ConfigMap before the first Kubernetes deployment.
