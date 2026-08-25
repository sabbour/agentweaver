---
"agentweaver": patch
---

Fix the agent host maintenance scan so Trivy SARIF uploads still reach GitHub Security while the workflow continues to enforce HIGH and CRITICAL vulnerability failures, and refresh the agent host image toolchain to pick up current Trivy fixes.
