---

"agentweaver": minor

---



Add purpose-bound GitHub capability fencing with immutable run snapshots for root, child, retry, and recovery launches. Root snapshots are now selected and captured directly from live authorization, repository grant, and Copilot binding sources at launch; the finite v1 legacy table is migration-only and is never consulted for new runs. Only a project whose persisted origin is explicitly blank may launch with zero snapshots; a GitHub-origin project that currently resolves none of the four purposes is denied rather than silently launched without capability protection. GitHub App history rows (installations, grants, bindings) are no longer used as the blank-project signal, since a GitHub-origin project can legitimately have none of those rows yet.
