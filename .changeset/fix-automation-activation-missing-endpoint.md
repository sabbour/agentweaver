---
"agentweaver": minor
---

Add real Activate/Deactivate endpoints and a Project Settings control for scheduled/event automation, and add BYOK as a valid model-provider source for activation.

Root cause: `AutomationActivationSnapshotService.ActivateAsync` — the only thing that creates an
`AutomationActivationRecord`, which `WorkflowScheduleTriggerService`/`WorkflowEventTriggerService`
both require before they'll fire — had no production endpoint or caller at all; only tests
invoked it. Project Settings explicitly said "This page does not enable or activate automation."
As a result, no user could actually turn on scheduled/event automation for a project through any
current product surface, even though the trigger infrastructure and UI copy implied it was a real,
usable feature. Pre-existing/migrated activation records kept working, but nothing new could ever
be activated.

- Added Owner-only endpoints: `GET /api/projects/{id}/automation/status`,
  `POST /api/projects/{id}/automation/activate`, `POST /api/projects/{id}/automation/deactivate`,
  backed by `AutomationActivationSnapshotService.ActivateAsync`/new `DeactivateAsync`/`GetStatusAsync`.
- Extended `AutomationActivationRecord` with a `ModelProviderSource` (`GitHubCopilot`/`Byok`) and an
  optional `ByokProviderId`, so an activation snapshot can now pin BYOK as its model-provider source
  instead of always requiring a GitHub Copilot binding. Added matching SQLite/Postgres migrations,
  including updating the snapshot's insert/immutability trigger and CHECK constraint to branch on
  the new BYOK-sourced shape.
- Added a Project Settings "Background automation" control (Activate/Deactivate button + status)
  that only Owners can see — non-Owners get a 403 from the status endpoint and never see the
  control at all — and removed the stale "does not enable or activate automation" copy.
- Added an end-to-end test proving activate → scheduled trigger fires → deactivate → trigger no
  longer fires → reactivate → trigger fires again, plus new BYOK activation/fencing and
  deactivate/reactivate/status coverage.
