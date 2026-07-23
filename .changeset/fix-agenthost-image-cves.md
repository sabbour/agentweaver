---
"agentweaver": patch
---

Bump the vendored `@github/copilot-linux-x64` CLI binary in the AgentHost image from 1.0.67 to 1.0.71-3 and self-update npm before installing global tooling, closing the Go stdlib/`golang.org/x/net`/`tar`/`undici` CVEs flagged by the Trivy image scan without weakening the scan gate itself.
