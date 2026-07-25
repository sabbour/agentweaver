---
'agentweaver': patch
---

Fix AgentHost mTLS startup so loading the mounted CA certificate no longer
attempts to parse a private key from the public-only `ca.crt` PEM.
