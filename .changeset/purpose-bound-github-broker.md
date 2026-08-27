---

"agentweaver": minor

---



Add purpose-bound GitHub capability fencing with immutable run snapshots for root, child, retry, and recovery launches. Root snapshots are now selected and captured directly from live authorization, repository grant, and Copilot binding sources at launch; the finite v1 legacy table is migration-only and is never consulted for new runs. A project with no GitHub App history launches with zero snapshots by design, while a project with GitHub App history that currently resolves none of the four purposes is denied rather than silently launched without capability protection.
