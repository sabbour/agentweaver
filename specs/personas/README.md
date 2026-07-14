# Agentweaver Personas

These personas define goal-directed behaviors and scenarios for persona-driven self-improvement testing. Each persona can later be converted into an agent definition that drives the running Agentweaver UI dynamically with Playwright.

> **Primary E2E track is now API-driven.** Personas drive Agentweaver through its
> REST API (bearer token) rather than a browser — see
> [`scripts/persona-harness/`](../../scripts/persona-harness/README.md) for the
> working harness, the self-improvement loop mapped to API calls, and how to add
> scenarios.
>
> **Two driving models** (both API-only, both stop at the confirmation gate):
> - **LLM-in-the-loop (primary going forward).** A fresh-context LLM is handed only
>   a persona **brief** (goals/voice/constraints — *not* a script) and drives the
>   run turn-by-turn, deciding each action from what the API actually returns, and
>   **must push back at least twice** grounded in real responses. This surfaces
>   emergent behaviour (e.g. the coordinator revising a plan in response to targeted
>   feedback) that a fixed script can't. Prototyped + live-verified for Priya
>   (`scripts/persona-harness/briefs/priya.md` + `agent-driver/`).
> - **Fixed-script (fallback / fast regression).** One-shot deterministic scenarios
>   in `scripts/persona-harness/scenarios/*.mjs`, kept as references.
>
> In BOTH models the harness is a **driver only** — it drives and captures evidence
> verbatim; it does **not** self-certify whether the produced content is *good*. A
> separate LLM/human **judge** renders that verdict from the captured evidence + the
> "Success looks like" criteria below, using a two-layer method (per-run verdict +
> cross-run meta-aggregation of invariants/divergences/gaps/drift) documented in
> [`scripts/persona-harness/JUDGE.md`](../../scripts/persona-harness/JUDGE.md).
>
> **Scope of what it proves today:** two rungs, under a strict **driver/judge
> separation** — the harness *drives* Agentweaver and *captures evidence*; it does
> **not** self-certify whether the produced content is *good* (that subjective
> verdict is deferred to a separate LLM/human judge that reads the finding JSON +
> the persona's authored "Success looks like" criteria).
> 1. **Scoping rung** — from a plain-language goal through project creation,
>    multi-agent team assembly, and a coordinator-drafted plan that settles at the
>    **outcome-spec confirmation gate** (Priya, Jordan scenarios). The driver's
>    only verdict here is deterministic **platform-correctness** (calls succeeded,
>    a team assembled, the spec left `drafting`, no `run.failed`); it captures the
>    full drafted outcome spec verbatim for the judge to assess against the
>    persona's success criteria.
> 2. **Generation-seam rung** — the harness drives the blueprint/workflow
>    generators and asserts the GENERATED artifacts are *structurally* correct.
>    Structural/schema validation IS legitimate deterministic driver checking (not
>    a subjective heuristic), so it stays a hard pass/fail: the roster excludes
>    reserved system roles (Scribe/Work Monitor/Rai/Coordinator — the class of bug
>    in issue #311), and generated workflows pass the same structural validation
>    the backend enforces (`WorkflowDefinitionLoader.Load`). Per-phase latency and
>    token/cost are recorded in each finding.
>
> It does **not** yet exercise the downstream `confirm → run → review → merge`
> rungs; those remain covered by manual/curl validation until the deeper (opt-in,
> non-deploying-by-default) rung is built. An automated LLM-judge-*calling* pass,
> dynamic LLM-driven scenario generation, and draft-blueprint testbed mode are also
> still pending. The Playwright/browser approach below stays a secondary track for
> frontend-specific UX findings.

## Self-improvement loop

1. **Load a persona + scenario** as the test agent's identity, domain context, goals, and behavioral profile.
2. **Drive the UI dynamically**: the agent explores Agentweaver as that user would, creating projects, assembling agent teams, configuring runs, inspecting outputs, and reacting to ambiguity or errors.
3. **Observe product failures**: capture screenshots, console/network errors, blocked flows, confusing states, missing affordances, malformed outputs, and places where the user cannot verify success.
4. **Produce findings**: turn observed failures into bug reports, feature gaps, UX notes, or acceptance-test ideas.
5. **Create work**: route findings into GitHub issues or Agentweaver tasks for the owning squad, then rerun scenarios after fixes.

The scenarios are intentionally outcome-based rather than brittle click scripts. A Playwright-driving agent should interpret each scenario as a mission: choose reasonable UI paths, recover from detours, and judge success by observable outcomes.

## Personas

| Persona | Domain | Example Agentweaver scenarios |
|---|---|---|
| [Jordan Lee](greenfield-aks-automatic-developer.md) | Greenfield app delivery / AKS Automatic | Blank idea to AKS Automatic; minimal-guidance deployment setup; post-deploy iteration |
| [Casey Morgan](existing-repo-aks-automatic-developer.md) | Existing repo modernization / AKS Automatic | Repo readiness assessment; fill deployment gaps and deploy; failed rollout recovery |
| [Devon Rivera](devon-platform-engineer.md) | Platform engineering / operations | Incident runbook execution; release readiness review; architecture decision follow-up |
| [Maya Chen](maya-market-strategist.md) | Market and competitive strategy | Competitive landscape synthesis; product positioning brief; launch risk scan |
| [Priya Nair](priya-customer-support-lead.md) | Customer support operations | Ticket triage swarm; escalation packet creation; support knowledge-base refresh |
| [Nina Alvarez](nina-legal-compliance-counsel.md) | Legal, privacy, compliance | Policy review board; vendor due-diligence packet; regulatory change impact scan |
| [Omar Haddad](omar-data-analyst.md) | Data analysis / business intelligence | KPI anomaly investigation; survey synthesis; experiment readout |
| [Ari Thompson](ari-research-scientist.md) | Scientific and technical research | Literature review swarm; grant proposal critique; replication-plan review |

## How to convert a persona into a Playwright agent later

- Use **Identity & background** and **Goals & motivations** as system prompt context.
- Use **Behavioral profile** to guide navigation style, patience, error recovery, and confidence thresholds.
- Use each **Scenario** as a test mission with flexible steps and observable success criteria.
- Use **Failure signals** as assertions, heuristics, and bug-classification rules.
- Prefer evidence capture over pass/fail only: screenshots, copied run URLs, generated artifacts, run logs, and issue drafts.
