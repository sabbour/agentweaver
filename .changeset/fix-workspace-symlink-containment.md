---
"agentweaver": patch
---

Resolve symlinks and reparse points before workspace file-access containment checks so a
repository-planted symlink can no longer escape the workspace root. Previously the checks were
lexical only (`Path.GetFullPath` + string prefix), which validated the pathname but still
followed a symlink on read or write — allowing a malicious repo to disclose or overwrite files
outside its worktree (e.g. a mounted secrets store). Workspace read and write endpoints now share
a single `WorkspacePathGuard` that resolves the real target and rejects any path landing outside
the workspace root.
