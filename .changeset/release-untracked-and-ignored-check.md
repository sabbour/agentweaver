---
"agentweaver": patch
---

Hardened the release pipeline (`azure:release-publish`, changeset prepare/sync
scripts) to reject untracked AND unexpectedly git-ignored files in the working
tree before publishing or syncing a release. Previously the check only ran
`git status --porcelain --untracked-files=all`, which does not surface files
that match a `.gitignore` pattern — an attacker-planted file under a path like
`node_modules/` or `dist/` could have been silently bundled into a release
artifact. The check now also flags unexpected ignored files, with a narrow
allowlist limited to genuinely safe, never-shipped editor/local-tooling paths
(`.vscode/`, `.idea/`, `.squad/`, etc.). Requires running these scripts from a
truly clean checkout, per the existing `RELEASING.md` guidance.
