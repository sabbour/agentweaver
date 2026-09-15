---
name: agentweaver-api-harness
description: Run Agentweaver's REST API harness for backend validation, repro reruns, or plain-English scenario exploration. Use for API/persona failures; use combined harness for cross-surface sweeps.
domain: testing
confidence: high
source: scripts/api-harness/SKILL.md
allowed-tools: Bash(node scripts/api-harness/run-persona.mjs:*) Bash(npm --prefix scripts/api-harness:*)
---

# API harness

Read [`scripts/api-harness/SKILL.md`](../../../scripts/api-harness/SKILL.md) before
running the harness. It is the source of truth for its real CLI, flags, safety
controls, exit codes, evidence artifacts, and both supported modes:

- For a structured scenario or a re-test from a `reproManifest`, invoke
  `node scripts/api-harness/run-persona.mjs` with a fresh scenario run. A manifest
  is provenance for a new comparable run—not an old `runId` replay.
- For a free-text or exploratory persona investigation, dispatch a fresh
  **`PersonaActor`** sub-agent with the persona brief. PersonaActor drives the live
  API itself and records its own transcript under
  `scripts/api-harness/transcripts/`. There is no `agent-driver` CLI.

Runs are unattended by default, and `auto_approve_tools` does **not** cover shell
commands: a run will emit `shell.approval_required` and stall forever unless
something approves it. Poll `GET /api/runs/{id}/events` and approve via
`POST /api/runs/{id}/shell-approvals` with `{"command_hash": "<payload.commandHash>"}`,
or the run will look `InProgress` while making no progress at all.

Use the actual commands in that contract. Capture the output path, inspect the
verdict/transcript JSON, and report the outcome with its evidence path and whether
it is pass, fail, or inconclusive. Do not claim that a zero driver exit establishes
subjective output quality.

Before running, check `scripts/harness-shared/learnings.md` (surface: `api` or `all`)
for already-known bugs, environment facts, and scenario-design notes so they aren't
rediscovered from source each run.
