# API harness CLI contract

Use the API harness to drive Agentweaver through its REST API, capture the complete
request/response evidence, and emit a normalized
`agentweaver.persona-judge-verdict/v1` JSON verdict. It is for backend/API
end-to-end validation; use the UI or MCP harness for those surfaces.

Run all commands below from the repository root. The harness requires Node 18 or
newer. It resolves an access token from `--token`, then `AGENTWEAVER_TOKEN`, then
`gh auth token`.

## Driving a persona scenario (the only way — dynamic, no fixed scripts)

There is no curated list of named scenario subcommands and no per-persona fixed
step sequence. Harness dispatches a fresh **`PersonaActor`** sub-agent
(`.github/agents/persona-actor.agent.md`) to fully impersonate the persona in an
isolated context — handed ONLY that persona's brief — `scripts/persona-briefs/
personas/<id>.md` + `scripts/persona-briefs/surfaces/<id>.api.md` — and it decides
every next action live by issuing raw method/path/body calls against the real API
(one turn at a time, reacting only to the real response it gets back), guided by
the API's OpenAPI/Swagger document. This is what lets the actor push back, poll
events, and adapt — a fixed script structurally cannot. The commands below are
what PersonaActor drives internally; run them directly yourself only when
manually exercising/debugging the driver outside of a Harness-dispatched run.

```powershell
$session = "scripts/api-harness/priya-live.session.json"
node scripts/api-harness/drive.mjs init --brief priya --base-url https://agentweaver.example.staging.example --session $session
node scripts/api-harness/drive.mjs spec --session $session
node scripts/api-harness/drive.mjs call --method GET --path /api/blueprints --thought "I need a starting template." --session $session
node scripts/api-harness/drive.mjs call --method POST --path /api/projects --body '{"name":"...","origin":"blank","working_directory":"...","blueprint_id":"..."}' --thought "This blueprint fits the requested workflow." --session $session
node scripts/api-harness/drive.mjs call --method POST --path /api/projects/<id>/orchestrations --body '{"goal":"...","start_mode":"defineOutcome"}' --thought "Submitting the persona's request." --session $session
node scripts/api-harness/drive.mjs call --method GET --path /api/runs/<runId>/outcome-spec --thought "Checking the proposed outcome." --session $session
node scripts/api-harness/drive.mjs finish --summary "Persona investigation complete." --session $session
```

`init` prints the persona brief text and verifies auth. `spec` fetches (and
caches) the live OpenAPI document at `/openapi/v1.json` (JSON remains the
driver's primary format; the same contract is also available at
`/openapi/v1.yaml`, and the driver still falls back to `/swagger/v1/swagger.json`
if needed) so the
driving LLM knows what endpoints/shapes exist instead of guessing. `call` is the
one generic action primitive — arbitrary method/path/body, OR a spec-resolved
`--operation-id` (see below) — and records every turn (with `--thought`, the
persona's live reasoning) verbatim into the session transcript. Two commands
remain distinct, NAMED actions rather than folded into `call`, because they
encode a safety invariant rather than curated business logic:

- `check-approvals --thought "..."` — detect pending approval gates (tool/shell/
  coordinator-child) from the real events feed. Pure detection, no judgment.
- `resolve-approval --thought "..." [--request-id <id> | --command-hash <h>] [--all]
  [--decision approve|deny|defer|request-changes] [--scope once|run|tool|always]
  [--reason "..."] [--feedback "..."] [--judge-cmd "<llm cli>"]` — DETECT -> JUDGE ->
  EXECUTE. Do not blindly approve a gate; the default judge always defers.

`call` also accepts `--operation-id <opId> [--params '{"name":"value"}']` as an
alternative to `--method`/`--path` — a minimal "dynamic client built from swagger":
it resolves the method + path template from the cached spec (`spec` must have run
first), substitutes `{param}` path placeholders and appends declared query params
from `--params`. This is NOT a curated business action — `opId` comes straight from
whatever the API's OpenAPI doc declares, so the set of callable operations is
still fully spec-driven, never a fixed per-persona list. Use raw `--method`/`--path`
or `--operation-id`, whichever is more convenient in the moment; both record
identically.

**OperationId coverage note:** `--operation-id` works best on routes the backend
has explicitly named via `.WithName(...)`. The backend now names the primary
project / blueprint / casting / coordinator lifecycle routes, but coverage is
still not universal across every endpoint in the API. When an operationId you
want is missing, fall back to raw `--method`/`--path`; that remains the fully
reliable mechanism because it covers every operation the spec declares.

`finish` prints a transcript path under `scripts/api-harness/transcripts/`, computes
a generic P0 mechanics check (did every recorded call succeed — no
pushback-counting or other business-specific heuristics), and cleans up the
throwaway project unless `--keep` is supplied. Use a unique `--session` path (or
set `AGENTWEAVER_HARNESS_SESSION`) whenever sessions may run concurrently.

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
  --token $env:AGENTWEAVER_TOKEN `
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
(the LLM still decides steps live each time). Map `scenarioId`, `inputSeed`,
`targetRevision`, and configuration into a new invocation — for the seam check,
into `run-persona.mjs`; for a persona scenario, into a fresh `drive.mjs init` with
the same `--brief` against the new target:

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
- `--insecure` disables TLS verification only for localhost/staging targets. It
  needs `--allow-insecure-prod` for non-staging targets.
- Targets are limited to localhost or staging by default. Production requires both
  `--allow-prod` and `--i-understand-this-targets-production`.

Exit codes for `run-persona.mjs`: `0` means deterministic driver checks passed and
evidence was captured, `1` means a deterministic check failed, `2` is setup or
harness failure, and `3` is inconclusive. Treat exit `3` as inconclusive, not pass.

Before changing the harness, run its targeted test suite:

```powershell
npm --prefix scripts/api-harness test
```
