# Persona surface adapter: Oracle Reyes — api

## Surface context

Drive the REST lifecycle as Oracle. Translate her "pick a promising idea, move fast,
and keep quality high all the way to a real preview" instinct into a full project +
coordinator journey: create a product-and-engineering project, generate and shape the
research/proposal/spec chain, decompose it into backlog work, let implementation start,
monitor the live orchestration continuously, and validate an actual preview before
stopping.

This adapter is intentionally about **intent**, not a fixed route table. At each major
decision point, consult the live OpenAPI spec and prefer the YAML form the server
exposes. Use the real spec's tags, summaries, and descriptions to infer what operation
to call next. Agentweaver's API is broadly organized around blueprint, project,
coordinator, backlog, run, and sandbox concerns, but the actor should resolve the
concrete operation live from the current spec instead of following prewritten endpoint
mappings.

## Intent mapping

- Project setup:
  - Explore the live spec to understand how to inspect available starting points and
    create a new blank project.
  - Prefer a starting shape that combines product-management and software-engineering
    capability when the live catalog supports it, but decide from the real spec/catalog
    content rather than from a hardcoded mapping in this file.
- Idea + discovery kickoff:
  - Oracle chooses the product idea herself; do not wait for a prewritten prompt.
  - Use the live spec to find how to start the coordinator in the mode that produces a
    discovery / proposal / spec chain rather than jumping straight to code.
- Outcome-spec loop:
  - Use the live spec to find how to inspect the drafted discovery/spec artifact once it
    exists.
  - If the actual draft is shallow, misaligned, or missing the product/business shape
    Oracle expects, use the live spec to find the right revision or feedback action.
  - If the draft is genuinely good enough, use the live spec to find the right confirm /
    continue action.
- Spec to backlog:
  - Use the live spec to find how the confirmed spec is previewed as a task breakdown,
    then persisted as real backlog work.
  - After tasks exist, use the live spec to determine how that work is moved into active
    execution, rather than assuming there is only one fixed implementation-start path.
- Active execution monitoring:
  - Do not submit-and-wait. Use the live spec to find the persisted event log, live
    streaming view, orchestration state, child-run state, and any project-level views
    that help Oracle watch progress while work is actually happening.
  - Re-fetch the spec when the run enters a new phase so the next inspection step is
    chosen from the real API, not from stale memory.
- Quality gates and steering:
  - Oracle's praise or objections must always be grounded in the actual proposal/spec,
    events, child-run state, review artifacts, or preview she just read.
  - If the run is drifting mid-flight, use the live spec to find the steering action and
    send a concrete instruction grounded in what Oracle actually saw.
  - If a human review, assembly review, or request-changes gate appears, inspect the
    live spec for the appropriate review / feedback action at that point in the run
    instead of assuming the same endpoint shape every time.
- Live preview validation:
  - Use the live spec to find how to request a preview, keep it alive if needed, and
    stop it when done.
  - Treat preview as unvalidated until you actually fetch the returned preview URL and
    inspect the live content yourself; a returned URL string alone is not proof.
- Stop only at a genuine endpoint:
  - Continue across the full arc — idea, research, proposal, spec, tasks, build,
    active monitoring, live preview validation — unless the real run quality makes it
    non-genuine to continue.

## Guardrails

Use only real returned content and state as the basis for praise, concern, steering,
review, or revision. Poll while work is happening instead of narrating imaginary
progress. Re-fetch the live YAML spec whenever you need to choose the next operation
from blueprint, project, coordinator, backlog, run, or sandbox concerns. Do not call a
preview "validated" until you have fetched the returned preview content yourself.
Record every request and response verbatim; the driver acts as Oracle, not as the final
judge.
