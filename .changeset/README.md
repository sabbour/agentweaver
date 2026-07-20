# Agentweaver changesets

Agentweaver is a single private product package. The only selectable package is `agentweaver`; do not add a workspace or select `apps/web`.

Use `patch` for compatible bug fixes and `minor` for features or breaking changes while Agentweaver is below 1.0. `major` is prohibited until an intentional `release/v1.0.0` preparation.

Use `npm run changeset` to add release intent. Do not run `changeset publish`, `changeset pre`, or snapshot commands: Agentweaver is not published to npm and those workflows are unsupported.
