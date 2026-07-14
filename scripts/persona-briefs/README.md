# Agentweaver persona briefs

Persona cores in `personas/` are surface-agnostic. Surface adapters live in
`surfaces/` and use `<persona>.<surface>.md`: for example `jordan.api.md`,
`jordan.ui.md`, and `jordan.mcp.md`. This package initially provides API adapters;
the UI and MCP harness owners can add their adapters without copying cores.

Use `loadPersona(name, surface)` from `index.mjs` to load and validate a pair.
`generate-core.mjs` and `generate-adapter.mjs` assemble provider-neutral prompts for
an external LLM; they never call a model or persist generated output.
