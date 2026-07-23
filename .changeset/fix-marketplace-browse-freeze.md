---
"agentweaver": patch
---

Fix the "Browse curated marketplaces" dialog freezing when selecting a source: browsing a marketplace now fetches only the source's subtree via the GitHub Trees API (bounded by a hard timeout) instead of a full, untimed repository clone, so failures surface as a clear error and a loading state is shown while browsing. Browsing also now falls back to an anonymous request when a user's token is refused with 401/403 (public marketplaces in SAML-enforced orgs such as `microsoft/skills` no longer 403), and lists candidate skills from their `SKILL.md` manifests alone rather than downloading every resource blob, so large marketplaces like `github/awesome-copilot` return results in a few seconds instead of timing out.
