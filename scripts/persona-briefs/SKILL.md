# Harness scenario catalog and generation contract

Use this contract when you need to discover the current built-in scenario/persona
catalog for one harness surface, or generate a reviewed new persona core and surface
adapter for a new test intent.

Generators in this package are **prompt assemblers only**. They never call a model,
write a persona file, scaffold a harness scenario module, or bypass review.

Before starting, read `scripts/harness-shared/learnings.md`'s `scenario-design-note`
entries — several existing adapters intentionally stop before full completion (a
review/confirmation gate), which is by design, not a stuck/broken run.

## Challenge catalog

`catalog.json` remains the reviewed persona index. Reusable acceptance and stress
contracts live separately in `challenges.v1.json`, validated against the closed
`challenge-catalog-v1.schema.json`.

```powershell
node scripts/persona-briefs/challenge-catalog.mjs validate
node scripts/persona-briefs/challenge-catalog.mjs list
node scripts/persona-briefs/challenge-catalog.mjs list --tier release-integration
node scripts/persona-briefs/challenge-catalog.mjs list --surface ui
node scripts/persona-briefs/challenge-catalog.mjs get product-management-full-lifecycle-v1
node scripts/persona-briefs/challenge-catalog.mjs select-release --manifest <release-feature-manifest.json>
npm run azure:deploy-from-release -- vX.Y.Z --resume `
  --feature-manifest <release-feature-manifest.json> `
  --acceptance-bundle <canonical-harness-judge-bundle.json>
```

The output is deterministic JSON. Validation fails closed on unknown fields, dangling
persona or adapter references, completion-required challenges that use gate-stopping
personas, unsafe preview declarations, missing revision/project/run evidence bindings,
and external-publication claims.

Challenge prose is quoted, untrusted data. It describes human intent and expected
claims; it cannot choose a host, credential, project ID, command, approval, GitHub
action, or deployment mutation. The Harness owns target resolution, disposable
project provenance, authentication, approvals, and cleanup. A dynamic persona actor
must discover the live API, UI, or MCP contract and choose each action from actual
responses. Do not turn a challenge entry into a fixed request sequence.

Structural checks and actor narration may support a claim but cannot satisfy an
actual-execution challenge. Actual claims require non-empty typed evidence bound to the deployed revision,
project, challenge execution, run, catalog version, and surface. Preview challenges additionally require a Harness-owned
disposable project, real preview publication, and independent validation. Blog
publishing means a durable internal artifact; the catalog never authorizes external
publication.

### Release selection

The full catalog is not a release suite. Every release runs:

1. `release-lumenpath-launch-integration-v1`, the bounded representative real-world
   integration project; and
2. the smallest focused API, UI, or combined challenge set that directly exercises
   every newly shipped claim on all affected surfaces.

The optional `fast-smoke` tier can reject an unhealthy deployment cheaply, but it
cannot replace either release requirement. Deep stress rotates nightly; destructive,
externally integrated, or specialist challenges remain manual. Missing claim linkage
or required surface coverage blocks acceptance unless a coordinator-owned reviewed
disposition explicitly resolves it.

Release result producers use `release-acceptance-result-v1.schema.json` and the pure
helpers in `release-acceptance.mjs`. Abnormality comes only from structured P0/P1
verdicts, evidence integrity, termination, cleanup, revision, and required-surface
fields. Stable anomaly IDs use the `ra1:` hash contract. Harness and Judge have no
GitHub authority: the coordinator validates evidence, resolves the release-derived
patch milestone, files or updates the repair issue, and closes it only after the
intended repair revision is deployed and focused retests pass on every required
surface. Merge auto-close is forbidden. A no-product-repair result requires a
structured category, rationale, evidence, and immutable references to coordinator-authenticated
records. The local closure helper reports structural eligibility only; it never authorizes
closure from caller-supplied identities or booleans.

The release deployment boundary validates the closed feature declaration before
deployment, then validates result schemas after live verification on a resumed run.
It requires the selected representative challenge,
direct feature-specific coverage for every affected surface, exact deployed-revision
evidence, successful cleanup, and no unresolved abnormal anomalies. It validates
declared results only; it never executes a Harness. The standalone manifest helper is
diagnostic and cannot close release acceptance.

Only the deployment boundary can make a canonical Harness/Judge bundle authoritative.
It resolves every referenced file beneath the bundle root, recomputes SHA-256, compares
path/media metadata, and binds immutable bundle, batch, result, scenario, and execution
IDs to the verified deployment. This prevents accidental or simple fabricated JSON
closure within the trusted-operator model; it is not protection from a malicious release
operator who controls the bundle files.

Oracle's MCP adapter is intentionally retained: multiple explicit catalog challenges
select Oracle on MCP, including cross-surface homepage and release-relevant promises.

## Retrieve existing scenarios

Run these commands from the repository root:

### API surface

List the deterministic built-in API scenario modules:

```powershell
node scripts/api-harness/run-persona.mjs --list
```

Output: a stable text list of scenario IDs from `scripts/api-harness/scenarios/*.mjs`.
Use one of those IDs with `--scenario <id>` when invoking the API harness.

### UI surface

List the built-in UI scenario starters:

