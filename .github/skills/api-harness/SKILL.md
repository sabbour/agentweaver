---
name: api-harness
description: Run Agentweaver's persona-driven REST API harness for backend end-to-end validation, reproducible verification reruns, or investigation of an API persona/scenario failure. Use this whenever asked to run, validate, retest, reproduce, or investigate the API/backend harness—even when the request only mentions a persona, a scenario, a repro manifest, or a backend workflow. Capture and report the generated JSON verdict or exploratory transcript rather than recreating API calls by hand.
domain: testing
confidence: high
source: scripts/api-harness/SKILL.md
allowed-tools: Bash(node scripts/api-harness/run-persona.mjs:*) Bash(node scripts/api-harness/agent-driver/tools.mjs:*) Bash(npm --prefix scripts/api-harness:*)
---

# API harness

Read [`scripts/api-harness/SKILL.md`](../../../scripts/api-harness/SKILL.md) before
running the harness. It is the source of truth for its real CLI, flags, safety
controls, exit codes, evidence artifacts, and both supported modes:

- For a structured scenario or a re-test from a `reproManifest`, invoke
  `node scripts/api-harness/run-persona.mjs` with a fresh scenario run. A manifest
  is provenance for a new comparable run—not an old `runId` replay.
- For a free-text or exploratory persona investigation, invoke
  `node scripts/api-harness/agent-driver/tools.mjs` in its session-based sequence
  and call `finish` to persist the transcript.

Use the actual commands in that contract. Capture the output path, inspect the
verdict/transcript JSON, and report the outcome with its evidence path and whether
it is pass, fail, or inconclusive. Do not claim that a zero driver exit establishes
subjective output quality.
