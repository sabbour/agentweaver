---
"Agentweaver.Projects.Config": patch
---

Allow unchanged pre-cutover third-party Skills to continue running under existing integrity, scan, revocation, and authority checks after cutover, and harden `project_skill_content_revisions` against post-cutover `LegacyThirdParty` inserts and mutations.
