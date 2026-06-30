# Track token and AI credit usage

**Issue:** [#29](https://github.com/sabbour/agentweaver/issues/29)  
**Area:** Observability & operations

## User story

As a project owner or admin, I want to see model token usage and AI credit cost across runs, projects, and the app, so that I can understand spend while work is happening and after it completes.

## Context / problem

Agentic workflows can consume significant model resources. Agentweaver surfaces usage where users make operational decisions: run timelines, cards, dashboards, and fleet overview.

## Scope

### In
- live run token counters
- run-card and graph-node cost chips
- project dashboard usage ranges
- usage attributable per agent, not only per model
- usage trend over time on the project dashboard
- agent leaderboard cost visibility
- admin app-wide usage overview

### Out
- billing reconciliation with external invoices
- non-model infrastructure costs
- usage for projects the caller cannot access

## Acceptance criteria

- [ ] Active runs show accumulating token and cost totals as usage arrives.
- [ ] Run cards and graph nodes show compact usage when available.
- [ ] Project dashboards summarize usage over selectable recent ranges.
- [ ] Admins can view app-wide usage; non-admins do not see unauthorized fleet data.
- [ ] Usage distinguishes total, input, output, and per-model breakdowns where available.
- [ ] Project usage can be broken down per agent, consistent with how the leaderboard lists agents.
- [ ] The project dashboard shows AI credit and token usage as a trend over time, alongside the existing throughput trend and over the same selectable range.

## Notable edge cases

- Missing cost data falls back to token counts or hides gracefully.
- Unauthorized app-wide usage requests do not create noisy errors for normal users.
- Very small AI credit values remain readable.
- Usage from runs with no agent attribution is grouped under a clearly labelled bucket.
- Days with no usage still appear in the trend as zero so the timeline stays continuous.
