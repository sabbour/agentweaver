# MCP harness CLI contract

Use this harness to validate Agentweaver's MCP protocol surface, capture the
complete `tools/call` request/response evidence, and emit a normalized
`agentweaver.persona-judge-verdict/v1` JSON verdict. It is for MCP end-to-end
validation, MCP tool-contract regression checks, and investigation of an
MCP-reported issue; use the API or UI harness for those surfaces.

Run all commands below from the repository root. The harness requires Node 18 or
newer. Install its dependencies and run its unit tests before changing it:

```powershell
npm --prefix scripts/mcp-harness install
npm --prefix scripts/mcp-harness test
```

There are **two** entry points, for two different jobs:

- `run-persona.mjs` — full **dynamic** persona scenario validation (no fixed
  script; a dispatched sub-agent drives the live tool menu). Use this to validate
  how the MCP surface behaves for a real persona.
- `smoke/mcp-cli-smoke.mjs` — a fast, **fixed** deterministic connectivity +
  capability tripwire. Use this for a quick "is the server reachable and does it
  still satisfy the capability contract" check.

Both transports work the same for either entry point:

- **stdio** (`--target stdio`) spawns a **local subprocess** via
  `--server-command`/`--server-args`. There is no network target validation —
  `stdio` is only a transport selector, never a URL.
- **http** (`--target <url>`) requires a real base URL that **must include the
  exact `/mcp` pathname** (e.g. `https://<host>/mcp`; the bare origin and
  `/mcp/` are not the endpoint). Any HTTPS host is accepted; HTTP is loopback-only.
  URL credentials/fragments and TLS bypasses are rejected.
  http transport also requires **OAuth**: `--token`/`AGENTWEAVER_TOKEN` must be an
  Agentweaver broker token for the exact `/mcp` resource with `mcp:invoke`, obtained
  through the app's OAuth flow. Raw Entra and GitHub tokens are rejected. Stdio
  transport still uses `AGENTWEAVER_TOKEN` for downstream API calls.

## Driving a persona scenario (the only way — dynamic, no fixed scripts)

There is no curated list of named tool sequences and no scripted layer between the
driving actor and the MCP server. A persona run is driven by a fresh sub-agent
dispatched under `scripts/mcp-harness/agent-driver/AGENT.md` — the MCP peer of the
API harness's `PersonaActor`. It is handed ONLY that persona's brief
(`scripts/persona-briefs/personas/<id>.md` + `scripts/persona-briefs/surfaces/<id>.mcp.md`),
discovers the live tool menu itself via MCP `tools/list`, decides every next
`tools/call` from the real response, pushes back at least twice when the real
content warrants it, never blind-approves an outcome-spec/confirmation gate, and
appends its own turn-by-turn JSONL transcript under
`scripts/mcp-harness/transcripts/` as it goes.

Because a Node process cannot itself dispatch a sub-agent (only an agent session
can), `run-persona.mjs` — exactly like the API harness's `run-persona.mjs`, which
also does not itself dispatch PersonaActor — owns the deterministic scaffolding
around that dynamic drive, in two phases the Harness agent runs in order:

### 1) prepare — resolve, guard, and emit the dispatch (default)

```powershell
node scripts/mcp-harness/run-persona.mjs `
  --scenario priya `
  --target http://localhost:5000/mcp `
  --token $env:AGENTWEAVER_TOKEN `
  --project-id <disposable-project-id> `
  --batch-id mcp-validation-001 `
  --seed priya
```

This resolves the persona core + `<id>.mcp.md` adapter, applies transport validation
(http only; stdio exempt), resolves the token, constructs the transcript path, and
writes the exact sub-agent dispatch prompt under `scripts/mcp-harness/dispatch/`.
It prints a `DISPATCH-REQUIRED` banner with the charter path, the dispatch-prompt
path, and the transcript path, then exits `3` (a dispatch was prepared but no
verdict exists yet — never a pass). It **never fabricates a transcript**.

The Harness agent then dispatches a fresh sub-agent under
`scripts/mcp-harness/agent-driver/AGENT.md` (via the `task` tool) with that prompt.
The sub-agent drives the server live and appends the JSONL transcript itself.

### 2) finalize — export MCP-adapted evidence, then judge natively (recommended)

