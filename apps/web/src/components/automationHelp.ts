// Shared, verbatim help copy for the automation toggles (Autopilot / Auto-approve
// tools). Centralized so the explanation stays consistent across the coordinator
// orchestration toolbar, the per-run header, and the project pickup defaults. Only
// the "applies to" clause differs per surface.

const AUTOPILOT_BASE =
  "Auto-answers the coordinator's clarifying questions using the coordinator model so the run doesn't pause for you. Tool/permission approvals are still asked, and every auto-answer is logged in the timeline.";

const AUTO_APPROVE_BASE =
  "Automatically approves tool calls so agents don't pause for each one — except tools blocked by sandbox policy (destructive shell / network), which always require explicit approval.";

export const AUTOMATION_HELP = {
  // Coordinator orchestration toolbar (cascades to child runs).
  autopilotOrchestration: `${AUTOPILOT_BASE} Applies to this orchestration and its child runs.`,
  autoApproveOrchestration: `${AUTO_APPROVE_BASE} Applies to this orchestration and its child runs.`,

  // Project pickup defaults (apply to auto-picked-up runs).
  autopilotPickup: `${AUTOPILOT_BASE} Applies to runs this project picks up automatically (and their child runs).`,
  autoApprovePickup: `${AUTO_APPROVE_BASE} Applies to runs this project picks up automatically (and their child runs).`,

  // Single-agent run header (auto-approve only).
  autoApproveRun: `${AUTO_APPROVE_BASE} Applies to this run.`,
} as const;
