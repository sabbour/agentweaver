# Simulated GitHub Issue — AIC Token Usage Chart

---

## Step 1 — Classification

| Field | Value |
|-------|-------|
| **Type** | Feature |
| **Domain** | React UI / dashboard (primary: `squad:trinity`) + API data layer (secondary: `squad:tank`) |
| **`go:` label** | `go:needs-research` — requires clarifying the token-usage data API before UI work can begin |
| **Priority** | `priority:p2` — no urgency specified; next sprint |
| **Release** | `release:backlog` |

---

## Step 2 — Labels

```
type:feature,squad:trinity,squad:tank,go:needs-research,priority:p2,release:backlog
```

---

## Step 3 — Issue Body (Feature template)

```markdown
## Summary
Add a stacked area chart to the dashboard that displays AIC token usage over time, broken down by model, with a selectable date range.

## Motivation
Operators and developers using the Agentweaver dashboard currently have no visibility into how many tokens are being consumed by each model (gpt-4o, claude-sonnet, etc.) over time. This makes it impossible to track cost trends, spot anomalies, or plan capacity. A visual, filterable chart closes that gap.

## Proposed solution
- Add a new "Token Usage" section (or page) to the dashboard.
- Render a **stacked area chart** where each band represents one model (e.g. gpt-4o, claude-sonnet, claude-haiku, etc.).
- The X-axis is time; the Y-axis is total AIC token count.
- Provide a **date-range picker** with at least three presets: **Last 7 days**, **Last 30 days**, **Last 90 days**.
- Data must be fetched from a backend endpoint that aggregates token usage per model per day (endpoint TBD — see research note below).

## Acceptance criteria
- [ ] A stacked area chart is visible on the dashboard showing AIC token usage over time.
- [ ] Each model (gpt-4o, claude-sonnet, and any others tracked) appears as a distinct, labelled band.
- [ ] A date-range picker offers "Last 7 days", "Last 30 days", and "Last 90 days" presets; selecting one re-fetches and re-renders the chart.
- [ ] The chart is responsive and renders correctly on standard desktop viewport widths.
- [ ] An empty/zero-data state is handled gracefully (no broken chart).
- [ ] The backend API endpoint returns per-model, per-day token counts for the requested range; `squad:tank` to confirm or create this endpoint.
- [ ] Chart colors are consistent with the existing Fluent design system palette.
- [ ] Unit/component tests cover the date-range picker logic and chart data mapping.

## Out of scope
- Custom date-range selection (arbitrary start/end dates) — only presets for now.
- Exporting chart data as CSV.
- Per-run or per-session breakdown (aggregated per day only in v1).

## Requested by
@sabbour — 2026-07-01
```

---

## Step 4 — `gh` Command

```bash
gh issue create \
  --title "Feature: AIC token usage stacked area chart with date-range picker" \
  --label "type:feature,squad:trinity,squad:tank,go:needs-research,priority:p2,release:backlog" \
  --body "## Summary
Add a stacked area chart to the dashboard that displays AIC token usage over time, broken down by model, with a selectable date range.

## Motivation
Operators and developers using the Agentweaver dashboard currently have no visibility into how many tokens are being consumed by each model (gpt-4o, claude-sonnet, etc.) over time. This makes it impossible to track cost trends, spot anomalies, or plan capacity. A visual, filterable chart closes that gap.

## Proposed solution
- Add a new \"Token Usage\" section (or page) to the dashboard.
- Render a **stacked area chart** where each band represents one model (e.g. gpt-4o, claude-sonnet, claude-haiku, etc.).
- The X-axis is time; the Y-axis is total AIC token count.
- Provide a **date-range picker** with at least three presets: **Last 7 days**, **Last 30 days**, **Last 90 days**.
- Data must be fetched from a backend endpoint that aggregates token usage per model per day (endpoint TBD — see research note below).

## Acceptance criteria
- [ ] A stacked area chart is visible on the dashboard showing AIC token usage over time.
- [ ] Each model (gpt-4o, claude-sonnet, and any others tracked) appears as a distinct, labelled band.
- [ ] A date-range picker offers \"Last 7 days\", \"Last 30 days\", and \"Last 90 days\" presets; selecting one re-fetches and re-renders the chart.
- [ ] The chart is responsive and renders correctly on standard desktop viewport widths.
- [ ] An empty/zero-data state is handled gracefully (no broken chart).
- [ ] The backend API endpoint returns per-model, per-day token counts for the requested range; squad:tank to confirm or create this endpoint.
- [ ] Chart colors are consistent with the existing Fluent design system palette.
- [ ] Unit/component tests cover the date-range picker logic and chart data mapping.

## Out of scope
- Custom date-range selection (arbitrary start/end dates) — only presets for now.
- Exporting chart data as CSV.
- Per-run or per-session breakdown (aggregated per day only in v1).

## Requested by
@sabbour — 2026-07-01"
```

---

## Step 5 — Dispatch Block

```
✅ Filed #<TBD>: Feature: AIC token usage stacked area chart with date-range picker
   https://github.com/<org>/agentweaver/issues/<TBD>

📋 Dispatch:
   squad:smith  — Research first: verify what token-usage data is currently stored
                  (or emitted) and confirm whether a suitable API endpoint exists.
                  Produce a one-page spec (data shape, endpoint contract, chart lib
                  recommendation) before UI work begins. Resolves the go:needs-research gate.
   squad:trinity — Owns the React/Fluent dashboard component once the data contract
                   is confirmed: stacked area chart, date-range picker, empty state,
                   responsive layout, Fluent color palette.
   squad:tank    — Owns the backend API endpoint that aggregates AIC token counts
                   per model per day; coordinates with smith on the data contract.

🏷️  Labels: type:feature, squad:trinity, squad:tank, go:needs-research, priority:p2, release:backlog
```
