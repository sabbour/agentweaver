---
"agentweaver": patch
---

Shorten the git SHA shown in the version badge to 7 characters, matching the short-SHA convention already used for `IMAGE_TAG` (`AppVersionProvider` now truncates the full `GIT_SHA` env var instead of passing it through as-is).
