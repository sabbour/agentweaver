---
"agentweaver": patch
---

fix(demo): beat capture fixes, narration scripts, and clean-staging improvements

- Beat 4.1: add 30s pause after Enter so SPA navigates to /assistant?runId before beat 4.2 starts
- Beats 4.2+4.3: remove startUrl for cross-beat URL continuity; use transcript text waitFor
- clean-staging: accept continuation beats (no startUrl) and unresolved env-var placeholders
- clean-staging: fix mojibake em dash in Blueprint fixture projectName
- Add narration scripts for AKS, Blueprint, and sizzle reel scenarios
- Add sizzle reel direction manifest (93.8s draft, 13/15 beats assembled)
