---
"agentweaver": patch
---

Make A2A mTLS secret reads fail closed during Azure deployments.

The deploy tool now checks Kubernetes Secret state as present, absent, or unknown. It retries transient read failures. It generates the A2A certificate secrets only after it confirms that all three secrets are absent. If the read state stays unknown, it stops with a read error and does not suggest `force: true`.
