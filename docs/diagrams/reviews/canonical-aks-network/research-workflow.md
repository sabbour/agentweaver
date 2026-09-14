# Astra research: workflows, MCP and guide claims

Completed read-only thread: `guide-workflow-evidence`, model `gpt-6-astra`.
This coordinator summary persists the bounded agent result. Scope: MCP lifecycle,
collective review, provider hierarchy, and the remaining guide audit corrections.

## Findings adopted

- `apps/Agentweaver.Mcp/Tools/CoordinatorTools.cs:14-190`,
  `RunTools.cs:137-302,366-394`, `TeamTools.cs:12-79`: team_cast proposes unless
  confirmed. Manual coordinator start uses defineOutcome and autopilot=false;
  get/confirm/revise are distinct tools.
- run_task starts a NEW run (default direct) and polls for a bounded period.
  Backlog capture -> Ready -> heartbeat reservation is an alternative, not a
  prerequisite to another explicit start. `CoordinatorPickupService.cs:183-245`
  owns atomic pickup.
- run_review accepts approved:boolean: approve or decline only. Request changes
  uses web/REST review. coordinator_steer is distinct.
- Children run Agent -> Assemble-ready. Selected software workflows apply
  collective RAI then Build & Test; deterministic platform PreviewStep follows.
  Revision may require coordinator-driven child work and reassembly.
- Assistant defaults to five active sessions. Thirty-minute inactivity yields
  resumable Idle, not completed. The audit's "idle completion" phrase is obsolete.
- `apps/Agentweaver.Api/Auth/EffectiveModelProviderResolver.cs:41-108`: active
  project binding wins/fails closed; otherwise platform BYOK then platform
  Copilot. Personal sessions use platform BYOK, personal BYOK, personal Copilot,
  never platform Copilot. Repository authority is separate.
- Project, platform and personal-user Copilot completion share the callback;
  MCP browser handoff is not a fourth completion scope.
- Board has four main/two attention buckets; Problems cannot be dragged to Ready.
  Software blueprints are software-delivery/bug-fix, not infra-ops. Only default
  review_policy is accepted. Historical Fleet process-credential and identity
  absolutes need current-contract qualification.

## Applied and deferred

Updated owned guide prose and the MCP lifecycle. Did not author the shared
canonical-default-workflow asset: its missing collective Build & Test and
revision semantics block the conditional guide-review-fig1 merge. The unsafe
legacy embed is withheld and accurate guide-specific prose remains.
