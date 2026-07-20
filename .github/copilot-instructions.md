# Copilot instructions — Agentweaver

You are working in **Agentweaver**, a repo developed with **Squad** (a named AI agent
team) that welcomes contributions from AI agents, including GitHub's `@copilot`. If you
are an AI agent landing here cold, read these entry points before making changes:

- **[`CONTRIBUTING.md`](../CONTRIBUTING.md) → "AI agent contributions"** — THE canonical
  process entry point. Covers the issue-driven lifecycle (issue → branch → PR → review →
  merge), the reviewer-rejection protocol, rubber-ducking, the decisions inbox, and
  docs-as-you-go. Read this first. Its "Testing" section has the exact build/test commands
  per area — use those, don't invent your own. Its **"Branch Topology — room for growth"**
  section is the canonical policy for the protected-`main` model and for when a branch
  tier may be added.
- **[`AGENTS.md`](../AGENTS.md)** — short cross-tool orientation for AI agents (points back
  to the docs below).
- **[`specs/README.md`](../specs/README.md)** — the current product-spec index
  (area-grouped product stories). Read this when you need product or feature context.
- **[`RELEASING.md`](../RELEASING.md)** — anything release/versioning/branching related
  (semver, cutting a release, deploy vs. upgrade vs. release).

Keep documentation part of the definition of done: update `docs/guide/` and/or `README.md`
in the same change when user-facing behavior changes.
