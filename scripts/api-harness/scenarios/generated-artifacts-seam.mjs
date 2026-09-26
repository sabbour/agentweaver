// Scenario playbook: GENERATED-ARTIFACT SEAM test (issue #1 expansion, requirement 2).
//
// Persona framing: specs/personas/greenfield-aks-automatic-developer.md (Jordan Lee)
// — a developer who types a plain-language idea and expects Agentweaver to GENERATE a
// fit-for-purpose team + workflow. This scenario doesn't judge a product plan; it
// judges the GENERATORS themselves at their seams:
//
//   • POST /api/blueprints/generate            → a generated blueprint (roster + workflows)
//   • POST /api/projects/{id}/workflows/generate → a generated workflow (YAML draft)
//
// and asserts the generated artifacts are STRUCTURALLY CORRECT, mirroring the exact
// backend rules (lib/generation-checks.mjs):
//   - roster excludes reserved system roles (Scribe/Work Monitor/Rai/Coordinator) — the
//     class of bug a human had to catch manually in issue #311;
//   - the generated workflow passes WorkflowDefinitionLoader.Load validation (no dangling
//     edges, every check-node verdict routed, no unsupported node types);
//   - neither artifact assigns work to a reserved orchestration role.
//
// Driven by lib/seams.mjs (kind: 'generation-seam'), NOT the persona runner. Bounded:
// generation endpoints return unsaved drafts; the throwaway project is deleted after.

export default {
  id: 'generated-artifacts-seam',
  kind: 'generation-seam',
  requireDurableJobs: true,
  personaFile: 'greenfield-aks-automatic-developer.md',
  personaScenario: 'Generated-artifact seam integrity',
  title: 'Generated-artifact seams — roster + workflow structural integrity',

  projectPrefix: 'seam-genart',

  // Base blueprint for the throwaway project that hosts project-scoped workflow generation.
  baseBlueprintId: 'blueprint-pm-and-software-development',

  // A realistic new-team idea to make the generator mint a domain roster + workflow.
  // Deliberately does NOT ask for logging/monitoring/safety roles — if the generator
  // leaks Scribe/Work Monitor/Rai/Coordinator into the roster, that's the #311 bug.
  blueprintDescription: [
    'A small team to take a plain-language product idea and turn it into a deployed',
    'web application: a product analyst to clarify requirements, a backend engineer,',
    'a frontend engineer, a QA engineer to write and run tests, and a release engineer',
    'to containerize and deploy to AKS Automatic. Include a review gate before deploy.',
  ].join(' '),

  // A workflow the generator must produce as a valid graph with a review/check gate —
  // exercises the check-branch-routing and edge-integrity rules specifically.
  workflowDescription: [
    'Design → implement → peer review → build and test → if tests pass, deploy; if they',
    'fail, loop back to implement. End with a human review gate before the deploy step.',
  ].join(' '),

  // Provider-backed conservative-generation probes. These are retained as ordinary
  // workflows when --keep is supplied, so post-merge acceptance can inspect or run
  // the exact generated artifacts rather than relying on synthetic completion.
  conservativeFanCases: [
    {
      id: 'independent-disjoint-research',
      expectedMode: 'fan',
      startRetainedRun: true,
      description: [
        'Research customer adoption signals and technical feasibility independently.',
        'The customer branch must write only reports/customer-signals.md. The technical',
        'branch must write only reports/technical-feasibility.md. Join both branches,',
        'then synthesize their ordered findings. This is documentation-only work.',
      ].join(' '),
    },
    {
      id: 'overlapping-output-scope',
      expectedMode: 'sequential',
      description: [
        'Research customer adoption signals and technical feasibility. Both tasks need',
        'to update reports/discovery.md, then synthesize the result. Keep dependent or',
        'overlapping work sequential.',
      ].join(' '),
    },
    {
      id: 'unknown-output-scope',
      expectedMode: 'sequential',
      description: [
        'Research two aspects of this product and synthesize the result, but no exact',
        'output files are known yet. Keep work sequential when write scope is unknown.',
      ].join(' '),
    },
  ],

  // Runtime acceptance matrix prepared for retained post-merge proof. The seam does
  // not fake provider execution; these labels travel with the evidence so operators
  // can retain the generated workflows/runs and prove each lifecycle outcome live.
  retainedRuntimeProofs: [
    'fan-success-overlap-and-declaration-order',
    'one-branch-failure',
    'cancellation-propagation',
    'restart-and-retry-without-duplicate-branches',
    'pm-discovery-branches-before-synthesis',
  ],
  verifyPmDiscovery: true,

  // A generated team should be more than a single generalist.
  minRosterSize: 3,
};
