# Priya Nair — Customer Support Lead

## Identity & background

- Leads a support team handling escalations, ticket queues, and customer-facing knowledge articles.
- Comfortable with CRM exports, ticket IDs, severity labels, and incident handoffs.
- Uses Agentweaver to triage incoming volume and create consistent escalation packets.

## Domain

Customer support operations, escalation management, customer communications, knowledge-base maintenance.

## Goals & motivations

- Reduce repeated manual triage across similar tickets.
- Give engineering concise, evidence-backed escalation summaries.
- Keep customer-facing language accurate, empathetic, and consistent.

## What Priya wants from a multi-agent system

- Agents that classify urgency, detect duplicates, draft responses, and identify missing troubleshooting data.
- Clear separation between internal notes and customer-facing drafts.
- Auditability: why a ticket was grouped, escalated, or considered resolved.

## Behavioral profile & decision patterns

- Starts with batches of messy text or ticket exports rather than a perfect prompt.
- Looks for bulk actions, filters, severity summaries, and exception handling.
- Low tolerance for hallucinated customer facts or sending language without review.
- Reacts to errors by isolating the bad ticket and rerunning a smaller batch.
- Expects private data warnings and redaction affordances.

## Agentweaver scenarios

### Ticket triage swarm

- **Trigger/goal:** Triage a morning queue of product-support tickets into themes, severities, and next actions.
- **Team/agents:** Ticket classifier, duplicate detector, sentiment analyst, escalation reviewer, support scribe.
- **UI steps attempted:** Create support-ops project; paste or upload sample ticket text; configure severity criteria; start triage; inspect grouped findings.
- **Success looks like:** Tickets grouped by issue pattern, severity, missing information, customer impact, and recommended owner.

### Escalation packet creation

- **Trigger/goal:** Prepare an engineering escalation for a high-value customer issue.
- **Team/agents:** Technical summarizer, reproduction-step analyst, customer-impact writer, risk reviewer.
- **UI steps attempted:** Enter ticket thread and known troubleshooting steps; request internal escalation packet; review customer-safe summary separately.
- **Success looks like:** A packet with timeline, impact, repro details, attempted mitigations, ask of engineering, and sanitized customer update.

### Support knowledge-base refresh

- **Trigger/goal:** Update a stale troubleshooting article based on recent tickets.
- **Team/agents:** Pattern miner, documentation writer, CSS reviewer, clarity editor.
- **UI steps attempted:** Provide ticket examples and existing article; run update workflow; compare old versus proposed article; create review task.
- **Success looks like:** Proposed article changes with rationale, common symptoms, step-by-step checks, and warnings for escalation thresholds.

## Failure signals to watch for

- Internal and customer-facing outputs are mixed together.
- The UI cannot handle batches, long pasted text, or partial/malformed ticket data.
- Priya cannot trace why a ticket was assigned a severity or grouped with another issue.
- Generated customer language is overconfident, insensitive, or missing review controls.
- Errors risk exposing private ticket content in logs or shared artifacts.
