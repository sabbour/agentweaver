---
name: "ui-harness"
description: "Run Agentweaver's persona-driven Playwright UI evidence harness for deployed browser-flow validation. Use whenever asked to run or validate the real web UI end-to-end, test a named persona's browser flow, capture UI evidence, or investigate a UI-reported issue; do not use for unit-only UI tests or a cross-surface full harness sweep."
domain: "testing"
confidence: "high"
source: "scripts/ui-harness/SKILL.md"
---

# UI harness

Use this skill for a **single UI-harness run**: browser UI validation, persona
browser-flow evidence capture, or investigation of a UI-reported issue. It is not the
combined launcher; do not replace the API or MCP harnesses, or orchestrate a full
cross-surface sweep here.

Read and follow the detailed CLI contract in `scripts/ui-harness/SKILL.md`. Invoke its
actual `node scripts/ui-harness/agent-driver-ui/tools.mjs` commands rather than
recreating browser steps with another Playwright interface.

The auth pattern is intentionally human-in-the-loop: run `login --base-url <url>` in a
headful browser, let the user complete OAuth, then use the locally stored, git-ignored
storage state for the headless `init`, action, and `finish` commands. Never automate the
login flow or expose the storage-state file.
