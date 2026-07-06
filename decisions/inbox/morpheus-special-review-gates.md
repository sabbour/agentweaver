# Morpheus special review gates

Decision: review gates are authored in workflow YAML as `check` nodes with `gate_kind: rai | rubberduck | human-review`. The Coordinator derives the assembly review chain from the selected workflow's authored gates and executes them once over the aggregate integration diff, in authored node order. Rubberduck is a pass/revise gate and uses the existing RubberduckTurnExecutor.

Merge and Scribe are platform actions, not authorable workflow steps. The Coordinator appends Merge then Scribe after the authored gates for every orchestration. Built-in catalog workflows no longer author merge/scribe nodes.

The old ReviewPolicies backend was removed because it competed with workflow YAML as a source of truth. Single-run start endpoints now return 410 Gone; GET/view paths remain.

Risks: resume-from-human-review assumes the human gate is terminal among authored assembly gates, matching the updated built-ins. Future workflow-editor support should preserve `gate_kind` vocabulary and avoid emitting merge/scribe nodes.
