// Scenario playbook: Priya Nair — "Ticket triage swarm".
//
// Persona: specs/personas/priya-customer-support-lead.md
// Maps the persona's first scenario onto Agentweaver's coordinator API. Priya
// pastes a messy batch of support tickets and expects a multi-agent team to
// group them by theme/severity/impact and recommend owners — a content/analysis
// task with NO infrastructure deployment, which is why it is a safe first
// end-to-end scenario to run against staging.
//
// Mapping to the REST API (all driven by lib/runner.mjs):
//   1. POST /api/projects            create a support-ops project seeded with the
//                                    Content Authoring blueprint (roster + workflows)
//   2. GET  /api/projects/{id}/team  confirm a multi-agent team was assembled
//   3. POST /api/projects/{id}/orchestrations  (start_mode: defineOutcome)
//                                    Priya "starts the run"; the coordinator drafts a
//                                    confirmable outcome spec and suspends at the gate
//   4. GET  /api/runs/{id}/outcome-spec  the reviewable plan Priya would inspect
//   5. GET  /api/runs/{id}/events    evidence trail (coordinator/workflow activity)
//
// DRIVER/JUDGE SEPARATION: this scenario does NOT judge whether the drafted plan
// is "good". The driver only drives the API and captures evidence. Below, the
// non-gating `judgeContext(evidence)` hook emits deterministic REFERENCE DATA
// (the expected ticket IDs, the raw batch, the persona's stated expectations)
// that a downstream LLM judge compares against the drafted spec to render the
// P1 output-quality verdict. Nothing here computes pass/fail.

const SAMPLE_TICKETS = `
[TICKET-4821] sev? — "App crashes on export to PDF, losing my report. Been happening since the update yesterday. Chrome on Windows." — customer: Contoso (enterprise, 900 seats)
[TICKET-4822] — "can't export pdf!!! this is unacceptable, third time this week" — customer: Contoso (same reporter, different thread)
[TICKET-4830] — "Feature request: dark mode would be nice someday" — customer: individual free tier
[TICKET-4835] — "Login loops back to sign-in page after SSO, whole team blocked, ~40 people can't work" — customer: Fabrikam (enterprise)
[TICKET-4840] — "Invoice shows wrong VAT for EU billing, finance flagged it" — customer: Northwind (mid-market)
`.trim();

export default {
  id: 'priya-ticket-triage',
  personaFile: 'priya-customer-support-lead.md',
  personaScenario: 'Ticket triage swarm',
  title: 'Priya Nair — Ticket triage swarm (API-driven)',

  // Seed the project with a multi-role content/analysis team, no infra roles.
  blueprintId: 'blueprint-content-authoring',
  projectPrefix: 'persona-priya-triage',

  // Priya "types a plain-language goal" — a messy batch, not a perfect prompt.
  buildGoal() {
    return [
      'Triage this morning\'s support ticket queue. Group the tickets by issue theme,',
      'assign a severity to each with a one-line justification, flag duplicates,',
      'note missing troubleshooting information, estimate customer impact, and',
      'recommend an owning team for each group. Keep any internal notes clearly',
      'separate from customer-facing wording. Here is the raw queue:',
      '',
      SAMPLE_TICKETS,
    ].join('\n');
  },

  /**
   * NON-GATING judge context. Emits deterministic reference data derived from the
   * scenario inputs so a downstream LLM/human judge can assess the drafted plan's
   * P1 output-quality against Priya's authored success criteria WITHOUT re-running
   * anything. This computes NO pass/fail — the driver never gates on it.
   *
   * The judge should look for (from Priya's "Success looks like"): all 5 tickets
   * accounted for, the 4821↔4822 duplicate pair flagged, per-ticket severity with
   * a one-line justification, a recommended owning team per group, missing-info
   * notes, customer-impact estimates, and internal notes kept separate from
   * customer-facing wording.
   *
   * @param {{ outcomeSpec: any, events: any[], team: any, submittedGoal: string }} _evidence
   */
  judgeContext() {
    const ticketIds = [...new Set([...SAMPLE_TICKETS.matchAll(/TICKET-\d+/g)].map((m) => m[0]))];
    return {
      expectedTicketIds: ticketIds,
      knownDuplicatePair: ['TICKET-4821', 'TICKET-4822'],
      duplicateRationale: 'Same reporter (Contoso), same "export to PDF" defect, two threads.',
      rawTicketBatch: SAMPLE_TICKETS,
      judgeShouldVerify: [
        `All ${ticketIds.length} tickets (${ticketIds.join(', ')}) accounted for in the plan — none dropped`,
        'The 4821↔4822 duplicate pair is flagged as duplicates',
        'Each ticket/group carries a severity with a one-line justification',
        'A recommended owning team is given per group',
        'Missing troubleshooting info and customer-impact are called out',
        'Internal analysis is kept clearly separate from customer-facing wording',
      ],
    };
  },
};
