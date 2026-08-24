# Blueprint — Trailhead Travel Studio — Narration Script
# Scenario: Product owner building a travel web app with AI agents
# Target runtime: ~4–5 min assembled (16 clean beats)
# Status: 16/22 beats clean ✅ | 6 beats blocked (2.5, 2.6, 4.4–4.7)

---

## Beat 0.0 — Hand off to secure sign-in
**File:** recordings/blueprint-demo/0.0-entra-sign-in-handoff.webm (2.19 MB)
**Duration:** ~15s

> *[No voiceover — silent Entra ID handoff, sets security tone]*

**Visual:** Sign-in page → Microsoft Entra ID button → SSO completes.

---

## Beat 1.1 — Create the project
**File:** 1.1-create-project.webm (1.37 MB)
**Duration:** ~12s

> "Name the project, describe what you're building. That's the entire brief. Agentweaver handles the rest."

**Visual:** New project dialog → "Trailhead Travel Studio" typed → created.

---

## Beat 1.2 — Choose a blueprint
**File:** 1.2-choose-blueprint.webm (1.50 MB)
**Duration:** ~12s

> "Blueprints are pre-cast teams for common product shapes. Pick the one that matches your stack and your team grows around it."

**Visual:** Blueprint gallery → Trailhead selected → confirm.

---

## Beat 1.3 — Inspect the team
**File:** 1.3-inspect-team.webm (3.59 MB)
**Duration:** ~18s

> "Meet your crew: a frontend builder, a backend engineer, a tester, and a coordinator keeping everyone on track. Fully staffed before you write a line."

**Visual:** Team panel → agent cards animate in → hover on each role.

---

## Beat 2.1 — Frame the product
**File:** 2.1-frame-product.webm (2.32 MB)
**Duration:** ~15s

> "Describe what you want in plain language. The coordinator reads it and breaks it into a concrete work plan — no tickets, no Jira."

**Visual:** Work order input → typed brief → coordinator begins planning.

---

## Beat 2.2 — Session running
**File:** 2.2-confirm-plan.webm (1.97 MB)
**Duration:** ~12s

> "The coordinator spins up, reads the brief, and proposes a plan. You see every step before agents touch a single file."

**Visual:** Run opens → work plan renders → coordinator status shows active.

---

## Beat 2.3 — Watch the work plan run
**File:** 2.3-watch-work-plan.webm (0.90 MB)
**Duration:** ~10s

> "Agents execute in parallel. Hero section, feature cards, CTA button — each piece assigned, each piece tracked."

**Visual:** Work plan items progress; completion indicators light up.

---

## Beat 2.4 — Review the board
**File:** 2.4-review-board.webm (0.27 MB)
**Duration:** ~8s

> "The board shows exactly where everything stands. Nothing falls through the cracks."

**Visual:** Kanban board → columns visible → task cards with status.

---

## Beat 2.7 — Review the trace
**File:** 2.7-review-trace.webm (1.79 MB)
**Duration:** ~14s

> "Full observability, out of the box. Every agent call, every model invocation — traced, timestamped, searchable."

**Visual:** Observability trace view → spans visible → hover on agent steps.

---

## Beat 2.8 — Review team memory and decisions
**File:** 2.8-review-memory.webm (1.10 MB)
**Duration:** ~12s

> "Agents remember. Design decisions, constraints, past choices — all stored, all searchable, all carry forward to the next run."

**Visual:** Memory panel → decision entries → hover on entries.

---

## Beat 3.1 — Schedule recurring dependency sweeps
**File:** 3.1-schedule-sweep.webm (2.78 MB)
**Duration:** ~16s

> "Set it once, forget it. Agentweaver wakes up on a schedule, checks your dependencies, and surfaces anything that needs attention."

**Visual:** Workflow scheduler → cron configured → next run preview.

---

## Beat 3.2 — Trigger bug triage from GitHub
**File:** 3.2-github-triage.webm (2.24 MB)
**Duration:** ~15s

> "A new GitHub issue triggers the coordinator automatically. It reads the report, classifies the bug, and routes it — no human in the loop."

**Visual:** GitHub issue → webhook → coordinator run starts → triage result.

---

## Beat 4.1 — Chat triage — read and dispatch
**File:** 4.1-chat-triage.webm (2.01 MB)
**Duration:** ~50s (trim pause to ~15s in post)

> "Or just ask. Type what you need — the assistant reads the issue, decides what to do, and kicks off the right workflow."

**Visual:** Assistant chat → user types triage request → Enter → coordinator processes.

---

## Beat 4.2 — Coordinator triages the bug
**File:** 4.2-assistant-triage.webm (0.24 MB)
**Duration:** ~8s

> "Your message lands. The coordinator has it."

**Visual:** Transcript shows user message confirmed → hover.

---

## Beat 4.3 — Scope the bug
**File:** 4.3-scope-bug.webm (0.22 MB)
**Duration:** ~8s

> "The workflow is in motion."

**Visual:** "workflow" text in transcript → hover.

---

## Beat 5.1 — Drive from your own tools
**File:** 5.1-drive-from-tools.webm (3.59 MB)
**Duration:** ~18s

> "Agentweaver speaks MCP. Your IDE, your CLI, your AI assistant — they all connect to the same team. Work the way you already work."

**Visual:** Account settings → MCP endpoint → hover on URL.

---

## Assembly Notes
- Total clean beats: 16 (of 22)
- Assembly order: 0.0 → 1.1 → 1.2 → 1.3 → 2.1 → 2.2 → 2.3 → 2.4 → 2.7 → 2.8 → 3.1 → 3.2 → 4.1 → 4.2 → 4.3 → 5.1
- **Blocked beats — require staging deploy + user action:**
  - 2.5: needs `npm run azure:deploy-from-local` from dev branch (session-approval-gate)
  - 2.6: needs GitHub PR from coordinator run in agentweaver-demo-dryrun
  - 4.4: needs multi-agent Bug Fix workflow run with topology-graph-canvas
  - 4.5: needs staging deploy + session-approval-gate
  - 4.6: needs AGENTWEAVER_DEMO_GITHUB_BUGFIX_PR_URL set in .env.local
  - 4.7: same as 4.6
- Beat 4.1 is 50s raw — trim the 30s pause to ~12s in editing
- Add 0.5s cross-dissolve transitions between all beats
- Lower thirds: "Trailhead Travel Studio" at beat 1.1
- End card: agentweaver.dev at beat 5.1
