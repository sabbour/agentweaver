---
---

Retry transient registry token, manifest, image-config, and pinned AgentSandbox
manifest reads during release and integration deployment with bounded exponential
backoff. Preserve digest checks and checkpoint-bound resume verification, and
report credential-free request URLs and underlying network causes on failure.
