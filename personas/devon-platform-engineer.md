# Devon Rivera — Platform Engineer

## Identity & background

- Senior platform engineer responsible for reliability, release gates, and internal developer productivity.
- Comfortable with GitHub, CI/CD, logs, dashboards, incident timelines, and architecture docs.
- Uses Agentweaver as a safer way to coordinate specialized agents before touching production systems.

## Domain

Platform engineering, operations, incident response, release readiness, architecture governance.

## Goals & motivations

- Reduce toil in incident diagnosis and release babysitting.
- Keep human approval on risky production or repository changes.
- Preserve traceable decisions, evidence, and follow-up tasks.

## What Devon wants from a multi-agent system

- Clear project boundaries, run status, logs, and artifacts.
- Specialist teams: investigator, release checker, code reviewer, SRE scribe, risk assessor.
- Confidence that agents work in isolated sandboxes and never merge without review.

## Behavioral profile & decision patterns

- Starts from a concrete incident, failed gate, or release question.
- Looks for templates, run history, logs, and diff previews before launching work.
- High tolerance for technical detail; low tolerance for hidden state or silent failures.
- Reacts to errors by checking logs, rerunning with narrower scope, or opening an issue with evidence.
- Expects status transitions to be explicit: queued, running, blocked, completed, needs review.

## Agentweaver scenarios

### Incident runbook execution

- **Trigger/goal:** A service regression appears after deployment; identify likely cause and propose mitigations.
- **Team/agents:** Incident commander, log analyst, code-change investigator, mitigation planner, scribe.
- **UI steps attempted:** Create or select an operations project; describe the incident; choose or assemble the incident team; start a run; monitor live events; inspect generated timeline, suspected causes, and follow-up tasks.
- **Success looks like:** A completed run with a timeline, ranked hypotheses, evidence links, mitigation options, and explicit unresolved questions.

### Release readiness review

- **Trigger/goal:** Decide whether a pending release is safe to promote.
- **Team/agents:** Release manager, test-results analyst, risk reviewer, documentation checker.
- **UI steps attempted:** Start a release-readiness scenario; provide release notes or repo context; review agent outputs; compare failures versus known flakes; request a final go/no-go summary.
- **Success looks like:** A visible recommendation with risk levels, blocking issues, non-blocking concerns, and a human approval checkpoint.

### Architecture decision follow-up

- **Trigger/goal:** Convert a debated technical decision into implementation tasks and validation checks.
- **Team/agents:** Architect, backend engineer, frontend engineer, QA, scribe.
- **UI steps attempted:** Feed an existing decision or inbox item; ask Agentweaver to create a plan; review proposed boundaries; inspect generated tasks.
- **Success looks like:** Traceable decision context, decomposed work, owners or suggested agents, and a reviewable plan artifact.

## Failure signals to watch for

- Run status is stale, ambiguous, or contradicts the event stream.
- Devon cannot find logs, artifacts, sandbox diff, or approval controls.
- Agents appear to take irreversible action without a review step.
- Technical outputs omit evidence, timestamps, or assumptions.
- Error messages do not identify whether the failure is auth, repo access, runtime, or model/tooling.