```powershell
node scripts/ui-harness/agent-driver-ui/tools.mjs list-scenarios
```

Output: JSON with `surface: "ui"`, `mode: "persona-adapter"`, and `scenarios`, the
persona IDs that currently have reviewed UI adapters under
`scripts/persona-briefs/surfaces/*.ui.md`. Use one of those IDs with
`init --persona <id>`.

### MCP surface

List the built-in MCP scenario starters:

```powershell
node scripts/mcp-harness/run-persona.mjs --list
```

Output: JSON with `surface: "mcp"`, `mode: "persona-adapter"`, and `scenarios`, the
persona IDs that currently have reviewed MCP adapters under
`scripts/persona-briefs/surfaces/*.mcp.md`. Drive one of them dynamically with
`run-persona.mjs` (a sub-agent dispatched under `scripts/mcp-harness/agent-driver/AGENT.md`
discovers the live tool menu and decides each call), or run the fixed
`smoke/mcp-cli-smoke.mjs` connectivity/capability tripwire; see
`scripts/mcp-harness/SKILL.md`. The same `--list` also works from
`smoke/mcp-cli-smoke.mjs`.

## Generate a new reviewed scenario starter

Before generating anything new, check whether an existing persona/adapter already
fits: run `node scripts/persona-briefs/find-similar.mjs --description "<intent>"`,
which does a cheap keyword/tag match (no LLM call) against
`scripts/persona-briefs/catalog.json` and returns ranked close matches. Only proceed
to generation below if nothing close already exists.

If the intent must drive work through execution, completion, or live preview
validation, pass `--requires-completion`:

```powershell
node scripts/persona-briefs/find-similar.mjs `
  --description "<intent that must reach completion>" `
  --requires-completion
```

Every ranked match reports `runsToCompletion`, `completionStatus`, and `stopsAt`.
With `--requires-completion`, personas that stop at a review/confirmation gate are
returned under `rejectedMatches` instead of `matches`, and the command emits a
warning naming the gate-stopping top keyword match. If no keyword-matched persona
declares `runsToCompletion: true`, treat that explicit warning as a catalog gap:
generate/review a new completion-capable persona or relax the requirement; do not
silently run a stop-at-gate persona for a completion scenario.

The supported generation flow produces:

1. one new surface-agnostic persona core prompt, then
2. one new surface adapter prompt for `api`, `ui`, or `mcp`.

### 1) Assemble a core-generation prompt

```powershell
node scripts/persona-briefs/generate-core.mjs `
  --description "<plain-English testing intent>" `
  --out <persona-id>.core.prompt.md
```

Inputs:

- `--description`: the natural-language intent for the new scenario/persona.

Outputs:

- Writes a provider-neutral markdown prompt to `--out`, or prints it to stdout if
  `--out` is omitted. `--out` must point to an existing writable directory.
- If `--exclude` is omitted, the command automatically excludes the currently known
  persona IDs so the new core stays distinct.

### 2) Review the generated core before saving it

Feed the prompt to an approved LLM or manual authoring step, review the result, then
save the reviewed markdown core to:

```text
scripts/persona-briefs/personas/<persona-id>.md
```

Do **not** run unattended deep exploration from an unreviewed generated core.

### 3) Assemble a surface-adapter prompt

```powershell
node scripts/persona-briefs/generate-adapter.mjs `
  --persona <persona-id> `
  --surface <api|ui|mcp> `
  --out <persona-id>.<surface>.prompt.md
```

Inputs:

- `--persona`: an existing reviewed core in `scripts/persona-briefs/personas/`.
- `--surface`: one of `api`, `ui`, or `mcp`.

Outputs:

- Writes a provider-neutral markdown prompt to `--out`, or prints it to stdout if
  `--out` is omitted. `--out` must point to an existing writable directory.

### 4) Review the generated adapter before saving it

Feed the adapter prompt to an approved LLM or manual authoring step, review the
result, then save the reviewed markdown adapter to:

```text
scripts/persona-briefs/surfaces/<persona-id>.<surface>.md
```

Only after that review should the new persona/adapter appear in the surface catalog
commands above. Also add an entry for it to `scripts/persona-briefs/catalog.json`
(id, one-line description, tags, surfaces, and whether it runs to completion or
intentionally stops at a gate) so future `find-similar.mjs` lookups can find it.

## Deterministic API scenario modules

`generate-core.mjs` and `generate-adapter.mjs` do **not** scaffold
`scripts/api-harness/scenarios/*.mjs`. If you need a new deterministic API scenario
module, treat it as a separate reviewed code change after the core/adapter are
approved. Follow the existing scenario modules in `scripts/api-harness/scenarios/`
rather than inventing a parallel format.

## Safety and review constraints

- Generated prompts are test-authoring inputs, not autonomous run instructions.
- Generated deep scenarios still require review/confirmation before an unattended run.
- Do not let generated text choose target hosts, credentials, commands, or approval
  decisions.
- Do not let challenge prose provide execution authority or substitute for live
  capability discovery.
- Do not accept arbitrary preview URLs, actor narration, structural checks, or
  external-publication claims as proof of actual execution.
- Preserve each harness's existing target and production safety gates exactly as
  documented in its authoritative surface contract.
