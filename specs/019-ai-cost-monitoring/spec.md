# Feature Specification: AI Credit and Token Usage Monitoring

**Feature Branch**: `019-ai-cost-monitoring`

**Created**: 2026-06-29

**Status**: Draft

**Input**: User description: "AI Credits/tokens/cost monitoring at an app level, at a project level, at an orchestration/task level, at an agent run level. Works with GitHub only. Visible on workflow and outside the workflow on dashboards."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Real-Time Token Visibility During a Run (Priority: P1)

A developer kicks off an agent run inside a workflow. As the agent executes turn by turn, the run view shows a live-updating token counter — input tokens, output tokens, and running total — so the developer can see how much capacity the run is consuming while it is still active. When the run finishes, the final token total is frozen and remains visible in the run detail view permanently.

**Why this priority**: Immediate feedback during execution is the highest-value signal. Without it, users have no way to detect runaway or unexpectedly expensive runs until they are already over budget.

**Independent Test**: Can be fully tested by starting any agent run and verifying that the token counts in the run view update with each agent turn, then match the totals on the completed run detail page.

**Acceptance Scenarios**:

1. **Given** an agent run is in progress, **When** the agent completes a turn that consumes tokens, **Then** the run view updates its token display within one refresh cycle to reflect the new totals.
2. **Given** an agent run has completed, **When** a user opens the run detail view, **Then** the finalized input token count, output token count, and total token count are displayed alongside the run status.
3. **Given** an agent run is in progress, **When** the user is viewing the run via the MCP server, **Then** token usage is available through the run's step stream or status endpoint.

---

### User Story 2 - Workflow-Level Cost Summary (Priority: P2)

A user reviews a completed workflow orchestration. On the workflow run detail page, they see a cost summary table breaking down token consumption by task — each task shows the total tokens it used, making it easy to identify which tasks in the orchestration are the most expensive.

**Why this priority**: Workflows orchestrate multiple tasks and agent runs; knowing cost at the task level enables users to optimize the most expensive steps and justify workflow designs to stakeholders.

**Independent Test**: Can be fully tested by running a multi-task workflow and verifying that the workflow run detail page shows a per-task token breakdown that sums to the workflow total.

**Acceptance Scenarios**:

1. **Given** a workflow run has completed with multiple tasks, **When** the user views the workflow run detail, **Then** each task shows its own token totals (input, output, total) and the workflow shows the aggregate.
2. **Given** a workflow run is in progress, **When** a task completes, **Then** its token usage is immediately visible in the workflow run view without requiring a page refresh beyond the normal live update mechanism.
3. **Given** a workflow run detail is viewed via the MCP server, **Then** per-task token usage is included in the workflow run data.

---

### User Story 3 - Project-Level Usage Dashboard (Priority: P3)

A project owner opens the project's usage dashboard and sees a summary of all token consumption within that project over a selectable time range. They can see totals by workflow, identify the top five most expensive workflows, and drill down to individual runs. The data is scoped strictly to their project — they cannot see usage from other projects.

**Why this priority**: Project owners need to understand the AI cost footprint of their project over time to manage budgets, justify resource allocation, and identify optimization opportunities.

**Independent Test**: Can be fully tested by navigating to a project's dashboard, selecting a date range, and verifying that the displayed token totals match the sum of all runs completed for that project in the selected period, with no data from other projects appearing.

**Acceptance Scenarios**:

1. **Given** a project has completed runs in the last 30 days, **When** a project member opens the project usage dashboard with the default date range, **Then** they see total tokens consumed, broken down by workflow.
2. **Given** a user is a member of Project A but not Project B, **When** the user views the project usage dashboard for Project A, **Then** only Project A's usage data is visible and Project B's data is inaccessible.
3. **Given** a user changes the time range filter on the project dashboard, **Then** the displayed totals and breakdown update to reflect only usage within the selected range.

---

### User Story 4 - App-Level Administrative Overview (Priority: P4)

An administrator opens the application-wide usage dashboard and sees total token consumption across all projects for a configurable time period. They can see which projects are consuming the most tokens, review daily or weekly trends, and export a summary. Administrators can see all projects; non-administrators cannot access this view.

**Why this priority**: Administrators need a global view to manage capacity, enforce fair use across teams, and report overall AI spend to stakeholders.

**Independent Test**: Can be fully tested by logging in as an administrator, navigating to the app-level dashboard, and verifying that the totals equal the sum of all project-level totals for the same period.

**Acceptance Scenarios**:

1. **Given** the user has an administrator role, **When** they open the app-level usage dashboard, **Then** they see total tokens consumed across all projects, with a breakdown by project.
2. **Given** the user does not have an administrator role, **When** they attempt to access the app-level usage dashboard, **Then** access is denied and they are redirected to their project-scoped view.
3. **Given** the administrator views the app-level dashboard, **When** they select a specific project row, **Then** they are taken to that project's usage dashboard.

---

### Edge Cases

