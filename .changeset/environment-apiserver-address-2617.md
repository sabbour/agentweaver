---
"Agentweaver.Environment": patch
---

Use the injected Kubernetes service address for the API server when `Kubernetes:ApiServer` is not set, so Environment does not need a DNS lookup that the egress policy blocks.
