---
"agentweaver": minor
---

feat: built-in workflows support edit, schedule, and event triggers via copy-on-write

Editing, scheduling, or adding event triggers to a built-in workflow now automatically
creates a local project copy (with the same name) instead of failing silently. Built-in
entries are hidden from the list when a local copy exists, eliminating duplicate rows.
