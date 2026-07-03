# Simulated GitHub Issue

## Issue Title
feat: Dashboard — AIC token usage over time (stacked area chart with date range picker)

## Issue Body

### Summary
Add a token usage visualization to the dashboard that shows AIC (AI Calls) token consumption over time, broken down by model, rendered as a stacked area chart with a configurable date range selector.

---

### Feature Description

Users need visibility into how tokens are being consumed across different AI models over time. This feature adds a new chart panel to the dashboard with the following capabilities:

#### Chart Type
- **Stacked area chart** where each layer represents a distinct model (e.g., `gpt-4o`, `claude-sonnet`, etc.)
- The Y-axis represents total token count (input + output, or optionally separated)
- The X-axis represents time (day granularity)

#### Date Range Picker
Provide a control to select from preset ranges:
- Last **7 days**
- Last **30 days**
- Last **90 days**

Optionally support a custom date range input for advanced users.

#### Model Breakdown
Each model used during the selected time range should appear as a distinct colored band in the stacked area chart. Models to support at minimum:
- `gpt-4o`
- `claude-sonnet` (and variants, e.g., `claude-sonnet-3.5`)
- Any other model surfaced in session/event data

#### Tooltip / Hover State
On hover over the chart, show a tooltip with:
- Date
- Per-model token count
- Total token count for that day

---

### Acceptance Criteria

- [ ] A stacked area chart renders on the dashboard showing token usage over time
- [ ] The chart correctly aggregates token counts per model per day
- [ ] The date range picker supports at minimum: Last 7 days, Last 30 days, Last 90 days
- [ ] Changing the date range re-fetches/re-filters data and re-renders the chart
- [ ] Each model is represented by a distinct color with a legend
- [ ] Hovering over the chart shows a tooltip with date, per-model counts, and total
- [ ] Chart handles the case where no data exists for a date range gracefully (empty state)
- [ ] New models that appear in the data are automatically included (no hardcoded model list)

---

### Suggested Implementation Notes

- Token usage data likely comes from the `events` table (`usage_input_tokens`, `usage_output_tokens`, `usage_model` columns)
- Query should group by `date_trunc('day', timestamp)` and `usage_model`
- Consider a backend API endpoint `/api/token-usage?range=7d|30d|90d` returning aggregated daily data
- Use an existing charting library already in the project (e.g., Recharts, Chart.js, Victory) if available; otherwise pick one
- Chart component should be self-contained and reusable

---

### Labels
`enhancement`, `dashboard`, `analytics`, `ui`

### Milestone
Backlog

---

## Simulation Notes

- **Tool used**: Direct file creation (no `gh` CLI invoked per task constraints)
- **Approach**: Manually composed a well-structured GitHub issue following conventional feature-request format
- **Repo context**: Agentweaver project — the `events` table with `usage_model`, `usage_input_tokens`, `usage_output_tokens` columns is the likely data source based on codebase knowledge
