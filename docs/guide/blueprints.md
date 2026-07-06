# Blueprints

Blueprints define the initial team roster, workflow set, review policy, and sandbox profile for a project.

## Blueprint validation

Generated and user-supplied blueprints are validated before they can be applied. Validation checks that:

- required fields are present (`id`, `name`, `review_policy`, `sandbox_profile`, roster, and workflows);
- roster roles are known catalog roles or declared bespoke roles with charters;
- workflow ids exist and their graphs are runnable, connected from `start`, and free of unreachable nodes;
- `review_policy` is coherent with the supported policy set;
- `sandbox_profile` is one of the supported profiles (`default` or `restricted`).

If generation returns an invalid blueprint, the API reports plain-language validation details and offers two safe next steps: regenerate with a clearer prompt or edit the draft and validate it again. Invalid blueprints are not saved or applied.
