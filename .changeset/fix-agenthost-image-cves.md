---
"agentweaver": patch
---

Bump the vendored `@github/copilot-linux-x64` CLI binary in the AgentHost image from 1.0.67 to 1.0.71-3, self-update npm before installing global tooling, bump `yq` from 4.44.3 to 4.53.3 (dominant source of the remaining HIGH/CRITICAL findings), and add a cache-busting `GH_CLI_CACHE_BUST` ARG to the GitHub CLI apt-install layer so it stops silently reusing a stale, CVE-carrying cached `gh` build across CI runs. Together these close the Go stdlib/`golang.org/x/net`/`tar`/`undici`/`gh` CVEs flagged by the Trivy image scan without weakening the scan gate itself.
