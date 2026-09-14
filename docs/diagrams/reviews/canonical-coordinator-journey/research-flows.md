# Research handoff: flows

Completed independent GPT-6 Astra directional-flow thread: findings supplied by the coordinator.

Each child has its own worktree. The dependency frontier drives dispatch; it is not peer chat.
Failed or RAI-flagged children block dependents. Board cards project persisted state; unresolved
dependencies leave tasks in Ready with a blocked flag. Approval is not completion.
EF/PostgreSQL RunEvents relay across replicas by cursor polling every 250 ms when idle.
Append takes a per-run lock, allocates MAX+1, commits, then acknowledges. Subscribers drain
the loaded batch before closing on a terminal event. SQLite register-channel/replay/tail is a separate lane.

The original flow tool-output attachment was named
1789326947702-copilot-tool-output-3e5f055558f345178849bcbf9aebd687.txt.
Its Temp location was not opened because this execution context forbids all Temp file operations.
This is the supplied findings summary, not a claimed verbatim copy of that report.
