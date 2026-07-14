# API harness CLI contract

Use the API harness to drive Agentweaver through its REST API, capture the complete
request/response evidence, and emit a normalized
`agentweaver.persona-judge-verdict/v1` JSON verdict. It is for backend/API
end-to-end validation; use the UI or MCP harness for those surfaces.

Run all commands below from the repository root. The harness requires Node 18 or
newer. It resolves an access token from `--token`, then `AGENTWEAVER_TOKEN`, then
`gh auth token`.

## Deterministic persona scenario

List the built-in scenarios:

```powershell
node scripts/api-harness/run-persona.mjs --list
```

Run one scenario and save its verdict at a known location:

```powershell
node scripts/api-harness/run-persona.mjs `
  --scenario priya-ticket-triage `
  --persona priya `
  --target https://agentweaver.example.staging.example `
  --token $env:AGENTWEAVER_TOKEN `
  --batch-id api-validation-001 `
  --seed priya-ticket-triage `
  --out scripts/api-harness/verdicts/priya-ticket-triage.json
```

`--target` and `--base-url` are aliases. `--scenario` is required except with
`--list`; `--persona` otherwise defaults from the scenario name. The driver writes
a finding under `scripts/api-harness/findings/` and a verdict under
`scripts/api-harness/verdicts/` (or `--out`) and prints both paths. Read the
verdict JSON and report its verdict and evidence references; do not treat a zero
driver exit as a subjective quality pass.

### Re-test from a repro manifest

A repro manifest describes a **fresh** comparison run, not a replay of its old
`runId` or `traceId`. Map its `scenarioId`, `inputSeed`, `targetRevision`, and
configuration into a new invocation:

```powershell
node scripts/api-harness/run-persona.mjs `
  --scenario <reproManifest.scenarioId> `
  --seed <reproManifest.inputSeed> `
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
instead of presenting the result as a like-for-like reproduction.

## Exploratory persona driving

For a free-text or emergent investigation, use the discrete driver commands rather
than inventing raw REST requests. Give every action a `--thought`, use a unique
`--session` path when sessions may run concurrently, and finish the session so its
transcript is persisted:

```powershell
$session = "scripts/api-harness/agent-driver/priya-exploratory.session.json"
node scripts/api-harness/agent-driver/tools.mjs init --brief priya --base-url https://agentweaver.example.staging.example --session $session
node scripts/api-harness/agent-driver/tools.mjs list-blueprints --thought "I need a starting template." --session $session
node scripts/api-harness/agent-driver/tools.mjs create-project --blueprint <blueprint-id> --thought "This blueprint fits the requested workflow." --session $session
node scripts/api-harness/agent-driver/tools.mjs submit-goal --goal "<persona's free-text request>" --thought "I am submitting the user's request as Priya." --session $session
node scripts/api-harness/agent-driver/tools.mjs get-spec --thought "I am checking the proposed outcome." --session $session
node scripts/api-harness/agent-driver/tools.mjs finish --summary "Exploratory API investigation complete." --session $session
```

Available follow-up commands are `get-team`, `revise-spec`, `get-events`,
`check-approvals`, and `resolve-approval`. `resolve-approval` can make a judged
`approve`, `deny`, `defer`, or `request-changes` decision; provide `--decision`,
`--reason`, and (for requested changes) `--feedback`, or configure an external
judge with `--judge-cmd`. Do not blindly approve a gate. The default scoping flow
does not confirm or execute work. `finish` prints a transcript path under
`scripts/api-harness/transcripts/` and cleans up the throwaway project unless
`--keep` is supplied.

## Options and safety

- `--timeout <seconds>` sets the scenario or polling timeout; `--keep` retains the
  throwaway resources.
- `--insecure` disables TLS verification only for localhost/staging targets. It
  needs `--allow-insecure-prod` for non-staging targets.
- Targets are limited to localhost or staging by default. Production requires both
  `--allow-prod` and `--i-understand-this-targets-production`.
- `--rung <value>` is accepted and recorded by the scenario CLI, but the current
  runner's behavior is defined by its selected scenario; do not assume it enables
  deeper approval driving.

Exit codes for `run-persona.mjs`: `0` means deterministic driver checks passed and
evidence was captured, `1` means a deterministic check failed, `2` is setup or
harness failure, and `3` is inconclusive. Treat exit `3` as inconclusive, not pass.

Before changing the harness, run its targeted test suite:

```powershell
npm --prefix scripts/api-harness test
```
