<!--
Original Agentweaver authoring prompts derived from arc42's 12-section model:
https://arc42.org/overview/ and https://docs.arc42.org/
Read ../README.md relative to this template's parent directory for authoring rules.
Replace markers and remove author comments before publication.
Retain a whole-system building-block overview. For a subsystem, link parent/shared
views. Combine or omit inapplicable sections with a short tailoring note, not empty
headings. Unknown critical facts remain explicit gaps with an owner/next check.
-->
# REPLACE_WITH_SYSTEM_OR_SUBSYSTEM architecture

REPLACE_WITH_READER_PURPOSE_SCOPE_AND_APPLICABLE_VERSION_OR_SOURCE

<!-- Explain tailoring here: which views are delegated to canonical pages and why.
Identify whether a view describes implemented source, a proposal, a target topology
or an observed deployment. Never use "current" to conceal that distinction. -->

## 1. Introduction and Goals

<!-- Purpose: explain the problem and architectural drivers.
Reader: who depends on this system, and what matters most to them?
Evidence: essential use cases, stakeholder needs and prioritized quality goals.
Link detailed requirements and section 10 instead of copying their full contents. -->
REPLACE_WITH_PROBLEM_STAKEHOLDERS_AND_PRIORITIZED_GOALS

## 2. Constraints

<!-- Purpose: show limits on design freedom.
Reader: what must the design obey, and what is merely a chosen approach?
Evidence: technical/organizational constraints and their origin or rationale.
Keep mandatory constraints distinct from decisions that could change. -->
REPLACE_WITH_CONSTRAINTS_AND_THEIR_REASONS

## 3. Context and Scope

<!-- Purpose: draw the boundary between the system and its users/neighbors.
Reader: what is inside, outside, and exchanged across each boundary?
Evidence: context diagram or partner/input/output table, protocols and trust
boundaries. Separate business exchanges from technical channels when useful. -->
REPLACE_WITH_CONTEXT_BOUNDARIES_AND_EXTERNAL_INTERFACES

## 4. Solution Strategy

<!-- Purpose: explain the few approaches that shape the architecture.
Reader: why these technologies and boundaries rather than the alternatives?
Evidence: goal -> approach -> trade-off -> canonical decision/view link.
Summarize; do not duplicate ADR rationales or the component inventory. -->
REPLACE_WITH_KEY_APPROACHES_AND_TRADE_OFFS

## 5. Building Block View

<!-- Purpose: make static structure and ownership understandable.
Reader: which blocks own which responsibilities, data and interfaces?
Evidence: structural diagram plus responsibility/interface table and useful source
locations. Distinguish library, adapter, host and external service. Expand only
important/risky internals; a dependency arrow is not a runtime message. -->
REPLACE_WITH_STATIC_STRUCTURE_OWNERSHIP_AND_DEPENDENCIES

## 6. Runtime View

<!-- Purpose: show how the blocks cooperate in relevant scenarios.
Reader: what happens in order, and what changes when a step fails or is retried?
Evidence: representative normal, failure and recovery sequences or numbered flows.
Show actor, authority check, state owner, durable write, async boundary, retry and
uncertain result where applicable. Link existing canonical flows instead of
redrawing them with different facts. Match participants to section 5. -->
REPLACE_WITH_SCENARIOS_AND_RUNTIME_VIEW_LINKS

## 7. Deployment View

<!-- Purpose: map software blocks onto execution infrastructure.
Reader: where does each block run, and which environment/dependencies does it need?
Evidence: deployment diagram or software-to-node table, networks/trust zones,
storage, identity, configuration ownership and operational prerequisites.
Label target vs observed topology and meaningful environment differences.
Link the canonical deployment/runbook; never infer running workloads from YAML. -->
REPLACE_WITH_DEPLOYMENT_MAPPING_STATE_AND_CANONICAL_LINKS

## 8. Crosscutting Concepts

<!-- Purpose: explain mechanisms shared by several blocks once.
Reader: which common rules must implementations preserve?
Evidence: canonical concepts for authorization, consistency, retries, identity,
secrets, observability or another relevant concern. Select only useful concepts,
name their scope/exceptions and link concrete flows; no exhaustive topic checklist. -->
REPLACE_WITH_SHARED_CONCEPTS_AND_CANONICAL_LINKS

## 9. Architecture Decisions

<!-- Purpose: let readers find consequential choices and their rationale.
Reader: what was decided, what alternatives lost, and is the decision accepted?
Evidence: canonical ADR links with actual status and consequences. Do not create
a second decision log. Repository-only ADRs need repository URLs in site pages. -->
REPLACE_WITH_ADR_INDEX_AND_STATUSES

## 10. Quality Requirements

<!-- Purpose: make quality goals assessable rather than promotional.
Reader: under which conditions must the system do what, and how is that assessed?
Evidence: focused usage, failure or change scenarios. Reference section 1's goals.
For each: context/environment, source/stimulus, affected block, response, measure,
and verification evidence/state. Separate proposed target from observed result.
Unknown targets stay unknown; do not invent an SLO or claim fixture proof is live. -->

| Scenario | Context and stimulus | Affected block and response | Measure | Verification state |
| --- | --- | --- | --- | --- |
| REPLACE_WITH_QUALITY_CONCERN | REPLACE_WITH_CONDITION_AND_TRIGGER | REPLACE_WITH_BLOCK_AND_RESPONSE | REPLACE_WITH_MEASURABLE_CRITERION | REPLACE_WITH_EVIDENCE_OR_KNOWN_GAP |

## 11. Risks and Technical Debt

<!-- Purpose: expose important remaining weaknesses and uncertainty.
Reader: what could fail, who is affected, and what is the next mitigation?
Evidence: prioritized risks/debt with consequence, owner and next action.
Keep unknowns and unsupported claims visible; link tracking in contributor
records, but explain the actual limitation directly in user-facing prose. -->
REPLACE_WITH_PRIORITIZED_RISKS_IMPACT_AND_FOLLOW_UP

## 12. Glossary

<!-- Purpose: give stakeholders one meaning for important terms.
Reader: which terms or acronyms could I misunderstand?
Evidence: short domain definitions consistent with canonical concepts. Link an
existing glossary where possible; omit trivial vocabulary and code inventories. -->
REPLACE_WITH_NECESSARY_TERMS_AND_DEFINITIONS
