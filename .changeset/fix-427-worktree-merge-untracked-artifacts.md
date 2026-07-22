---
"agentweaver": patch
---

Fixed a coordinator merge failure that blocked runs whose kept-alive preview left
untracked build artifacts (e.g. a `node_modules/` directory in a demo app with no
`.gitignore`) in the working tree. When the merge took the working-tree
reconciliation path, harmless untracked files that are absent from the merge result
tree were incorrectly treated as unreconcilable and failed the merge with
"uncommitted content diverges from the merge result". Untracked paths the result
tree does not reference are now correctly left untouched (a hard reset never touches
them), matching the reconciler's documented contract, while genuinely divergent
edits to tracked files still correctly block the merge.
