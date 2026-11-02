# Write documentation for Agentweaver readers

Use this guide when you write or review documentation, whether you are a
contributor or a coding agent. Start with the reader's goal, not the change
that prompted the article. The reader should not need delivery history to
understand or use Agentweaver.

These are repository authoring materials, not product instructions. Read the
[current-docs audit](./audit.md) for rewrite priorities and the
[source comparison](./sources.md) for the guidance behind these standards.

## Choose one primary purpose

| Reader question | Use this template | Usual home |
| --- | --- | --- |
| How do I learn this by doing it? | [Tutorial or getting started](./templates/tutorial.md) | `docs/guide/` |
| How do I complete this task? | [How-to](./templates/how-to.md) | `docs/guide/` |
| What does this command, setting, field or response mean? | [Reference](./templates/reference.md) | `docs/reference/` |
| Why does this work this way? | [Explanation](./templates/explanation.md) | `docs/experience/` or `docs/architecture/` |
| Why did this fail, and what can I safely do? | [Troubleshooting](./templates/troubleshooting.md) | `docs/guide/` |
| How is this system structured, and what are its trade-offs? | [Architecture deep dive](./templates/architecture.md) | `docs/deep-dive/` |

A planned operation, such as a configuration update, is a how-to. Use the
troubleshooting template when diagnosis determines the action. Do not create
another template for the same procedure with a different audience label.

Give tasks goal-based titles with concrete verbs. “Find CLI commands without
signing in” is a task; “Agentweaver CLI” names a subject. A reference can use a
subject title. Explain only the context needed to complete a task; link to
longer explanations and exact reference entries.

## Establish facts before drafting

1. Identify the reader, goal, applicable version or source branch, starting
   state, and observable finish. A source build is not an installation journey.
2. Read the owning implementation, configuration/schema, and relevant tests.
   Record supporting paths and test names in the PR. Treat existing prose as
   something to verify, not as the only authority.
3. Distinguish current source behavior, published availability, a particular
   deployment, and proposals. State the boundary once where it affects the
   reader's decision. Never infer availability from a directory, route, health
   check, fixture test, or successful documentation build.
4. Preserve exact commands, identifiers, defaults, units, errors, conditions,
   authorization rules, and limits. “Absent,” “empty,” and “false” can have
   different meanings. If evidence conflicts, resolve it with the owner before
   publishing a claim. Describe a material unknown honestly; do not invent it.

Keep roadmap, acceptance receipts, review transcripts, tracking numbers,
file-and-line evidence, and merge-order notes in contributor records or issues.
User guides explain behavior directly. This does not prohibit useful public
source links or technical identifiers such as an ADR number or schema version.
Release notes describe shipped changes; they are not an acceptance log.

## Draft the smallest useful article

Copy the chosen Markdown template into the appropriate docs directory. Replace
every `REPLACE_WITH_…` marker, remove author comments, and omit sections that do
not help the reader. These are prompts, not a requirement to fill empty boxes.
Use the [worked CLI task](../guide/find-cli-commands.md) as a small example.

- Lead with what the reader will achieve and who needs the article. Put
  permissions, tools, platform constraints, cost, and required starting state
  before the first action that depends on them.
- Use numbered steps for an ordered task. Name where to act: shell, working
  directory, UI page, or configuration file. Give one clear action per step.
- Use fenced, language-labeled code blocks for copyable commands. Keep prompts,
  output and editorial notation out of command blocks. State the supported
  shell and OS; do not mix PowerShell and Bash syntax.
- Use commands and options that exist in the applicable source. For a required
  target value, define a clearly named placeholder, how to obtain it, its
  format, and how to quote it before use. Do not make up download URLs,
  deployment hosts, operation IDs, credentials or runnable-looking examples.
  Templates intentionally contain no fake product commands.
- Put warnings before the risky action. Name the affected resource and
  consequence. Identify mutation, billing, downtime, data loss, and separate
  approval requirements where they apply. Never suggest disabling TLS,
  widening permissions, printing secrets, or blindly retrying uncertain writes.
- Say what success looks like at the point the reader needs to check it. For
  asynchronous work, distinguish accepted from completed. Give a safe next
  check when the expected result is absent, and a stop/escalation condition.
- Link the next useful task and the canonical reference. Do not copy entire
  procedures or configuration tables into multiple articles.

