---
'agentweaver': patch
---

Fix a Postgres foreign-key violation that could silently drop a whole decomposed
work plan. `BacklogPromotionService` now saves task rows in their own
`SaveChangesAsync` call before adding and saving their dependency rows, so EF
Core/Npgsql's batched insert ordering can no longer race the dependency rows
ahead of the tasks they reference (`FK_backlog_task_dependencies_backlog_tasks_depends_on_task_id`).
