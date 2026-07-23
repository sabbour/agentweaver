---
"agentweaver": patch
---

Bump the vendored `@github/copilot-linux-x64` CLI binary in the AgentHost image from 1.0.67 to 1.0.71-3, self-update npm before installing global tooling, bump `yq` from 4.44.3 to 4.53.3 (dominant source of the remaining HIGH/CRITICAL findings), and add a cache-busting `GH_CLI_CACHE_BUST` ARG to the GitHub CLI apt-install layer so it stops silently reusing a stale, CVE-carrying cached `gh` build across CI runs. Add a narrowly-scoped `.trivyignore` for the handful of CVEs confirmed to have no fix in the newest upstream `yq`/`gh` releases (transitive Go stdlib/grpc-go/x-net/x-text baked into third-party compiled binaries we cannot patch) — the Trivy gate's severity/exit-code/ignore-unfixed settings are untouched, so any other CVE still fails the build. Together these close out the Trivy image scan without weakening the scan gate itself.
