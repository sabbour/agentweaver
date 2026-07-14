// Scenario playbook: Jordan Lee — "Blank idea to AKS Automatic".
//
// Persona: specs/personas/greenfield-aks-automatic-developer.md
// Jordan is a full-stack dev who types a plain-language product idea and expects
// Agentweaver to coordinate specialists from idea → scaffolded app → container →
// AKS Automatic deployment → smoke test, with minimal Kubernetes hand-holding.
//
// This playbook exists to prove lib/runner.mjs GENERALIZES beyond Priya: it reuses
// the exact same drive+judge engine, supplying only a different blueprint, goal,
// and bespoke checks. It is bounded IDENTICALLY to the Priya scenario — the engine
// starts the coordinator in `defineOutcome` mode and stops at the outcome-spec
// confirmation gate, so NOTHING is scaffolded, containerized, deployed, or merged.
// It validates the first, safe rung of Jordan's journey: does Agentweaver turn a
// blank product idea into a coordinated, reviewable plan in one traceable flow?
//
// Mapping to the REST API (all driven by lib/runner.mjs):
//   1. POST /api/projects                         create project + software team
//   2. GET  /api/projects/{id}/team               confirm a multi-role team
//   3. POST /api/projects/{id}/orchestrations     (defineOutcome) draft a plan, gate
//   4. GET  /api/runs/{id}/outcome-spec           the reviewable plan Jordan inspects
//   5. GET  /api/runs/{id}/events                 evidence trail
//
// Judged from API responses only — never screenshots. DRIVER/JUDGE SEPARATION:
// this scenario emits deterministic non-gating `judgeContext` reference data only;
// the subjective "is the drafted plan good?" verdict is deferred to an LLM judge.

export default {
  id: 'jordan-blank-to-plan',
  personaFile: 'greenfield-aks-automatic-developer.md',
  personaScenario: 'Blank idea to AKS Automatic',
  title: 'Jordan Lee — Blank idea to a coordinated plan (API-driven)',

  // A software-delivery team (architect/backend/qa + workflows), not infra-only roles.
  blueprintId: 'blueprint-software-development',
  projectPrefix: 'persona-jordan-blank',

  // Jordan types a single plain-language idea and expects the team to be inferred.
  buildGoal() {
    return [
      'Build a simple multi-user task tracker with a web UI and an API, then deploy',
      'it to AKS Automatic. I am comfortable with code and GitHub but not a',
      'Kubernetes specialist — coordinate the specialists for me, keep the generated',
      'code and cloud changes reviewable, and only ask me for the decisions I truly',
      'must make (app purpose, subscription, region, public/private exposure). I want',
      'a visible path from idea to a scaffolded repo, a container image, an AKS',
      'Automatic deployment, and a live smoke test against the running app.',
    ].join(' ');
  },

  /**
   * NON-GATING judge context. Emits deterministic reference data + the things a
   * downstream LLM/human judge should verify against Jordan's authored "Success
   * looks like" / "Failure signals". Computes NO pass/fail — the driver never
   * gates on it.
   *
   * From Jordan's persona: the plan should move idea → scaffolded app → container →
   * AKS Automatic deployment → live smoke test in one traceable flow, only ask for
   * the decisions he truly must make (app purpose, subscription, region,
   * public/private exposure), keep generated code/cloud changes reviewable, and
   * NOT declare success before a reachable endpoint / concrete smoke-test evidence.
   */
  judgeContext() {
    return {
      requiredDecisionsJordanExpects: ['app purpose', 'subscription', 'region', 'public/private exposure'],
      expectedArc: ['product idea', 'scaffolded repo', 'container image', 'AKS Automatic deployment', 'live smoke test'],
      judgeShouldVerify: [
        'Drafted plan spans the full idea → app → container → deployment arc in one traceable flow',
        'Plan owns verification — does not declare success before a reachable endpoint / smoke test',
        'Plan only asks Jordan for the decisions he truly must make (not Kubernetes minutiae)',
        'Generated code and cloud changes are kept reviewable',
      ],
      knownFailureSignals: [
        'UI cannot move from product idea to coordinated app/container/deployment in one flow',
        "'success' declared before a reachable endpoint or concrete smoke-test evidence",
      ],
    };
  },
};
