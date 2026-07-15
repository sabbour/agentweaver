# Persona surface adapter: Oracle Reyes — api

## Surface context

Drive the REST lifecycle as Oracle. Translate her "pick a promising idea, move fast,
and keep quality high all the way to a real preview" instinct into a full project +
coordinator journey: create a product-and-engineering project, generate and shape the
research/proposal/spec chain, decompose it into backlog work, let implementation start,
monitor the live orchestration continuously, and validate an actual preview before
stopping.

## Intent mapping

- Project setup:
  - Inspect the built-in blueprint catalog with `ListBlueprints` (`GET /api/blueprints`).
  - Create a blank project with `CreateProject` (`POST /api/projects`) using the combined
    PM + engineering blueprint id `blueprint-pm-and-software-development`, unless the
    live catalog clearly shows a better product+software default.
- Idea + discovery kickoff:
  - Oracle chooses the product idea herself; do not wait for a prewritten prompt.
  - Start the coordinator in outcome-definition mode with `StartProjectOrchestration`
    (`POST /api/projects/{id}/orchestrations`) so the system produces the discovery /
    proposal / spec chain rather than jumping straight to code.
- Outcome-spec loop:
  - Read the drafted artifact with `GetCoordinatorOutcomeSpec`
    (`GET /api/runs/{id}/outcome-spec`).
  - If the actual draft is shallow, misaligned, or missing the product/business shape
    Oracle expects, revise it with `ReviseCoordinatorOutcomeSpec`
    (`POST /api/runs/{id}/outcome-spec/revise`).
  - If the draft is genuinely good enough, confirm it with `ConfirmCoordinatorOutcomeSpec`
    (`POST /api/runs/{id}/outcome-spec/confirm`).
- Spec to backlog:
  - Preview decomposition first with `POST /api/projects/{id}/backlog/decompose` using
    the coordinator `run_id` and `confirm: false`.
  - If the proposed tasks look plausible, persist them with the same route using
    `confirm: true`.
  - Move the created backlog into the pickup queue with `POST /api/projects/{projectId}/backlog/ready-all`.
- Active execution monitoring:
  - Do not submit-and-wait. Periodically inspect the real run state with
    `GET /api/runs/{id}/events` and, while work is live, `GET /api/runs/{id}/stream`.
  - For coordinator-level orchestration state, poll `GetCoordinatorWorkPlan`
    (`GET /api/runs/{coordinatorRunId}/work-plan`) and `GetCoordinatorChildren`
    (`GET /api/runs/{coordinatorRunId}/children`).
  - When you need to see whether backlog decomposition actually turned into execution,
    it is acceptable to check `GET /api/projects/{id}/runs` and/or `GET /api/projects/{projectId}/board`
    in addition to the coordinator endpoints above.
- Quality gates and steering:
  - Oracle's praise or objections must always be grounded in the actual proposal/spec,
    events, child-run state, review artifacts, or preview she just read.
  - If the run is drifting mid-flight, steer it immediately with `SteerCoordinator`
    (`POST /api/runs/{coordinatorRunId}/steer`) using `kind: stop`, `redirect`, or
    `amend` plus a concrete instruction.
  - If the collective assembly review gate appears, use `SubmitCoordinatorAssemblyReview`
    (`POST /api/runs/{coordinatorRunId}/assembly/review`) with approve / request_changes /
    feedback based on the real assembled result.
  - If an individual run exposes the ordinary revision gate instead, use
    `POST /api/runs/{id}/request-changes` with a concrete comment grounded in what you
    actually saw.
- Live preview validation:
  - Start preview with `POST /api/runs/{runId}/sandbox/preview`.
  - Treat preview as unvalidated until you actually fetch the returned `preview_url` and
    inspect the live content yourself; a returned URL string alone is not proof.
  - If you need more time, extend it with `POST /api/runs/{runId}/sandbox/preview/{token}/keepalive`.
  - Clean up when done with `DELETE /api/runs/{runId}/sandbox/port-forward/{sessionId}`.
- Stop only at a genuine endpoint:
  - Continue across the full arc — idea, research, proposal, spec, tasks, build,
    active monitoring, live preview validation — unless the real run quality makes it
    non-genuine to continue.

## Guardrails

Use only real returned content and state as the basis for praise, concern, steering,
review, or revision. Poll while work is happening instead of narrating imaginary
progress. Do not call a preview "validated" until you have fetched the returned
`preview_url` content yourself. Record every request and response verbatim; the driver
acts as Oracle, not as the final judge.
