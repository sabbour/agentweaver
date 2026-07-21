---
name: "agentweaver-ui-harness"
description: "Run Agentweaver's deployed-UI harness for browser evidence, repros, or plain-English scenarios. For Agentweaver UI only, not generic Playwright automation; use combined harness for full sweeps."
domain: "testing"
confidence: "high"
source: "scripts/ui-harness/SKILL.md"
allowed-tools: Bash(node scripts/ui-harness/agent-driver-ui/tools.mjs:*) Bash(npm --prefix scripts/ui-harness:*)
---

# UI harness

Use this skill for a **single UI-harness run**: browser UI validation, persona
browser-flow evidence capture, or investigation of a UI-reported issue. It is not the
combined launcher; do not replace the API or MCP harnesses, or orchestrate a full
cross-surface sweep here.

Read and follow the detailed CLI contract in `scripts/ui-harness/SKILL.md`. Invoke its
actual `node scripts/ui-harness/agent-driver-ui/tools.mjs` commands rather than
recreating browser steps with another Playwright interface.

That contract now includes `list-scenarios` for the reviewed built-in UI catalog. Use
the separate `harness-scenarios` skill for cross-surface cataloging and persona
generation.

The auth pattern is intentionally human-in-the-loop: run `login --base-url <url>` in a
headful browser, let the user complete OAuth, then use the locally stored, git-ignored
storage state for the headless `init`, action, and `finish` commands. Never automate the
login flow or expose the storage-state file.

Before running, check `scripts/harness-shared/learnings.md` (surface: `ui` or `all`)
for already-known bugs, environment facts, and scenario-design notes so they aren't
rediscovered from source each run.
