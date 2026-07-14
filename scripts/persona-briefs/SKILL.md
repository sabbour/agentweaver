# Harness scenario catalog and generation contract

Use this contract when you need to discover the current built-in scenario/persona
catalog for one harness surface, or generate a reviewed new persona core and surface
adapter for a new test intent.

Generators in this package are **prompt assemblers only**. They never call a model,
write a persona file, scaffold a harness scenario module, or bypass review.

Before starting, read `scripts/harness-shared/learnings.md`'s `scenario-design-note`
entries — several existing adapters intentionally stop before full completion (a
review/confirmation gate), which is by design, not a stuck/broken run.

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
node scripts/mcp-harness/smoke/mcp-cli-smoke.mjs --list
```

Output: JSON with `surface: "mcp"`, `mode: "persona-adapter"`, and `scenarios`, the
persona IDs that currently have reviewed MCP adapters under
`scripts/persona-briefs/surfaces/*.mcp.md`. The current implemented MCP runner is
still the smoke path; treat this catalog as the reviewed starting set for MCP
persona-driven work.

## Generate a new reviewed scenario starter

Before generating anything new, check whether an existing persona/adapter already
fits: run `node scripts/persona-briefs/find-similar.mjs --description "<intent>"`,
which does a cheap keyword/tag match (no LLM call) against
`scripts/persona-briefs/catalog.json` and returns ranked close matches. Only proceed
to generation below if nothing close already exists.

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
- Preserve each harness's existing target and production safety gates exactly as
  documented in its authoritative surface contract.
