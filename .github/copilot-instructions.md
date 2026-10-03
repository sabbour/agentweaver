# Agentweaver project workflow routing

Read the repository README and its linked v1 architecture/testing guides for canonical
commands. `AGENTS.md` and `CONTRIBUTING.md` are not present on this branch.

The project extension `agentweaver-issue-to-merge` is discoverable in Agentweaver
sessions; discovery does **not** automatically run it for every prompt. Use the
`run_dynamic_workflow` tool only when the user explicitly requests an issue-to-merge
workflow, or a matching skill/slash command specifically instructs its use. An
issue-backed request for complete implementation, local reviews, PR, CI, and
serialized merge is eligible. Explanations, read-only investigations, and
release/deployment requests are not. Surface the intended workflow use to the
user, respect native workflow approval, and do not invent resource ceilings.

For authorized delivery, supply the concrete task, positive issue number, target
branch (`v1` by default), existing `type:` and `area:` labels, milestone, and
`mode: "deliver"`; omission of mode runs a no-mutation rehearsal. Inspect the
durable run result and resume the same run ID after a resolvable interruption.
Do not treat a completed rehearsal as delivery proof. The run is bound to its
initiating session, not shared across project sessions. There is no auto-run hook.