- What happens when a run is cancelled mid-execution? Token usage captured before cancellation must be recorded and attributed correctly, not discarded.
- How does the system handle a run that spans multiple models (if the model is changed between turns)? Usage must be recorded per call and aggregated under the run, with the model noted per call.
- What if the GitHub Copilot API does not return usage data for a specific call? The system must record the call with a null token count and surface a warning indicator, not silently omit the entry.
- What happens when a workflow has hundreds of tasks? The per-task breakdown must paginate or truncate with a "show all" option rather than rendering an unmanageable table.
- How are historical records affected when a project is deleted? Usage records must be retained for the configured retention period even after a project is deleted, accessible only to administrators.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The system MUST capture input token count, output token count, and total token count for every GitHub Copilot API call made during an agent run.
- **FR-002**: Every token usage record MUST be associated with the four-level hierarchy: application instance > project > workflow orchestration run > agent run.
- **FR-003**: The system MUST aggregate token usage at each level of the hierarchy — agent run, orchestration run, project, and application — so that each level's total equals the sum of its children.
- **FR-004**: Token usage data MUST be updated in the run detail view within one refresh cycle of each agent turn completing.
- **FR-005**: The system MUST provide a project-level usage dashboard accessible to all members of that project, scoped strictly to that project's data.
- **FR-006**: The system MUST provide an application-level usage dashboard accessible only to users with an administrator role.
- **FR-007**: Both dashboards MUST support filtering by a user-selectable time range (minimum selectable ranges: last 7 days, last 30 days, last 90 days, custom date range).
- **FR-008**: The project dashboard MUST display a breakdown of token usage by workflow, identifying the top consumers within the selected period.
- **FR-009**: The application dashboard MUST display a breakdown of token usage by project, with the ability to drill down to the project-level dashboard.
- **FR-010**: Token usage data MUST be exposed through the backend API so that both the Web UI and MCP server can access it without containing any aggregation logic of their own.
- **FR-011**: The MCP server MUST expose token usage data for runs, orchestration runs, and projects through dedicated tool endpoints.
- **FR-012**: Token usage data MUST be retained for a minimum of 90 days from the date of the run.
- **FR-013**: The system MUST NOT expose one project's usage data to users who are not members of that project.
- **FR-014**: Token usage records MUST survive run cancellation and partial execution — any tokens consumed before a run ends in any terminal state MUST be recorded.
- **FR-015**: The system MUST record which model was used for each GitHub Copilot call, associated with its token usage record. Both the project-level and application-level dashboards MUST display a breakdown of token usage by model, allowing users to see how consumption is distributed across the models available within GitHub Copilot.

### Key Entities

- **TokenUsageRecord**: Represents token consumption for a single GitHub Copilot API call. Key attributes: run identifier, call timestamp, model used, input token count, output token count. Links to the agent run that triggered it.
- **RunUsageSummary**: Aggregated token totals for a single agent run. Derived from all TokenUsageRecords for that run.
- **OrchestrationUsageSummary**: Aggregated token totals for a workflow orchestration run, summing all agent runs within it.
- **ProjectUsageSummary**: Time-windowed aggregate of token usage across all orchestration runs within a project.
- **AppUsageSummary**: Time-windowed aggregate across all projects within the application instance.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Token usage for a completed agent turn is reflected in the run detail view within 5 seconds of the turn completing, under normal operating conditions.
- **SC-002**: The project-level dashboard loads and renders usage data for the default 30-day range within 3 seconds for projects with up to 500 runs in that period.
- **SC-003**: Token counts displayed in the system match the values returned by the GitHub Copilot API for those calls, with zero discrepancy for any individual call.
- **SC-004**: A user with access to a project can identify the single most token-intensive workflow in that project for any 30-day window without more than two navigation steps from the project dashboard.
- **SC-005**: Zero usage data from Project B is ever visible to a user who is a member of Project A only, verified across all API endpoints and UI surfaces.
- **SC-006**: All token usage records generated during a run are persisted even if the run terminates abnormally (crash, timeout, cancellation).

## Assumptions

- GitHub Copilot is the only model provider used by this application, consistent with the constitution's Principle II. Cost monitoring is therefore scoped entirely to GitHub Copilot token usage and does not need to support other providers.
- GitHub Copilot's API returns token usage metadata (input tokens, output tokens) in the response to each model call. If a specific call or model variant does not return this metadata, the record is stored with null token counts and flagged.
- Token counts are treated as the primary cost signal. Mapping tokens to a monetary cost figure is considered out of scope for this feature, as GitHub Copilot is subscription-based and does not publish per-token pricing. Raw token counts are sufficient for relative cost analysis.
- Per-model token breakdowns are displayed in both the project-level and app-level dashboards, reflecting the user's choice of model within GitHub Copilot.
- Access control relies on existing project membership and administrator role definitions already present in the system. This feature does not introduce new role types.
- Mobile support for the cost dashboards is out of scope for the initial version.
- Export/download of usage data (CSV, etc.) is out of scope for the initial version but should be considered a natural follow-on.
- Usage data is stored separately from run event logs; it is a derived summary, not a replacement for the full event stream.
