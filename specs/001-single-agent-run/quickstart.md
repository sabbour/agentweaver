# Quickstart: Single-Agent File-Editing Run

This guide validates the feature end-to-end against the authoritative API and
both clients. It is a run/validation guide; implementation details live in
[plan.md](./plan.md), [data-model.md](./data-model.md), and
[contracts/](./contracts/).

## Prerequisites

- .NET 9 SDK installed
- Node.js 20+ (for the Web UI)
- `git` available on PATH (worktree, diff, merge lifecycle)
- A model-source credential for at least one provider:
  - GitHub Copilot SDK auth (see Copilot SDK auth docs), or
  - Microsoft Foundry endpoint + key
- A local git repository with a target originating branch to run against

## Setup

```bash
# Backend (authoritative API)
cd backend/Scaffolder.Api
dotnet restore
dotnet run            # serves http://localhost:3000

# Web UI (separate terminal)
cd clients/web
npm install
npm run dev

# CLI (separate terminal)
cd clients/cli
dotnet run -- --help
```

Configure the run-root directory and provider credentials via environment/app
settings before starting a run.

## Validation Scenarios

Each scenario maps to a user story and its acceptance criteria in
[spec.md](./spec.md). API shapes are defined in
[contracts/run-api.yaml](./contracts/run-api.yaml); streamed step payloads in
[contracts/run-step-event.schema.json](./contracts/run-step-event.schema.json).

### Scenario 1 - Submit a task and get a file-editing result (US1, P1)

1. Create a run (CLI or Web UI), choosing an originating branch, a model source,
   and a natural-language task (e.g., "add a license header to every source file").
2. Expected: API returns `201` with a `Run` whose `status` starts at `queued`,
   then transitions to `running`. A session/worktree is created from the branch.
3. Expected: the run reaches a terminal `completed` state and a diff against the
   originating branch is available via `GET /runs/{runId}/diff`.
4. Verify: the originating branch is untouched; all changes live only in the
   run's worktree (SC-005 for the no-merge case).

### Scenario 2 - Watch a run's steps live (US2, P2)

1. While a run is in progress, open the live stream: `GET /runs/{runId}/stream`
   (SSE) from the CLI and from the Web UI simultaneously.
2. Expected: agent messages, tool calls (`read_file`/`write_file` with path), and
   tool results appear in order, each with a monotonic `sequence`, without manual
   refresh (FR-011, SC-006).
3. Expected: both clients show identical steps; a final `lifecycle` event marks
   the run finished.
4. Verify reconnect: disconnect a client mid-run and reconnect with
   `Last-Event-ID`; the client resumes without missing steps.

### Scenario 3 - Choose the model source (US3, P3)

1. Submit two runs, selecting `copilot_sdk` for one and `microsoft_foundry` for
   the other.
2. Expected: each run records and uses the selected provider; only the two
   supported providers are offered.
3. Verify rejection: submitting an unsupported `modelSource` returns `400`
   (FR-009).

### Scenario 4 - Review and approve the merge back (US4, P4)

1. For a `completed` run (now `awaiting_review`), review the diff, then
   `POST /runs/{runId}/review` with `{ "decision": "approve" }`.
2. Expected: status transitions to `merged`; the originating branch now contains
   exactly the reviewed changes (SC-004).
3. Decline path: in a separate run, submit `{ "decision": "decline" }`; expected
   status `declined`, originating branch byte-for-byte unchanged (SC-005), and the
   worktree retained for reference.
4. Conflict path: if the originating branch diverged and changes conflict, expect
   `merge_conflict` surfaced to the human; the branch is not modified.

### Sandbox enforcement check (FR-006/FR-007, SC-002)

1. Drive a run whose task induces a path escape attempt (absolute path, `..`
   traversal, or a symlink resolving outside the artifact directory).
2. Expected: the tool result is `rejected` with `errorCode: PATH_ESCAPE`; nothing
   outside the artifact directory is read or written.

### Bounded-run check (FR-013, FR-029)

1. Submit a run with a low `maxSteps` (or `maxDurationSeconds`) against a task the
   agent will not finish quickly.
2. Expected: the run ends in terminal status `bounded`, visible in both clients.

### Content-safety failure check (FR-025, SC-008)

1. Configure a test model provider stub or use an integration environment that returns
   a content-safety failure signal for a known prompt.
2. Submit a run using that prompt.
3. Expected: no content-safety-failing output reaches the CLI or Web UI; the run ends
   in terminal status `failed`; the event log contains a `run.failed` event with
   `failureReason` indicating a content-safety failure; the event is visible by
   replaying the stream from `lastSeenSequence = 0`.
4. Verify: `GET /runs/{runId}/stream` returns the `run.failed` event; no prior
   `agent.message` or `tool.call` event in the stream contains the flagged content.

### No-secrets-in-outputs check (FR-026, SC-009)

1. Submit a run whose task prompt includes a clearly identifiable token string (e.g.,
   `SECRET_TOKEN_TEST_XYZ`); ensure the working area files also contain a similarly
   identifiable pattern.
2. Let the run complete normally.
3. Inspect all event log payloads via `GET /runs/{runId}/stream` (replay from
   `lastSeenSequence = 0`), the diff via `GET /runs/{runId}/diff`, and the
   operational record (internal inspection or admin endpoint).
4. Expected: the identifiable token string does not appear verbatim in any event log
   `payload` field, in the diff output, or in any client-facing response body.

### Governance traceability check (FR-027, FR-028, SC-010)

1. Submit a run that exercises multiple governance policy paths: one valid tool call
   (sandbox pass), one rejected tool call (path escape), one model-source validation
   (valid enum), and the human-approval gate (approve the run).
2. After the run completes and is approved, inspect the OperationalRecord for the run
   (internal or admin endpoint).
3. Expected: `policyTrace` contains a timestamped entry for each governance decision
   — at minimum: the tool permission grants, the PATH_ESCAPE rejection, the
   model-source validation pass, the run-limit evaluation, and the human-approval gate
   outcome.
4. Verify: a compliance reviewer can reconstruct all policy outcomes for the run solely
   from the `policyTrace` without requiring access to the full event log (SC-010).

## Pass Criteria

- All four user-story scenarios pass from the CLI and, independently, from the
  Web UI with identical outcomes (SC-003).
- Sandbox and bounded-run checks behave exactly as specified.
- Content-safety failure check: 100% of flagged outputs withheld, recorded, and the
  run terminated; zero flagged content reaches any client (SC-008).
- No-secrets check: zero occurrences of the identifiable test token in any event log
  payload, client output, or operational record (SC-009).
- Governance traceability check: `policyTrace` in OperationalRecord covers all
  policy decision points for the run, enabling full compliance reconstruction (SC-010).
- No emojis appear in any product output, logs, or UI (Principle VII, NFR-002).
