# Agentweaver MCP Test Harness Plan

_Last updated: 2026-07-14 — author: Morpheus (Runtime Engineer)_

> **Status: design spec only.** No code in this plan is built yet. This document
> specifies a **third** persona-driven validation harness — the **MCP harness** —
> that sits alongside the existing **API harness** (`scripts/persona-harness/`,
> being extended by Tank and renamed to `scripts/api-harness/` under the naming
> convention below) and the planned **Playwright UI harness** (`scripts/ui-harness/`,
> spec'd by Trinity in `docs/ui-test-harness-plan.md`). All three drive the **same
> personas** through **different surfaces** and feed a **shared judge**. See
> [Cross-Harness Shared Layer](#cross-harness-shared-layer).
>
> **Naming convention:** harnesses are named `{surface}-harness` (`api-harness`,
> `ui-harness`, `mcp-harness`) — by the **surface** they test, not by the fact that
> they use personas. Persona generation/authoring is an orthogonal concern that lives
> **exclusively** in the shared `scripts/persona-briefs/` package all three consume.

## The full vision — a self-improvement feedback loop, not three test suites

The three harnesses are **not** independent test suites. Together they form a
**self-improvement feedback loop** whose purpose is to **replace manual bug-hunting** —
today's loop of Ahmed launching the app and reporting bugs, or the coordinator running
ad hoc API calls that have to be re-described each session. To make that loop
autonomous, **all three stages of the pipeline must be LLM/model-driven**, not just the
middle one:

1. **Persona generation** (the *inputs*) — personas must be **LLM-generatable on
   demand** (new Jobs-To-Be-Done variations), not limited to the hand-authored
   jordan/maya/priya set. See
   [Shared persona / brief format](#1-shared-persona--brief-format--define-personas-once-surface-agnostically).
2. **Persona behavior** (the *driving*) — already covered by this harness's
   LLM-in-the-loop MCP tool-call selection (a fresh LLM decides each turn live from
   real tool results). See [Architecture](#architecture).
3. **Judging** (the *evaluation*) — the shared judge must render more than P0/P1
   pass/fail; it must also assess a **frustration level** (an emotional/UX signal) from
   the transcript evidence. See
   [Judge architecture](#2-judge-architecture--recommendation-one-shared-judge-core--thin-mcp-evidence-adapter-option-a).

**Division of responsibility across the three surfaces.** The **API harness** tests
core backend functionality **in isolation** — it is the **ground-truth layer** (did the
orchestration mechanically do the right thing, independent of any client's ergonomics).
The **MCP and UI harnesses** primarily test the **experience layer** — is the platform
**usable, discoverable, and non-frustrating** for an MCP client (or a browser user),
not merely "did the JSON-RPC call succeed." When an MCP/UI harness surfaces a
usability/design problem, it is **cross-referenced against the API harness's findings
for the same persona/scenario** to determine whether it traces back to a real
API/backend defect (a ground-truth failure) or is purely an experience-layer issue
living in the MCP/UI surface itself. This cross-reference is exactly what the **shared
verdict schema + cross-surface meta-aggregation** (Option (a)) makes possible.

---

## Goal

Continuously validate Agentweaver's **MCP (Model Context Protocol) surface** — the
conversational/CLI operator-agent workflow that MCP clients (GitHub Copilot CLI,
VS Code, any MCP host) use to drive the platform **through MCP tool calls** rather
than raw REST or a browser. The MCP surface is the seam through which "type anything
and an agent drives Agentweaver" (#50/#201) actually runs, and the epic **#295**
("reliable CLI and conversational operator workflows") has no automated end-to-end
guard today (that gap is exactly **#131**).

The MCP harness applies the **same brief-driven, LLM-in-the-loop, mandatory-pushback,
driver-only, judge-separated** architecture the API harness converged on — but each
"turn" is an **MCP tool call** (`agentweaver-run_submit`, `agentweaver-run_status`,
`agentweaver-coordinator_outcome_spec_revise`, …) issued over the JSON-RPC MCP
protocol, and the captured evidence is **MCP protocol-level** (tool name, arguments,
structured content result, `isError`, JSON-RPC error codes like `-32001`, tool-loop
traces) rather than HTTP request/response or DOM snapshots.

---

## Goals / Non-Goals

### Goals

1. **Exercise the real MCP surface an MCP client sees** — drive Agentweaver by
   invoking the published `agentweaver-*` MCP tools over the MCP transport (HTTP
   streamable and/or stdio), authenticated exactly the way Copilot CLI authenticates
   (OAuth AS-minted bearer or GitHub-token passthrough — see
   [Auth & session model](#auth--session-model)), not by calling `/api/*` directly.
2. **Validate the MCP contract, not just the backend behind it.** Regressions
   unique to the MCP layer — tool schema drift, non-actionable error strings
   (#129), missing `run_task` one-call path (#130), `agentweaver.agent.md` driver
   guidance gaps (#128) — must be caught here, because the API harness (which bypasses
   MCP) is structurally blind to them.
3. **Be brief-driven and LLM-in-the-loop**, not a fixed script: a fresh-context LLM
   decides each persona turn (which tool to call next, with what arguments) live from
   the **real MCP tool results** it has seen so far, and must **push back ≥2 times**
   per run, grounded in real returned content.
4. **Be a pure driver.** Capture deterministic, verbatim MCP evidence (tool called,
   arguments, result content, `isError`, protocol error code, timing, tool-loop
   trace). Embed **zero** subjective "is this good?" heuristics. A separate LLM judge
   renders the quality verdict from the captured transcript.
5. **Share personas and a judge with the API and UI harnesses**, so the same Jordan/
   Maya/Priya scenario can be compared across API vs UI vs MCP surfaces
   (cross-surface meta-aggregation).
6. **Provide the #131 CLI→MCP smoke test** as a fast, deterministic subset (P0-only)
   that runs in CI, plus the deeper LLM-driven scenarios for quality signal.

### Non-Goals

- **Not** re-driving the raw REST API — that is the API harness's job. If a bug is in
  the backend and reproduces identically via REST, it belongs to the API harness; the
  MCP harness owns bugs that live in or are shaped by the **MCP layer**.
- **Not** a Playwright/browser surface — that is Trinity's UI harness. The MCP harness
  never opens a browser (the one exception: `github_signin` device-flow requires a
  human to visit a verification URL once; see Auth).
- **Not** building the backend conversational operator-agent (#201) itself. The
  harness is a client of the MCP surface; #201 is a server feature. Once #201 ships a
  natural-language operator run type, the same brief/pushback architecture extends to
  it (noted as forward-looking scope, not built now).
- **Not** embedding pass/fail quality judgment in the driver (driver-only, same hard
  rule as the other two harnesses).
- **Not** running the release pipeline. The harness validates; only the coordinator
  (Squad) cuts releases.

---

## MCP Surface Investigated

Findings below are from reading the live server source (`apps/Agentweaver.Mcp/`),
the driver instructions (`.github/agents/agentweaver.agent.md`), the generated tool
index (`docs/reference/mcp-tools.md`), and epic **#295** + children.

### Transport & protocol shape

- The MCP server (`apps/Agentweaver.Mcp/Program.cs`) is built on the .NET
  `ModelContextProtocol` SDK (`AddMcpServer().WithToolsFromAssembly()`), exposing
  tools discovered from `[McpServerTool]` attributes across
  `apps/Agentweaver.Mcp/Tools/*.cs`.
- Two transports:
  - **stdio** (`--stdio`): single-user/local. The server forwards the caller's own
    bearer to the backend; **no JWT validation** is performed (local trust). Falls
    back to a shared `AGENTWEAVER_API_KEY` only when there is no inbound token.
  - **HTTP streamable** (`app.MapMcp("/mcp")`): the hosted staging surface. Runs in
    **stateless** mode (`WithHttpTransport(o => o.Stateless = true)`) so each tool
    call executes in its own HTTP scope and the caller's bearer (captured by
    `McpBearerTokenMiddleware`) flows into the tool. (The stateful transport left
    `IHttpContextAccessor.HttpContext` null during tool calls and 401'd every call —
    a real bug already fixed; the harness must exercise the stateless HTTP path that
    Copilot CLI actually hits.)
- Tools return a **JSON string** as their content (`JsonSerializer.Serialize(...)`);
  errors are raised as `McpApiException(statusCode, message)`, surfaced to the client
  as MCP tool errors. Long-running tools (`run_watch`, `github_signin`) stream
  `ProgressNotificationValue` progress notifications.
- **90 tools across 14 categories** are published today (authoritative list:
  `docs/reference/mcp-tools.md`, generated from source). The categories the harness
  must cover: **GitHub Auth, Project, Blueprint, Catalog, Coordinator, Run, Backlog,
  Team, Workflow, Memory, Diagnostics, Workspace, Skill, Sandbox Policy.**

### The tools that matter for the common operator workflow

| Category | Tools the harness drives | API-harness analog |
|---|---|---|
| Auth | `github_status`, `github_signin`, `github_signout` | (bearer supplied out-of-band) |
| Session | `session_start`, `session_current`, `session_update` | (implicit) |
| Discovery | `project_list`, `list_blueprints`, `catalog_list_scenarios`, `catalog_list_roles`, `workflows_list` | `list-blueprints`, `get-team` |
| Project | `project_create`, `project_get`, `project_configure` | `create-project` |
| Run / coordinator | `coordinator_start`, `coordinator_outcome_spec_get`, `coordinator_outcome_spec_revise`, `coordinator_outcome_spec_confirm`, `run_submit`, `run_status`, `run_watch`, `coordinator_work_plan_get`, `coordinator_children_get`, `orchestration_topology`, `coordinator_steer` | `submit-goal`, `get-spec`, `revise-spec` (**the pushback lever**), `confirm-spec`, `steer`, `get-events` |
| Approvals / review | `run_review`, `start_preview` (+ shell/tool approvals surfaced via events) | `check-approvals`, `resolve-approval` |
| Results | `run_show_artifacts`, `run_get_file` | (event/spec inspection) |
| Backlog | `backlog_capture_task`, `backlog_move_to_ready`, `send_all_backlog_to_ready`, `backlog_get_board` | (n/a) |
| Health | `diagnostics_get`, `heartbeat_status` | (n/a) |

**Key mapping:** the API harness's `submit-goal → get-spec → revise-spec (pushback)
→ confirm-spec` sequence maps almost 1:1 onto MCP's `coordinator_start →
coordinator_outcome_spec_get → coordinator_outcome_spec_revise →
coordinator_outcome_spec_confirm`. The **pushback lever** on the MCP surface is
`coordinator_outcome_spec_revise` (scoping rung) or `coordinator_steer` (deeper rung).
This 1:1 mapping is why persona briefs can be **surface-agnostic** and reused verbatim.

### Auth & session model

Investigated in `McpBearerTokenMiddleware.cs` + `AgentweaverApiClient.cs` +
`GitHubAuthTools.cs`:

- The hosted MCP endpoint is an **OAuth 2.0 protected resource** (RFC 9728). It
  publishes `/.well-known/oauth-protected-resource` (and `/mcp`-suffixed) metadata
  pointing at the Authorization Server; unauthenticated calls get a `401` with a
  `WWW-Authenticate` challenge advertising the resource-metadata URL. This is the
  exact discovery dance Copilot CLI / VS Code perform.
- **Two accepted bearer types:**
  1. **Agentweaver-minted OAuth access token** (signed JWT) — validated **offline**
     against the AS JWKS (`iss`/`aud`/`exp`/RS256) by `McpAccessTokenValidator`. The
     backend then does the authoritative `jti`-denylist + per-user org enforcement.
  2. **Raw GitHub OAuth token** (transitional passthrough) — validated by calling
     `GET https://api.github.com/user`, cached 5 min. Gated by
     `Auth:Mcp:AllowGitHubPassthrough` (default on).
- Either way, the resolved identity + raw bearer are stashed in `HttpContext.Items`
  and **forwarded to the backend API** by `AgentweaverApiClient` — so the backend sees
  the real caller, not a service identity. **This is the critical difference from the
  API harness:** the API harness supplies its own `gh auth token` bearer directly to
  `/api/*`; the MCP harness must obtain a bearer the **MCP server** accepts and let
  the MCP server forward it. For staging validation the harness will use the
  **GitHub-token passthrough** path (a `gh auth token`), which is the simplest bearer
  the hosted `/mcp` currently accepts.
- **In-band auth tools:** `github_signin` runs the GitHub **device flow** and streams
  a `user_code` + `verification_uri` as progress; a human enters the code once, then
  the tool polls to completion. `github_status` reports signed-in state;
  `session_start`/`session_current` establish the operator session. The
  `agentweaver.agent.md` "auth-first" rule (call `github_status`, and on 401 run
  `github_signin` + `session_start` before retrying) is itself part of what #128 wants
  hardened — the harness will drive and **assert** that recovery path.

### What exists today vs. what's missing (per #295 and children)

| # | Title | State today | What the harness validates |
|---|---|---|---|
| **#295** | Epic: reliable CLI + conversational operator workflows | 5 open children | Umbrella — the harness is the end-to-end guard for the whole epic |
| **#128** | Harden `agentweaver.agent.md` driver instructions (auth-first, `run_watch`, retry, full sequence) | Partially present in `.github/agents/agentweaver.agent.md` today; edge/recovery guidance incomplete | Drive the documented sequences (auth-first recovery, timeout retry, backlog→run) and assert each works from the tools alone |
| **#129** | Make all MCP tool errors actionable (`{error, hint}` shape) | **Not implemented** — tools throw `McpApiException(statusCode, message)` with raw pass-through messages; no `hint` field | Deliberately trigger each error class (signed-out, 404 project, 409 wrong-state, workflow-not-allowed, `-32001` timeout, sandbox-not-bound) and capture the raw error content for the judge to score against the #129 target table |
| **#130** | `run_task` single-call submit→poll→results tool | **Not implemented** — no `run_task`/`run_quick` tool; the happy path is 7–10 chained calls | Once shipped, drive the one-call path and assert it surfaces review gates / steering questions instead of silently skipping. Until then, the harness documents the multi-call baseline it replaces |
| **#131** | CLI→MCP end-to-end smoke test | **Not implemented** — no automated MCP path test | **This harness's deterministic P0 subset IS #131** (signin → project → submit → poll → artifacts → cleanup), runnable in CI |
| **#201** | Backend conversational operator-agent run type | **Deferred** (Trinity's #201 investigation recommended deferring) | Forward-looking: when shipped, the same brief/pushback loop drives natural-language operator turns; not built now |

> **Investigation note:** #129, #130, #131, and the `run_task` entry point are all
> **not yet implemented**. The harness must therefore be written to (a) validate the
> current multi-call reality, and (b) grow assertions for `run_task`, the `{error,
> hint}` shape, and the #128 hardened sequences **as they land** — the harness is both
> the validator and the acceptance-test author for #295's children.

---

## Architecture

The MCP harness reuses the API harness's proven shape (`briefs/` → LLM-in-the-loop
driver → verbatim transcript → separate judge) with the surface swapped from REST to
MCP. It is a **new, sibling** harness, not a modification of the API harness
(`scripts/persona-harness/`, renaming to `scripts/api-harness/`).

### 1. How a persona brief drives MCP tool calls, turn by turn

- A fresh-context LLM (a sub-agent, or any model with shell access) is handed **only**
  a persona **brief** (goals, constraints, voice, the mandatory-pushback instruction —
  the exact same brief the API and UI harnesses use) plus a short "you are driving
  Agentweaver via its MCP tools" preamble that lists the available tools and the MCP
  result shapes it will see.
- Each **turn** = the driving LLM choosing the next **MCP tool call** and its
  arguments, based on the persona brief + the **real MCP tool results** seen so far —
  never a pre-written both-sides script. This mirrors `agent-driver/tools.mjs`'s
  turn-by-turn model, but the discrete tools the LLM invokes are thin wrappers that
  perform an **MCP JSON-RPC `tools/call`** instead of an HTTP request.
- **Pushback** = the LLM, having read a real MCP result (a drafted outcome spec from
  `coordinator_outcome_spec_get`, a work plan, a review diff), decides the persona
  would object and issues a real `coordinator_outcome_spec_revise` (scoping rung) or
  `coordinator_steer` (deeper rung) tool call carrying the objection — not free-text
  chat, not a scripted complaint. Mandatory **≥2 grounded pushbacks** per run, decided
  in the moment.
- **Two rungs**, matching the API harness's safety model:
  - **Scoping rung (default, safe):** drive to the `coordinator_outcome_spec_confirm`
    gate and **stop** — never confirm. Nothing is scaffolded/containerized/deployed.
    This is the `#131` smoke-test rung plus quality judging of the drafted spec.
  - **Deeper rung (opt-in, flagged):** confirm the spec and drive through dispatch →
    work plan → in-loop approvals (`run_review`, tool/shell approvals) → preview
    (`start_preview`, with the **mandatory live `curl` of `preview_url` before
    approving** rule carried over from the E2E plan) → completion → artifacts
    (`run_show_artifacts` / `run_get_file`). Behind an explicit `--deep` flag.

**Persona-realistic gate review (when NOT auto-approved).** A real human operator does
not fire-and-forget a run and only check the final status — they engage with the gates
Agentweaver raises. So when the deeper rung runs **without** auto-approve, the persona
must **actually validate gate content before deciding** — read the diff / work plan /
review output / preview at that gate through the persona's JTBD lens — rather than
blindly approving every time. This follows the **same DETECT → JUDGE → EXECUTE pattern
Tank already built for the API harness** (judge-gated approval driving, commit
`b4ac1104`): the driver structurally **detects** the pending gate (from the events feed
/ tool results: `tool.approval_required`, `coordinator.child_approval_required`,
`shell.approval_required`, or a `run_review`-eligible state) and **packages that one
gate's evidence**; the driver's **LLM (acting as the persona)** decides
**approve / request-changes / defer** based on what it is shown; the driver then
**executes** exactly that decision against the real MCP tools (`run_review`, the
tool/shell approval/denial tools). This is fully consistent with the driver-not-debug
boundary: the LLM is **reacting as a user would to what it's shown**, not diagnosing
platform internals or root-causing a bug.

Beyond binary approve/reject, the persona should also exercise **human-review-style
feedback** where the gate supports it — e.g. request changes with a short note ("this
also needs to handle X") via `coordinator_steer` / the review request-changes path —
because that is a real interaction pattern Agentweaver supports and the harness should
drive it, not just the two-button path.

> **Scope boundary — do NOT over-index on this (functional correctness, not output
> grading).** The point of persona gate-review is to exercise the **mechanism** end to
> end — does approve / request-changes / defer actually work, does the run progress
> correctly through the DAG afterward, do notifications fire, does a requested change
> re-enter the right stage — **not** to make the persona a quality bar for the agents'
> generated code/design. The persona's review feedback stays **realistic-but-lightweight**
> (enough to meaningfully drive the request-changes path once or twice), never an
> elaborate code-review rubric demanding perfect output. Correspondingly, the **judge**
> criteria for these turns stay on **"did the platform mechanics work"** (P0) — *not*
> "was the AI's output good." (Output quality is still judged as P1 for the drafted
> spec, but gate-review turns specifically test functional correctness of the gating
> machinery.)

### 2. MCP transport client

The harness needs a minimal **MCP client** (not the full backend). Two supported
targets, selectable by flag, so we test both real client paths:

- **`--target http`** (primary, staging): speak MCP **streamable HTTP** JSON-RPC to
  `https://<staging-host>/mcp`, performing the RFC 9728 discovery + bearer auth exactly
  as Copilot CLI does. Bearer obtained via `gh auth token` (GitHub passthrough path).
- **`--target stdio`** (secondary, local/CI): launch the MCP server with `--stdio`
  and speak JSON-RPC over stdio — the simplest deterministic path for the #131 CI
  smoke test (no OAuth, forwards the local bearer).

Implementation options for the client, in preference order: (a) the official
`@modelcontextprotocol/sdk` TypeScript/JS client (matches the Node ESM stack the API
harness already uses), or (b) a thin hand-rolled JSON-RPC-over-HTTP/stdio client if we
want zero new deps. **Recommendation: (a)** — using the real MCP SDK client means the
harness exercises the same framing/negotiation an actual MCP host uses, catching
protocol-level regressions a hand-rolled client would paper over.

**Driver performance / interaction model (applies to all three harnesses).** The MCP
driver is **headless-first, low-touch, and parallel by design** — built to run **many
personas/scenarios concurrently**, not one at a time, and to operate **autonomously
without requiring user interaction**. Ahmed should be able to **observe** a run if he
wants — an **optional** live transcript tail, streaming turn-by-turn stdout, or a status
view — but observation is never a required interactive step; the default is unattended
fan-out (the coordinator dispatches N persona sessions as background agents, mirroring
the E2E plan's "use Fleet to parallelize as much as possible" rule). Concretely: each
run owns an isolated harness `sessionId`, its own transcript file, and its own MCP
client instance, so runs never share mutable state.

**Transport choice ↔ parallel-run feasibility.** The two targets differ on how many
sessions can run concurrently:

- **`--target http`** (staging): N persona sessions **can** run concurrently against
  the **same** base URL without state collision, because the hosted `/mcp` is
  **stateless** (`WithHttpTransport(o => o.Stateless = true)`) — each `tools/call`
  carries its own bearer and executes in its own HTTP scope, with no server-side
  session affinity to collide on. The only shared-state caveats are *backend* resources
  the personas create (projects/runs) — so each concurrent session must create its
  **own** project (unique name) rather than reusing a shared one, keeping run/project
  IDs disjoint. This is the recommended path for large concurrent fan-out. One auth
  caveat: interactive `github_signin` device flow is per-identity and human-gated, so
  concurrent unattended runs should share a **pre-provisioned bearer** (one `gh auth
  token`) rather than each triggering a device flow.
- **`--target stdio`** (local/CI): each session spawns its **own** `--stdio` server
  process, so parallelism is process-level (N processes) and naturally collision-free,
  but heavier per-session (one server process each). Best for a bounded CI matrix and
  the #131 smoke test; the HTTP target scales further for big concurrent sweeps.

### 3. Driver-only evidence capture (the hard constraint)

Same rule as the other two harnesses: **the driver captures and executes; it never
judges.** The driver's LLM-in-the-loop role is **exclusively to choose the PERSONA's
next action** from the brief + the responses it has observed (i.e. it *simulates the
user*) — it **never** diagnoses why something failed, classifies a root cause, or
decides whether a failure is "real"; **all** interpretation, debugging, and root-cause
judgment is the judge's job alone, working from the evidence bundle the driver hands
off. When a run misbehaves, the driver's LLM reacts **as the persona would** (retry,
push back, get confused, or abandon) and records that reaction verbatim — it does not
step out of character to analyze the platform. Each turn is recorded verbatim into an
**MCP transcript** with a turn shape that parallels the API harness's `TranscriptTurn`
but carries MCP-native fields:

```jsonc
// agentweaver.mcp-transcript/v1  (one turn)
{
  "n": 4,
  "at": "2026-07-14T18:03:11Z",
  "sessionId": "mcp-<uuid>",        // correlates all turns of one run
  "traceId": "<from result _meta / response header, if server emits one>",
  "actor": "persona",               // persona decision vs system read
  "thought": "Jordan wants to see a deploy+smoke-test step; the drafted spec stops at scaffold — object.",
  "toolName": "coordinator_outcome_spec_revise",   // the MCP tool called
  "toolArguments": { "run_id": "…", "feedback": "…" }, // verbatim args
  "mcp": {
    "requestId": 12,                // JSON-RPC id
    "isError": false,               // MCP tool-result isError flag
    "protocolErrorCode": null,      // JSON-RPC error code (e.g. -32001 timeout), if any
    "structuredContent": { … },     // parsed tool result (the JSON the tool serialized)
    "rawContent": "…"               // verbatim result text, lossless
  },
  "latencyMs": 812,
  "outcome": { "ok": true, "isError": false, "protocolErrorCode": null },
  "note": "revised spec now includes container + AKS deploy + smoke-test nodes"
}
```

Deterministic facts the **driver** may assert (these are objective, not quality
judgments), analogous to the API harness's `p0Objective`/`platformChecks` block:

- every driving tool call returned `isError:false` with no JSON-RPC error code;
- the outcome spec left `drafting` and settled (via `coordinator_outcome_spec_get`);
- the **mandatory ≥2 pushbacks** were each **applied** (the revise/steer tool call
  succeeded and the spec/plan actually changed);
- **schema/shape validation** of tool results (e.g. `run_submit` returned a `run_id`;
  `run_show_artifacts` returned a files array) — structural, not "is it good".

Everything subjective — did the spec actually cover the persona's needs, did the
system improve in response to each pushback, was an error message actually actionable
(#129) — is captured verbatim and left to the judge.

### 4. Auth/session handling in the harness

- `--target http`: on startup the harness (a) obtains `gh auth token`, (b) performs
  the MCP `initialize` handshake, (c) resolves the RFC 9728 protected-resource
  metadata, (d) attaches `Authorization: Bearer <token>` to every `tools/call`. If the
  server returns a 401/`invalid_token` challenge, the harness records it verbatim (it
  is itself evidence for #128's auth-first requirement) and, for the driven persona,
  the LLM may call `github_status` → `github_signin`. Device-flow `github_signin`
  needs a one-time human code entry; for unattended CI we use `--target stdio` (no
  OAuth) or a pre-provisioned token.
- `--target stdio`: the local bearer/`AGENTWEAVER_API_KEY` is forwarded; no device
  flow. This is the deterministic CI path (#131).
- Session tools (`session_start`/`session_current`) are driven as first-class turns so
  the harness validates the operator-session lifecycle the `agentweaver.agent.md`
  auth-first rule depends on.

### 5. Judging

The harness **assembles** a judge prompt from the captured MCP transcript + the
persona's authored criteria + the shared JUDGE playbook, and a **separate LLM judge**
renders the verdict. The verdict uses the **shared** `agentweaver.persona-judge-
verdict/v1` schema (see [Cross-Harness Shared Layer](#cross-harness-shared-layer)) —
the MCP harness contributes an **evidence adapter** that normalizes MCP turns into the
same digest shape the shared judge consumes. No MCP-specific verdict schema.

---

## Directory / File Layout Proposal

A **new sibling** package — nothing under the API harness's current
`scripts/persona-harness/` is modified (Tank owns those files; it is renaming to
`scripts/api-harness/`). Proposed root: **`scripts/mcp-harness/`**.

```
scripts/mcp-harness/
  package.json                 # Node ESM, "type":"module"; dep: @modelcontextprotocol/sdk
  README.md                    # what it is, how to run, the two rungs, the two targets
  JUDGE.md                     # MCP-specific judge ADDENDUM only — points at the SHARED
                               #   JUDGE playbook; documents the MCP evidence taxonomy
                               #   (isError, JSON-RPC error codes, tool-loop traces) and
                               #   the #129 actionable-error rubric. Does NOT fork the
                               #   P0/P1/CANNOT_DETERMINE method.
  mcp-client/
    client.mjs                 # thin wrapper over @modelcontextprotocol/sdk: initialize,
                               #   RFC9728 discovery, tools/call, progress handling
    transport-http.mjs         # streamable-HTTP transport + bearer auth (staging)
    transport-stdio.mjs        # stdio transport (local/CI, launches server --stdio)
  agent-driver/
    tools.mjs                  # LLM-in-the-loop DRIVER tool surface — MCP analog of the
                               #   API harness's agent-driver/tools.mjs. Discrete tools:
                               #   init / list-blueprints / create-project / submit-goal /
                               #   get-spec / revise-spec (pushback) / steer /
                               #   check-approvals / resolve-approval / finish.
                               #   Each performs an MCP tools/call and records a turn.
    AGENT.md                   # the driving-LLM preamble: "you drive Agentweaver via MCP
                               #   tools; here are the tools + result shapes; obey the brief"
  lib/
    transcript.mjs             # MCP transcript writer (agentweaver.mcp-transcript/v1)
    evidence-adapter.mjs       # normalizes MCP turns -> the SHARED judge digest shape
    mcp-p0.mjs                 # deterministic P0 block (isError/error-code/schema/pushback)
    reporter.mjs               # DRIVE+CAPTURE OK / DRIVER P0 FAIL banner (never PASS/FAIL)
  smoke/
    mcp-cli-smoke.mjs          # #131 deterministic smoke test (P0-only, stdio target):
                               #   signin/token -> project -> submit -> poll -> artifacts -> cleanup
  transcripts/  .gitignore     # captured runs (git-ignored, like the API harness)
  verdicts/     .gitignore     # judge verdict blocks for meta-aggregation
  findings/     .gitignore
```

**Shared, not copied:** `briefs/` and the judge core are **not** duplicated here — they
live in the shared package (below) and are imported by all three harnesses. This
harness's `JUDGE.md` is a thin **addendum** documenting only the MCP evidence taxonomy.

`package.json` scripts (mirroring the API harness's `node --test` convention):

```jsonc
{
  "scripts": {
    "test": "node --test",
    "smoke": "node smoke/mcp-cli-smoke.mjs",   // #131, wireable as npm run test:mcp-smoke at repo root
    "judge": "node ../persona-briefs/judge/assemble.mjs"  // shared judge assembler
  }
}
```

---

## Coverage Mapping (which issues this harness validates, and how)

| Issue | How the MCP harness validates it |
|---|---|
| **#295** (epic) | End-to-end guard for the whole "reliable CLI + conversational operator" epic. The deterministic smoke rung proves the happy path stays working release-over-release; the LLM-driven rung proves quality/actionability across varied personas. Meta-aggregation across MCP runs surfaces invariants (candidate P0 guarantees) and divergences (P1 signal) for the epic. |
| **#131** (CLI→MCP smoke test) | **Directly implemented** by `smoke/mcp-cli-smoke.mjs`: signin/token → create/reuse project → `run_submit` (smallest task, fast blueprint) → `run_status` poll to terminal (≤5 min) → `run_show_artifacts` asserts ≥1 artifact → `run_archive` cleanup. Runs on `--target stdio` in CI, `--target http` against staging. Failure output names the failing step + tool + raw error — satisfying #131's "actionable failure output" AC. |
| **#129** (actionable errors) | A dedicated **error-probe scenario** deliberately triggers each error class from #129's table — signed-out call, `project_get` on a bad id (404), `run_review` on a not-reviewable run (409), submit a workflow not in `allowed_workflow_ids` (400), induce `-32001` timeout, hit a sandbox-not-bound 409 — and captures each raw error result verbatim. The judge scores each against the #129 target ("does it say what went wrong, why, and the next tool to call; is there a `hint`?"). Because the driver embeds no heuristics, this becomes a living acceptance test for #129 as it's implemented. |
| **#130** (`run_task` one-call path) | Until shipped: the harness documents/measures the multi-call baseline (turn count from goal→results) as the regression `run_task` must beat. After shipped: a scenario drives `run_task` directly and the driver asserts it (a) returns `run_id`+`status`+`artifacts` in one call, (b) **surfaces** review gates / steering questions (`awaiting_review`) rather than silently skipping, (c) respects the timeout returning partial state — all structural/deterministic driver checks; the judge assesses whether the one-call result is actually usable. |
| **#128** (hardened `agentweaver.agent.md` driver instructions) | The harness drives the exact sequences #128 documents and asserts they're navigable **from the tools + agent.md alone**: auth-first recovery (force a 401 → `github_status` → `github_signin` → retry), `run_watch` long-poll behavior (assert it streams progress and doesn't look like a hang without explanation), timeout retry (`-32001` → `diagnostics_get` → idempotent retry), the full submit→poll→review→artifacts sequence, and the backlog→ready→run flow. Gaps the driving LLM hits (it got stuck, guessed an id, couldn't recover) are captured as evidence that the driver instructions need hardening. |
| **#201** (conversational operator-agent run type) | Forward-looking. When #201 ships a natural-language operator run, the same brief/pushback loop drives real conversational turns through that run type (text-in/text-out) instead of typed tool calls — the judge taxonomy carries over unchanged. Explicitly out of current scope (Trinity's investigation recommended deferring #201). |

---

## Cross-Harness Shared Layer

> This is the section Ahmed asked all three harness specs to converge on. Trinity is
> being asked the **same** questions for the UI harness; the recommendations below are
> written to be adopted identically by API, UI, and MCP so we end up with **one** set of
> personas and **one** judge, not three.

### 1. Shared persona / brief format — define personas ONCE, surface-agnostically

**Problem today:** personas exist in two coupled places — the authored specs
(`specs/personas/*.md`, "Success looks like" / "Failure signals") and the brief files
(`scripts/persona-harness/briefs/{jordan,maya,priya}.md`, goals/voice/constraints +
the mandatory-pushback instruction). The brief files are already **surface-agnostic in
spirit** (Jordan's brief talks about "get idea → app → container → deploy" and "push
back ≥2 times", never about REST specifically) — but they physically live inside the
API harness and reference "the real Agentweaver API".

**Recommendation:** extract the briefs into a **shared package all three harnesses
import**:

```
scripts/persona-briefs/            # shared, surface-agnostic — the single source of truth
  briefs/
    jordan.md   maya.md   priya.md   …   # goals, constraints, voice, MANDATORY ≥2 pushback
  generate/
    generate-brief.mjs       # LLM-driven brief GENERATOR — synthesize a new brief on demand
    brief-schema.mjs         # the surface-agnostic brief contract every generated brief must satisfy
  judge/
    JUDGE.md                 # the shared P0 / P1 / CANNOT_DETERMINE + FRUSTRATION playbook (surface-neutral core)
    verdict-schema.mjs       # agentweaver.persona-judge-verdict/v1 (canonical, shared)
    assemble.mjs             # judge-prompt assembler core (surface-agnostic)
    meta-aggregate.mjs       # cross-run + CROSS-SURFACE aggregation
  package.json               # imported by api-harness, ui-harness, mcp-harness
```

Each brief is written **surface-neutrally**: it states *what the persona wants* and
*that they must push back ≥2 times grounded in real responses*, and leaves *how a turn
is taken* to the harness. The MCP harness reads `jordan.md` and drives it via
`coordinator_outcome_spec_revise` tool calls; the API harness drives the same
`jordan.md` via `revise-spec` REST calls; the UI harness drives it via browser DOM
actions. A tiny **surface adapter** in each harness maps the abstract levers
("propose", "inspect draft", "push back", "confirm") onto its concrete surface.

**The shared package must support LLM-generated briefs, not just store hand-written
ones** (pipeline stage 1 of the self-improvement loop). `generate/generate-brief.mjs` is
an **LLM-driven generator**: given a seed (a JTBD theme, a target discipline, a
capability/seam to stress, or "a plausible new-user variation of Jordan"), it prompts a
model to synthesize a **new surface-agnostic brief** — a fresh persona with its own
goals, constraints, voice, and the mandatory ≥2-pushback instruction — conforming to
`brief-schema.mjs`. Generated briefs are the same shape as the hand-authored ones, so
**all three harnesses drive them with zero code changes**; the hand-authored
jordan/maya/priya set becomes the seed/exemplar corpus, not the ceiling. Generated
briefs may be persisted into `briefs/` (promoted after they prove useful) or driven
ephemerally for a single exploratory session. This keeps the harness fleet from
replaying the same three personas forever and lets it **probe the space of realistic
user intents autonomously** — the whole point of replacing manual bug-hunting. (An
existing `lib/generate-brief.mjs` already lives in the API harness as a starting point;
Phase 2 folds/generalizes it into the shared `generate/` module.)

- The authored `specs/personas/*.md` criteria stay where they are; the shared
  `assemble.mjs` resolves them exactly as `lib/judge.mjs` does today (parse the
  `specs/personas/<name>.md` link out of the brief).
- **Convergence note for Trinity:** this is the same location and the same brief files
  we both already cite; adopt `scripts/persona-briefs/` as the shared home so the UI
  and MCP harnesses import identical persona definitions rather than each forking a
  copy. Migration is a **move + re-point imports**, done without editing the API
  harness's driver logic (see Rollout).

### 2. Judge architecture — RECOMMENDATION: **one shared judge core + thin MCP evidence adapter** (option a)

**Recommendation: (a) — a single shared judge core** (one prompt library + one
canonical verdict schema across API, UI, and MCP), with a **thin MCP-specific evidence
adapter** that normalizes MCP protocol evidence into the shared judge's turn-digest
shape. **Not** a separate MCP judge.

**Reasoning:**

1. **The existing verdict schema is already surface-agnostic.**
   `agentweaver.persona-judge-verdict/v1` (in `lib/judge.mjs`) is
   `{p0, p1, pushback, cannotDetermine, findings}`:
   - **P0** = "objective orchestration mechanics" (auth accepted, project created,
     team assembled, run accepted, events flowed, spec settled, pushbacks applied).
     Every one of these is **surface-independent** — they're facts about the
     orchestration, observable through REST *or* MCP *or* DOM. MCP just observes them
     through tool results instead of HTTP bodies.
   - **P1** = "is the produced content good vs the persona's authored criteria" —
     judged from the **drafted outcome spec content**, which is identical bytes
     regardless of whether it was fetched via `coordinator_outcome_spec_get` (MCP),
     `GET /outcome-spec` (REST), or read off the DOM (UI). The quality question does
     not change with the surface.
   - **pushback / findings / CANNOT_DETERMINE** are likewise surface-neutral.
2. **MCP protocol failures fit P0 without a new taxonomy.** The one genuinely
   MCP-specific evidence class — JSON-RPC/protocol errors (`-32001` timeout, tool
   `isError`, transport/auth challenges) — maps cleanly onto **P0 platform-correctness**
   as an *additional deterministic mechanic*, exactly like HTTP status maps onto P0 for
   REST. It does **not** need a materially different verdict taxonomy; it needs a
   small **evidence adapter** that translates "MCP tool `isError:true` / error code
   `-32001`" into the same "a driving call failed → P0 fail, file it" signal the shared
   judge already understands. This is the concrete test Ahmed named ("unless MCP
   evidence can't fit a shared verdict schema"), and it **does** fit.
3. **Cross-surface meta-aggregation is the decisive argument.** The highest-value
   signal is comparing **the same persona/scenario across surfaces**: does Jordan's
   drafted spec come out equivalent whether driven via API, UI, or MCP? If MCP
   consistently produces a worse spec, or the `run_task` path silently drops a review
   gate the REST path surfaces, that is a **surface-specific defect** only a
   cross-surface aggregation can see — and it's only possible if all three emit the
   **same verdict schema** into one `meta-aggregate.mjs`. Three separate judges with
   three schemas make this comparison impossible. This alone justifies (a).
4. **#129 (actionable errors) is naturally a P1-style judgment** ("is this error
   message actionable?") that the shared judge can render given the raw error text —
   no separate judge needed, just the MCP evidence adapter feeding the raw error +
   the #129 rubric (carried in the MCP `JUDGE.md` addendum) into the shared prompt.

**Required schema addition — a `frustration` dimension (pipeline stage 3).** The
shared verdict schema must carry more than pass/fail; it must include a **frustration
level** as a **required field alongside `p0` and `p1`** — an emotional/UX assessment the
judge makes **from the transcript evidence**, capturing how frustrating the experience
would have been for the persona regardless of whether the run mechanically succeeded. A
run can be **P0-green and P1-PASS yet deeply frustrating** (the persona got there, but
only after fighting the surface) — that frustration is precisely the usability signal
the MCP and UI harnesses exist to surface, and it must not be lost in a binary verdict.
The canonical `agentweaver.persona-judge-verdict/v1` schema therefore extends to:

```jsonc
{
  "p0": { "verdict": "PASS | FAIL", ... },     // objective mechanics (unchanged)
  "p1": { "verdict": "PASS | PARTIAL | FAIL", ... },  // content quality (unchanged)
  "frustration": {                              // REQUIRED — emotional/UX assessment
    "level": "none | low | moderate | high | abandoned",  // ordinal, judge-assigned
    "evidence": "<transcript turn refs + one-line rationale>",
    "signals": [ "<the specific frustration signals observed>" ]
  },
  "pushback": { ... }, "cannotDetermine": [ ... ], "findings": [ ... ]
}
```

Because the field is **shared**, frustration is directly comparable across API/UI/MCP
for the same persona in `meta-aggregate.mjs` (e.g. "the same scenario is `low` via REST
but `high` via MCP" localizes a purely experience-layer defect). **For MCP
specifically, the frustration signals the judge should look for include:** excessive
retry / error-recovery turns (repeated `-32001` → `diagnostics_get` → retry loops); the
persona **abandoning a tool-call sequence** or backing out of a workflow; repeated
clarification requests or re-`get`-ing state because a prior result was unclear; a high
ratio of `isError:true` turns or non-actionable errors (#129) forcing guesswork; long
`run_watch` hangs with no explanation (#128); and the persona having to chain many tools
where one path should exist (the multi-call baseline #130's `run_task` is meant to
collapse). The API-harness adapter maps the equivalent signals from HTTP evidence and
the UI adapter from DOM/interaction evidence, so the ordinal is meaningfully comparable
across surfaces. The `JUDGE.md` core documents the frustration rubric; the MCP `JUDGE.md`
addendum lists the MCP-specific signal catalog above. The driver still assigns **no**
frustration itself — like P1, it is a subjective call the judge renders from captured
evidence.

**What's MCP-specific (the thin adapter), and only this:**
- `lib/evidence-adapter.mjs` maps `agentweaver.mcp-transcript/v1` turns →
  the shared digest (`{kind, intent, composition, httpStatus→protocolStatus, …}`), and
  surfaces the MCP frustration signals (retry loops, abandonment, `isError` ratio) for
  the judge to weigh;
- the MCP `JUDGE.md` **addendum** documents the extra evidence fields (`isError`,
  `protocolErrorCode`, tool-loop trace), the #129 actionable-error rubric, and the
  MCP-specific frustration-signal catalog;
- the shared `assemble.mjs` gains a small `surface` parameter (`api|ui|mcp`) so the
  prompt preamble names the surface, but the **method, schema, and taxonomy are
  identical**.

**Evidence Sources (applies to the shared judge, not just the UI harness).** The judge
must not reason from the raw tool-call transcript **alone** — it cross-references what
an MCP tool call *claimed* happened against what *actually* happened server-side. The
shared judge relies on **all** of:
- **Visuals** — screenshots/DOM (available only when a scenario also drives the UI in
  the same run; **N/A for pure-MCP runs**, present for hybrid MCP+UI scenarios).
- **API / MCP responses** — the protocol-level evidence this harness already captures
  verbatim (tool name, args, structured result, `isError`, `protocolErrorCode`, timing,
  tool-loop trace).
- **Server-side logs — Application Insights + cluster (`kubectl`)** — the ground-truth
  of what the backend actually did, correlated to the transcript by **`run_id` and
  `trace_id`** (the transcript already records `traceId` from the MCP result `_meta` /
  response headers, and `run_id` from `run_submit`/`coordinator_start` results). The MCP
  **evidence adapter** (`lib/evidence-adapter.mjs`) should therefore pull the relevant
  AppInsights transactions and `kubectl logs` slices keyed by those IDs and attach them
  to the assembled judge prompt alongside the transcript — reusing the proven correlation
  queries from `docs/e2e-harness-plan.md` (App Insights transaction search on the run's
  correlation/session ID; `kubectl logs -n agentweaver <pod>`). This lets the judge catch
  **claim-vs-reality drift** — e.g. a tool call returned success but the backend logged a
  silent failure, or a `start_preview` reported a URL that never served traffic — which
  the transcript alone cannot show. Log pulls are **best-effort**: if AppInsights/kubectl
  are unavailable, the judge proceeds on transcript + protocol evidence and marks the
  unverifiable claims `CANNOT_DETERMINE` rather than guessing. Consistent with driver-only:
  the **adapter gathers** this correlated evidence; the **judge interprets** it.

**Rejected alternative (b) — a fully separate MCP judge** — because it would fork the
prompt library and verdict schema, guarantee drift between the three harnesses' quality
bars, and **destroy cross-surface meta-aggregation** (the single most valuable thing
running three harnesses buys us). The only condition that would justify (b) is if MCP
protocol evidence needed a fundamentally different verdict taxonomy than HTTP/DOM — and
investigation shows it does not (protocol errors slot into P0; error-actionability
slots into P1).

### 3. Driver-only, everywhere (the shared hard constraint)

All three harnesses keep the **identical** hard rule: the driver **captures verbatim
deterministic evidence and never embeds subjective correctness judgment**. For the MCP
harness that means the driver records the tool called, its arguments, the structured
result, `isError`, protocol error codes, and timing — and asserts only objective facts
(call succeeded, spec settled, pushbacks applied, result schema valid). Whether the
outcome is *good*, whether an error was *actionable*, whether the system *improved in
response to pushback* — all belong to the shared judge, reading the captured MCP
transcript after the fact. Same contract, same schema, different surface.

---

## Rollout Plan (build in parallel without touching in-flight files)

**Constraint:** Tank is actively editing `scripts/persona-harness/`
(`agent-driver/tools.mjs`, `lib/`, `runner.mjs`) and Trinity is writing
`docs/ui-test-harness-plan.md`. This harness must be buildable **without modifying any
of their in-flight files**. Everything below is **read-only** w.r.t. the API harness.

**Phase 0 — spec & convergence (this document).** Land `docs/mcp-test-harness-plan.md`.
Record the judge-architecture recommendation as a decision so it can be reconciled with
Trinity's parallel UI recommendation **before** anyone extracts the shared package.

**Phase 1 — new sibling scaffold, zero shared-file edits.** Create
`scripts/mcp-persona-harness/` with the MCP client (`mcp-client/`), the driver tool
surface (`agent-driver/tools.mjs`), the transcript writer, the P0 block, and the
reporter. At this phase it can **temporarily read** the API harness's `briefs/*.md`
**read-only** (import path, no edits) so it can run before the shared package exists.
Deliver the **#131 stdio smoke test** first — it's the highest-value, most
deterministic piece and unblocks CI.

**Phase 2 — shared `scripts/persona-briefs/` extraction (coordinated, gated on
Phase 0 convergence).** Once Trinity and I have reconciled the judge recommendation,
one coordinated change **moves** `briefs/*.md` + the judge core (`JUDGE.md`,
`verdict-schema`, `assemble`, `meta-aggregate`) into `scripts/persona-briefs/` and
re-points imports. This is done as a **single, sequenced step when the API harness is
at a safe checkpoint** (Tank's `harness/wip-persona-v1` merged or paused) — never as a
concurrent edit to Tank's live files. Until then, the MCP harness imports briefs
read-only and carries its own thin judge addendum.

**Phase 3 — LLM-driven scenarios + cross-surface meta-aggregation.** Add the
brief-driven scoping-rung scenarios (Jordan/Maya/Priya over MCP), the #129 error-probe
scenario, and wire MCP verdicts into the shared `meta-aggregate.mjs` so API-vs-MCP
comparisons for the same persona become possible. Add the opt-in `--deep` rung
(confirm → dispatch → approvals → preview → completion) behind a flag, reusing the E2E
plan's **live-`curl`-preview-before-approve** rule.

**Phase 4 — CI + acceptance-test-for-#295-children.** Wire `npm run test:mcp-smoke` at
the repo root (#131 AC), and grow the harness's assertions as #129/#130/#128 land so it
doubles as their acceptance suite.

**Non-interference guarantees:**
- New top-level dir `scripts/mcp-persona-harness/` — no path overlap with
  `scripts/persona-harness/` or `docs/ui-test-harness-plan.md`.
- Phase 1 only **reads** the API harness's briefs; it never writes them.
- The shared-package extraction (Phase 2) is explicitly deferred to a coordinated
  checkpoint, not raced against Tank's edits.
- No release-pipeline actions; the coordinator ships.

---

## Open Questions (to reconcile with Trinity + coordinator)

1. **Shared-package name/location:** `scripts/persona-briefs/` proposed here. Trinity
   is asked the same — converge on one name before Phase 2.
2. **MCP client dependency:** adopt `@modelcontextprotocol/sdk` (recommended) vs a
   hand-rolled JSON-RPC client (zero-dep). Recommendation: the real SDK, to exercise
   real protocol framing.
3. **CI auth for `--target http`:** device-flow `github_signin` needs a human once; CI
   uses `--target stdio` (no OAuth) or a pre-provisioned token. Confirm which staging
   bearer CI may hold.
4. **`run_task` (#130) dependency:** the one-call scenario is stubbed until #130 ships;
   the harness measures the multi-call baseline meanwhile.

---

## GitHub Copilot CLI Skill

So a Copilot session can say *"run the MCP harness against persona Jordan"* and have it
routed to the real CLI command — capturing the JSON verdict and reporting it back — the
MCP harness ships as a **Copilot-CLI-discoverable skill**. This section is **spec-only**;
authoring the actual `SKILL.md` content is a follow-on implementation task (same tier as
the harness build-out itself, done once the harness exists).

### Discovery mechanism matters

Copilot CLI **auto-discovers skills only from specific canonical directories**:

- `.github/skills/` &nbsp;— official Copilot CLI path
- `.claude/skills/` &nbsp;— official Copilot CLI path
- `.agents/skills/` &nbsp;— official Copilot CLI path
- `.squad/skills/` &nbsp;— this repo's own Squad convention
- `.copilot/skills/` &nbsp;— this repo's own Squad convention

It does **not** scan arbitrary `scripts/` subfolders. Therefore a `SKILL.md` living inside
`scripts/mcp-harness/` alone is **not** auto-discoverable by Copilot CLI — on its own it is
just a README for humans and other agents. Discoverability requires a file under one of the
canonical directories above.

### Design for TWO files, not one

1. **`scripts/mcp-harness/SKILL.md`** — the harness's own detailed operator / CLI-contract
   doc: the exact commands, flags (`--target http|stdio`, `--persona <name>`, `--scenario`,
   the smoke/`--smoke` P0-only rung, etc.), the expected JSON output shape (the canonical
   `agentweaver.persona-judge-verdict/v1` block with its `p0` / `p1` / `frustration`
   fields), and the exit codes (0 = drive+capture OK, non-zero = driver P0 fail). It lives
   with the code and is versioned alongside it, so the contract never drifts from the driver.

2. **A thin pointer skill at `.github/skills/mcp-harness/SKILL.md`** — the actual
   Copilot-CLI-discoverable entry point. Its job is to:
   - describe **when to invoke** this skill — e.g. *"use when asked to validate the MCP
     tool surface end-to-end, test a specific persona's tool-call flow, or investigate an
     MCP-reported issue"*; and
   - **delegate to the real harness by shelling out to its CLI** (e.g.
     `node scripts/mcp-harness/... --target stdio --persona <name>`), rather than
     duplicating the command contract.

   It follows this repo's existing skill-authoring convention — mirror the frontmatter and
   structure of an existing entry such as
   [`.copilot/skills/docs-feature/SKILL.md`](../.copilot/skills/docs-feature/SKILL.md)
   (`name` / `description` with trigger phrases / `domain` / `confidence` / `source`
   frontmatter, then a Markdown body). The pointer stays thin; the detailed contract stays in
   `scripts/mcp-harness/SKILL.md`.

### Same two-file treatment across all three harnesses

All three harnesses — **API**, **UI**, and **MCP** — get this identical two-file treatment
(a code-adjacent `scripts/<harness>/SKILL.md` contract plus a thin discoverable pointer under
`.github/skills/<harness>/SKILL.md`). This keeps the harnesses in lockstep and lets a single
Copilot session route *"run the MCP harness against persona X"* to the actual CLI command,
capture the canonical JSON verdict, and report the result back — the same way it would for the
API or UI surface.

**The special value for the MCP surface:** because this harness **authenticates and
transports exactly the way GitHub Copilot CLI itself does** (streamable-HTTP JSON-RPC with a
`gh auth token` bearer via the RFC 9728 discovery dance on `--target http`, or a launched
`--stdio` server on `--target stdio`), the pointer skill effectively lets **Copilot CLI test
its own MCP integration path** — a Copilot session invoking the skill drives Agentweaver's MCP
surface through the same seam Copilot CLI uses in production, so the harness doubles as a
self-test of that seam.
