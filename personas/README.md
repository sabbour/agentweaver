# Agentweaver Personas

These personas define goal-directed behaviors and scenarios for persona-driven self-improvement testing. Each persona can later be converted into an agent definition that drives the running Agentweaver UI dynamically with Playwright.

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
| [Devon Rivera](devon-platform-engineer.md) | Platform engineering / operations | Incident runbook execution; release readiness review; architecture decision follow-up |
| [Maya Chen](maya-market-strategist.md) | Market and competitive strategy | Competitive landscape synthesis; product positioning brief; launch risk scan |
| [Priya Nair](priya-customer-support-lead.md) | Customer support operations | Ticket triage swarm; escalation packet creation; support knowledge-base refresh |
| [Luca Moretti](luca-content-director.md) | Content production | Campaign content pipeline; executive blog post room; localization readiness pass |
| [Nina Alvarez](nina-legal-compliance-counsel.md) | Legal, privacy, compliance | Policy review board; vendor due-diligence packet; regulatory change impact scan |
| [Omar Haddad](omar-data-analyst.md) | Data analysis / business intelligence | KPI anomaly investigation; survey synthesis; experiment readout |
| [Elena Park](elena-learning-designer.md) | Education and enablement | Course design studio; learner feedback synthesis; assessment quality review |
| [Samir Patel](samir-founder-operator.md) | Startup operations / strategy | Board memo prep; hiring plan workshop; operating cadence design |
| [Ari Thompson](ari-research-scientist.md) | Scientific and technical research | Literature review swarm; grant proposal critique; replication-plan review |

## How to convert a persona into a Playwright agent later

- Use **Identity & background** and **Goals & motivations** as system prompt context.
- Use **Behavioral profile** to guide navigation style, patience, error recovery, and confidence thresholds.
- Use each **Scenario** as a test mission with flexible steps and observable success criteria.
- Use **Failure signals** as assertions, heuristics, and bug-classification rules.
- Prefer evidence capture over pass/fail only: screenshots, copied run URLs, generated artifacts, run logs, and issue drafts.
