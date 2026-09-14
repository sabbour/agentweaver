---
"agentweaver": patch
---

Stop unattended runs spinning on a shell command blocked by the destructive-command approval gate.

A run created with `auto-approve-tools`/`autopilot` has no operator watching for approval cards, but
`run_command` still told it to "retry this command" after approval — so the agent re-issued the same
blocked command indefinitely while the run reported `InProgress`. Destructive shell remains
deliberately ineligible for run-level auto-approval, so the gate is unchanged; only the guidance is.
An unattended run is now told not to retry and to achieve the result without the destructive
operation, and `shell.approval_required` carries an `unattended` flag for the timeline.
