---
"agentweaver": patch
---

Fix the "Browse curated marketplaces" dialog freezing when selecting a source: browsing a marketplace now fetches only the source's subtree via the GitHub Trees API (bounded by a hard timeout) instead of a full, untimed repository clone, so failures surface as a clear error and a loading state is shown while browsing.
