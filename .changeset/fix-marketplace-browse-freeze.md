---
"agentweaver": patch
---

Fix the "Browse curated marketplaces" dialog freezing when selecting a source: browsing a marketplace now fetches only the source's subtree via the GitHub Trees API (bounded by a hard timeout) instead of a full, untimed repository clone, so failures surface as a clear error and a loading state is shown while browsing. Browsing also now falls back to an anonymous request when a user's token is refused with 401/403 (public marketplaces in SAML-enforced orgs such as `microsoft/skills` no longer 403), and lists candidate skills from the Git Trees metadata alone — without downloading any blob content at browse time — so large marketplaces like `github/awesome-copilot` (~400 skills) return results in a few seconds instead of timing out; skill content is downloaded only at import time. The "Azure Skills" marketplace subpath is also corrected to a plugin path that actually exists (`.github/plugins/azure-sdk-dotnet/skills`).
