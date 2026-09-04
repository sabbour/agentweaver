# API harness CLI contract

Use the API harness to drive Agentweaver through its REST API, capture the complete
request/response evidence, and emit a normalized
`agentweaver.persona-judge-verdict/v1` JSON verdict. It is for backend/API
end-to-end validation; use the UI or MCP harness for those surfaces.

Run all commands below from the repository root. The harness requires Node 18 or
newer. It requires an access token from the transient `AGENTWEAVER_TOKEN`
environment or the explicit recorder-session provider. It never accepts bearer
material in process arguments and never borrows `gh auth token` or
`GITHUB_TOKEN` for a remote target.

### Token acquisition for staging (Entra Conditional Access)

Agentweaver staging uses Entra Conditional Access. The token cannot be retrieved via
device-code flow or plain browser. With an already authenticated demo-recording
session, use the recorder-session provider. It reads the protected session token only
in memory for each request; never export it to an environment variable, CLI argument,
transcript, finding, verdict, or log:

```powershell
node scripts/api-harness/run-persona.mjs `
  --scenario generated-artifacts-seam `
  --target https://<host>.staging.<domain> `
  --auth-provider recorder-session
```

The provider uses `scripts/demo-recording/.auth/` by default; use
`--recorder-auth-root` only for an existing protected recording-auth root. Verify the
recording session first with `npm run demo:record -- status`. If its bearer is expired,
the human-only recording sign-in flow must refresh it.

## Driving a persona scenario (the only way — dynamic, no fixed scripts, no HTTP-calling wrapper)

There is no curated list of named scenario subcommands, no per-persona fixed
step sequence, and no scripted HTTP-calling layer standing between the driving
actor and the target. Harness dispatches a fresh **`PersonaActor`** sub-agent
(`.github/agents/persona-actor.agent.md` — a real CLI agent with shell/`execute`
access) to fully impersonate the persona in an isolated context — handed ONLY
that persona's brief — `scripts/persona-briefs/personas/<id>.md` +
`scripts/persona-briefs/surfaces/<id>.api.md` — and it decides every next action
live by calling whatever operation it resolves from the real API's OpenAPI/
Swagger document, one turn at a time, reacting only to the real response it gets
back. This is what lets the actor push back, poll events, and adapt — a fixed
script (or a fixed subcommand wrapper) structurally cannot.

Roughly, this is what PersonaActor runs inside its own shell (see
`persona-actor.agent.md` for the exact turn-by-turn contract — spec fetching,
pushback grounding, never-blind-approve, stop-at-gate, transcript recording):

```powershell
$env:AGENTWEAVER_BASE_URL = "https://agentweaver.example.staging.example"
$transcript = "scripts/api-harness/transcripts/priya-live-<timestamp>.jsonl"

@'
const response = await fetch(`${process.env.AGENTWEAVER_BASE_URL}/api/blueprints`, {
  headers: { Authorization: `Bearer ${process.env.AGENTWEAVER_TOKEN}` },
  redirect: 'error',
});
console.log(await response.text());
'@ | node --input-type=module -
# ...append the real request+response as a JSON line to $transcript, then repeat...
```

The live OpenAPI spec (prefer `/openapi/v1.yaml` — more compact/token-efficient
for an LLM to read than the equivalent JSON; `/openapi/v1.json` is a fine
fallback) is how PersonaActor learns what endpoints/shapes exist instead of
guessing — including approval/steer/confirmation-type actions, which are just
more endpoints it discovers the same way, not special named commands. There is
no code-enforced default-defer wrapper for approvals anymore; PersonaActor is
explicitly instructed (in its own agent file) to never blind-approve a gate and
to ground every approval decision in real observed content.

**Transcript recording is PersonaActor's own responsibility.** It appends one
JSON line per turn (thought + real request + real response) with
`appendRedactedJsonLine` from `scripts/harness-shared/safe-jsonl.mjs` as it goes
— never direct shell redirection or reconstruction after the fact. Harness reads
this recursively sanitized file directly when building judge evidence.

## Generation-seam structural check (fixed, not a persona scenario)

`generated-artifacts-seam` is the one remaining fixed script, and deliberately so:
it asserts the blueprint/workflow GENERATORS are structurally correct (reserved-
role leaks, dangling edges, backend-guard round-trips) — a deterministic
regression check with no persona behavior or pushback dimension, not something a
driving LLM needs to interpret live.

```powershell
node scripts/api-harness/run-persona.mjs `
  --scenario generated-artifacts-seam `
  --target https://agentweaver.example.staging.example `
  --batch-id api-validation-001 `
  --seed generated-artifacts-seam `
  --out scripts/api-harness/verdicts/generated-artifacts-seam.json
```

`--target` and `--base-url` are aliases. The driver writes a finding under
`scripts/api-harness/findings/` and a verdict under `scripts/api-harness/verdicts/`
(or `--out`) and prints both paths. Read the verdict JSON and report its verdict
and evidence references; do not treat a zero driver exit as a subjective quality
pass.

### Re-test from a repro manifest

A repro manifest describes a **fresh** comparison run, not a byte-identical replay
of its old `runId`/`traceId` — and, for a dynamically-driven persona scenario, not a
literal replay of its prior turn sequence either. "Comparability" means: the same
persona-brief version + the same seed + the same target-revision, re-driven fresh
(a freshly dispatched PersonaActor still decides steps live each time). Map
`scenarioId`, `inputSeed`, `targetRevision`, and configuration into a new
invocation — for the seam check, into `run-persona.mjs`; for a persona scenario,
into a fresh `PersonaActor` dispatch with the same persona brief against the new
target.

```powershell
node scripts/api-harness/run-persona.mjs `
  --scenario generated-artifacts-seam `
  --target <current-target-url> `
  --target-revision <current-target-revision> `
  --batch-id <new-comparison-batch> `
  --out scripts/api-harness/verdicts/retest.json
```

The CLI intentionally has no `--repro-manifest` or `--run-id` replay option.
Preserve the original manifest and run identifiers only as provenance/correlation,
then compare the new verdict with the old one. `adapterVersion`,
`personaCoreVersion`, `harnessRevision`, judge model, and fixture state are not
override flags: verify that the checked-out harness, configured judge, and fixture
state match the manifest before calling a rerun comparable. Report any mismatch
instead of presenting the result as a like-for-like reproduction. Do NOT expect two
dynamic persona runs of the same brief to be byte-identical turn-for-turn — only
intent-comparable; the driving LLM may take a different but equally valid path
each time.

## Options and safety

- `--timeout <seconds>` sets the seam-check or polling timeout; `--keep` retains the
  throwaway resources.
- Network targets must be absolute HTTP(S) URLs. HTTP is allowed only for loopback;
  URL credentials/fragments and TLS bypasses are rejected.
- Bearer credentials are sent only to the configured origin. Absolute cross-origin
  paths and cross-origin redirects fail closed.

Exit codes for `run-persona.mjs`: `0` means deterministic driver checks passed and
evidence was captured, `1` means a deterministic check failed, `2` is setup or
harness failure, and `3` is inconclusive. Treat exit `3` as inconclusive, not pass.

Before changing the harness, run its targeted test suite:

```powershell
npm --prefix scripts/api-harness test
```
