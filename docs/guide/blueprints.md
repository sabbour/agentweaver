# Blueprints

Blueprints define the initial team roster, workflow set, review policy, and sandbox profile for a project.

## Blueprint validation

Generated and user-supplied blueprints are validated before they can be applied. Validation checks that:

- required fields are present (`id`, `name`, `review_policy`, `sandbox_profile`, roster, and workflows);
- roster roles are known catalog roles or declared bespoke roles with charters;
- workflow ids exist and their graphs are runnable, connected from `start`, and free of unreachable nodes;
- `review_policy` is coherent with the supported policy set;
- `sandbox_profile` is one of the supported profiles (`default` or `restricted`).

Built-in catalog entries are also checked through the production workflow parser and runtime graph
binder at startup. `GET /api/blueprints` returns additive `exportability` diagnostics for every
parsed built-in (`exportable` or `unavailable` plus stable codes). Unavailable entries remain visible
for troubleshooting, but cannot be suggested, selected, or applied.

If generation returns an invalid blueprint, the API reports plain-language validation details and offers two safe next steps: regenerate with a clearer prompt or edit the draft and validate it again. Invalid blueprints are not saved or applied.

## Generated blueprint hardening

Blueprint generation is library-first. The generator selects built-in workflows only when their
process fits the requested team, not just because names or domain words overlap. When no built-in
workflow fits, Agentweaver can generate a custom workflow draft and, on apply, write it into the new
project's `.agentweaver/workflows/` directory so it is immediately selectable.

Generated blueprints may also include bespoke roles, but only as a last resort. Every non-catalog
role must have a matching bespoke role definition with an inline charter, and validation rejects
unrostered bespoke roles or bespoke ids that collide with catalog roles.

See [Workflow generation](../workflow-generation.md) for the generation path and [Workflows](./workflows.md)
for editing and saving generated workflow drafts.
