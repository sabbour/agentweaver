# Blueprints

Blueprints define the initial team roster, workflow set, review policy, and sandbox profile for a project.

## Blueprint validation

Generated and user-supplied blueprints are validated before they can be applied. Validation checks that:

- required fields are present (`id`, `name`, `review_policy`, `sandbox_profile`, roster, and workflows);
- roster roles are known catalog roles or declared bespoke roles with charters;
- workflow ids exist and their graphs are runnable, connected from `start`, and free of unreachable nodes;
- `review_policy` is `default`, the only currently accepted policy;
- `sandbox_profile` is one of the supported profiles (`default` or `restricted`).

If generation returns an invalid blueprint, the generation job ends with a canonical failure. Invalid blueprints are not saved or applied.

## Durable generation jobs

Blueprint generation runs asynchronously so complex Blueprint and custom-workflow requests are not
limited by one HTTP response window. `POST /api/blueprints/generate` requires an
`Idempotency-Key` header and returns `202 Accepted` with a job id plus status, result, cancel, and
retry URLs.

The accepted job snapshots the caller, project or repository context, effective provider and model
selection, and credential-binding version. Reusing the same idempotency key with the same request
returns the original job; reusing it for different input returns `409 Conflict`.

Poll the status URL until the job is terminal:

- `completed`: fetch the result URL. The result identifies one immutable Blueprint artifact by
  `artifact_id`, Blueprint `logical_id`, and version.
- `failed`: inspect the redacted failure code and message. Provider deadlines use
  `blueprint_provider_timeout`; temporary provider failures use
  `blueprint_provider_unavailable`. Retry is available only when the failure is marked retryable.
  If the accepted provider or credential binding changed, reauthorize and submit a new request so
  the new job captures the replacement provider snapshot.
- `cancelled`: use the retry URL to queue the same job again. Retry cannot create another artifact
  for a job that already completed.

Status, result, cancel, and retry requests reauthorize the current caller and any bound project.
Provider failures never substitute a default workflow and report success.

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

<!-- flagship-diagrams:start -->
## Visual model

### Blueprint relationships

[![UML component view showing catalog selection, repository suggestion, or model generation producing a Blueprint; the Blueprint owning roster and role definitions while referencing workflows, review policy, and sandbox profile; explicit validation and apply materializing project configuration and a cast team consumed by the coordinator.](../diagrams/flagship/canonical-blueprint-relationships.png)](../diagrams/drawio/generated/flagship/canonical-blueprint-relationships.drawio)

[Structured source](../diagrams/src/flagship/canonical-blueprint-relationships.json) · [Editable draw.io](../diagrams/drawio/generated/flagship/canonical-blueprint-relationships.drawio)
<!-- flagship-diagrams:end -->