Use plain, precise technical English. Address the reader as “you”; use active
verbs and descriptive link text. Define unfamiliar terms before using them.
Keep UI labels and code names exact. Use one term for one concept: a provider
is not a model; authentication is not authorization; an accepted request is
not a completed operation. Explain unavoidable internal terms instead of
writing “proof slot,” “gate green,” or “receipt join” as unexplained user goals.
Do not impose a word count or simplify away a safety condition.

## Tailor architecture deep dives with arc42

Use the [architecture template](./templates/architecture.md) for system or
subsystem deep dives, not for tutorials or marketing. It covers arc42's twelve
sections with reader questions and evidence prompts. Section names and intent
come from [arc42](https://arc42.org/overview/); the prompts are original to this
repository.

For a whole-system description, retain a useful building-block overview. For
a subsystem, link its parent context and shared concepts, then expand only
the important or risky internals. Keep the twelve-section mapping recognizable;
combine or omit inapplicable sections with a short tailoring note. An unresolved
security or deployment question is a known gap, not “not applicable.”

Separate static structure, runtime interactions and deployment topology.
Choose representative normal, failure and recovery scenarios, not every code
path. Explain authority checks, state owners, durable writes, retries and
uncertain outcomes. A dependency diagram cannot establish request order or a
running deployment.

Link existing facts instead of creating parallel versions:

- [Foundation boundaries](../architecture/overview.md),
  [runtime/coordination](../deep-dive/orchestration.md), and
  [deployment](../deep-dive/infra-deployment.md).
- Canonical ADRs in [architecture decisions](../architecture/decisions/0001-platform-architecture.md)
  and [issue-sync delegation](../architecture/decisions/0002-project-issue-sync-delegation.md).
  Preserve each ADR's actual status; Proposed does not mean implemented.
- [Diagram authoring](../diagrams/README.md) for maintained views. Label every
  view's scope and state; provide a caption, useful alt text, editable source
  and full-size image.

ADRs and design documents are excluded from the published site. In a published
article, link to their repository URLs, as the existing architecture overview
does, rather than a nonexistent local site route. Adjust template-relative
links after copying.

For quality requirements, specify a context, stimulus, affected component,
response, measure and verification state. A target is not a measurement.
Do not invent latency targets, guarantees or passed tests to complete a table.

## Review and validate

Before requesting review, check:

- Can the intended reader find the article, identify applicability, complete
  the goal and recognize success without reading an issue?
- Do commands, defaults, error conditions and security boundaries match source?
  Did the review cover branches and failures, not just the happy-path wording?
- Are placeholders defined, commands copyable, and risky actions preceded by
  the required warning? Is recovery conditional on evidence?
- Are explanation, lookup data and procedural steps in their right homes?
  Are links and terminology consistent?
- Are current behavior, proposal and deployment status distinguishable?
  Do prose, diagrams and examples agree?
- Have author comments, unresolved markers and internal delivery evidence been
  removed from published prose?

Run safe examples from the documented starting state where possible. Record
the exact command, platform, source revision, result and limits in the PR.
If an example needs unavailable services, permissions or paid calls, verify
its source contract and mark execution **not run**. Do not invent a successful
outcome or call a fixture run live acceptance.

From the repository root, use the existing checks:

```powershell
npm run docs:build
npm run docs:check-links
npm run docs:check-drift -- --base origin/v1
npm run docs:check-diagrams
npm run docs:check-flagship-diagrams
npm run test:docs-diagrams
```

Use Node.js 24 and Python 3 as described in the
[testing](../guide/testing.md#validate-documentation-changes) and diagram
guides. If VitePress is missing, restore the locked docs dependencies with
`npm ci --prefix docs --ignore-scripts --no-audit --no-fund`. Do not add tooling
to enforce writing preferences.

Add maintained Markdown pages to `docs/docs-source-map.json` so the source
link check sees them. Add published articles to the appropriate navigation
in `docs/.vitepress/config.ts`. Contributor materials under `docs/authoring/`
are excluded from site output and local search, but included in source link
checks. Keep them out of user navigation. The root README and tracked Copilot
instructions link here so humans and agents use the same standard.

Build, link and diagram checks do not prove technical claims or example
behavior. Reviewer source checks remain necessary.
