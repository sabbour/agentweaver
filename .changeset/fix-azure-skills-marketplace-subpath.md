---
"agentweaver": patch
---

Fix the built-in "Azure Skills" marketplace subpath so browsing it finds skills. The `microsoft/skills` repo nests `SKILL.md` files one directory deeper (`.github/plugins/azure-skills/skills/<name>/SKILL.md`) than the previously configured subpath.
