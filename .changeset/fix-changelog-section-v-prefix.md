---
"agentweaver": patch
---

Fix `extractChangelogSection` to accept an optional `v` prefix inside bracketed changelog headings (e.g. `## [v0.9.70]`), so `release:sync-dev`'s pre-flight version check no longer fails on historical hand-authored CHANGELOG.md entries.
