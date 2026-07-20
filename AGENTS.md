# AGENTS.md

Agentweaver is developed with **Squad**, a team of named AI agents (Trinity, Tank,
Morpheus, Smith, Link, Seraph, Scribe, and others), and welcomes contributions from AI
agents — including GitHub's `@copilot` coding agent. If you are an AI agent (Copilot,
Claude, Cursor, or any other tool) that just cloned this repo, start here.

## Process

**[`CONTRIBUTING.md` → "AI agent contributions"](CONTRIBUTING.md#ai-agent-contributions)**
is the full process doc — read it before making changes. It covers the issue-driven
lifecycle (issue → branch → PR → review → merge), the label-based Squad automation, the
reviewer-rejection protocol, rubber-ducking, the auditable decisions inbox
(`.squad/decisions/inbox/`), and docs-as-you-go (docs are part of the definition of done,
not a follow-up). This file is a pointer, not a replacement — don't skip the source.
For the `dev → release → main` model and release/maintenance branching, see
**[CONTRIBUTING.md → "Branch Topology"](CONTRIBUTING.md#branch-topology)**.

## Build & test

The exact per-area build/test commands live in
**[`CONTRIBUTING.md` → "Testing"](CONTRIBUTING.md#testing)** (and are mirrored by the CI
workflow). Run only the suite(s) relevant to what you changed; use the commands there
rather than guessing, so the two don't drift.

## Other key docs

- **[`RELEASING.md`](RELEASING.md)** — versioning (semver), branching model, and how a
  release differs from a deploy/upgrade.
- **[`specs/README.md`](specs/README.md)** — the current product-spec index (area-grouped
  product stories) for product/feature context.
- **[`.github/copilot-instructions.md`](.github/copilot-instructions.md)** — the
  Copilot-specific version of this orientation.