```powershell
node scripts/mcp-harness/run-persona.mjs `
  --scenario priya `
  --target http://localhost:5000/mcp `
  --token $env:AGENTWEAVER_TOKEN `
  --transcript scripts/mcp-harness/transcripts/priya-live-<timestamp>.jsonl `
  --dump-evidence scripts/mcp-harness/verdicts/priya-evidence.json `
  --prompt-out scripts/mcp-harness/verdicts/priya-judge-prompt.txt
```

With `--transcript`, `run-persona.mjs` parses the JSONL the driver wrote, runs the
`required-capabilities.json` contract check **live** against the target (the MCP
peer of the API harness's fixed `generated-artifacts-seam` structural check —
deterministic, separate from the dynamic drive; pass `--no-capability-check` to
skip it, or it degrades gracefully if the server is unreachable), computes the
objective MCP P0 facts, adapts the evidence via `scripts/harness-judge/adapters/mcp.mjs`,
and writes normalized evidence plus the shared Judge prompt without calling a
subprocess judge. The Harness/Squad agent must synchronously dispatch the `Judge`
custom agent using the prompt-file content, save its raw text response, then validate
and persist it:

```powershell
node scripts/harness-judge/save-verdict.mjs <raw-judge-response.txt> `
  --evidence scripts/mcp-harness/verdicts/priya-evidence.json `
  --out scripts/mcp-harness/verdicts/priya.json
```

This agent-native path produces real LLM verdicts while keeping the untrusted
evidence isolated in the tool-less Judge agent. For headless/CI environments with no
agent session, the legacy `finalize --transcript ... --out ...` path remains available
and uses `AGENTWEAVER_JUDGE_CMD`; without that configured command it safely produces
`CANNOT_DETERMINE`. Read the saved verdict JSON and report its verdict and evidence
references; do not treat a zero driver exit as a subjective quality pass.

`--target` and `--base-url` are aliases; `--scenario` and `--persona` are aliases.
List the reviewed persona IDs that currently have MCP adapters (no server needed):

```powershell
node scripts/mcp-harness/run-persona.mjs --list
```

## Capability-contract smoke path (fixed, not a persona scenario)

The smoke path is the one fixed script here, and deliberately so: it is a fast,
deterministic connectivity + capability tripwire — submit a task, poll it, retrieve
artifacts, attempt cleanup — with no persona behavior or pushback dimension. Use it
to confirm the server is reachable and still satisfies
`scripts/mcp-harness/required-capabilities.json`.

```powershell
npm --prefix scripts/mcp-harness run smoke -- `
  --target stdio `
  --server-command dotnet `
  --server-args '["run","--project","apps/Agentweaver.Mcp","--","--stdio"]' `
  --project-id <disposable-project-id> `
  --project-is-disposable
```

The smoke command supports `--target stdio|http`, `--token`, `--project-id`,
`--project-is-disposable`, `--goal`, `--timeout-ms`, `--poll-ms`, and
`--list` (a no-connect print of the reviewed persona IDs with MCP adapters). On
success it emits JSON headed by `DRIVE+CAPTURE OK`, including the run ID, terminal
status, artifact count, and compatibility report. A non-zero exit means the driver
or its capability contract failed; preserve its output when reporting the failure.

## Discovery and contract rules

The harness begins every session with live MCP `tools/list` discovery. Treat that
response — the live tool names, schemas, and descriptions — as the sole action
menu. Do not hardcode or substitute tool names from documentation.

Independently, both entry points check
`scripts/mcp-harness/required-capabilities.json`. This regression contract verifies
the required workflow capabilities and their input/output schema shapes while
allowing unrelated additive tools. A contract failure is a surface regression, not
a reason to bypass discovery or call a guessed tool.

## Options and safety

- Network targets use the host-agnostic transport rules above; stdio is exempt.
- `--project-id` requires `--project-is-disposable`. The run is archived, but a
  caller-supplied project is never deleted.
- Without `--project-id`, smoke creates a uniquely named project with a remote-safe
  empty working-directory request and the software-development blueprint. It deletes
  only that owned project after archiving the run, on success or failure.

Exit codes for `run-persona.mjs`: `0` means the phase completed (finalize produced
a verdict from real evidence with a passing capability contract, or prepare emitted
a dispatch), `1` means a deterministic check failed (capability-contract regression
or judged P0 FAIL), `2` is setup/harness failure, and `3` is inconclusive (a
dispatch was prepared but not yet driven, or the judge could not render a verdict).
Treat exit `3` as inconclusive, not pass.
