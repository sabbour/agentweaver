---
"agentweaver": patch
---

Render fully-promoted ("delegated to backlog") coordinator runs as complete instead of
leaving RAI, Human Review, Merge, and Scribe stuck as "Pending forever", and notify the
user when subtasks are promoted to the Board.

- The coordinator graph descriptor now marks the skipped assembly stages of a delegated
  run with an authoritative `delegated` status (single source of truth); the run tree and
  workflow graph render those nodes as a terminal "Delegated to backlog" state and the
  coordinator/work-plan nodes as Completed.
- A poll-derived "N subtasks created" notification (linking to the project Board) is
  emitted for delegated runs, reusing the existing notification center with board-specific
  toast/badge copy.
