import type { WorkflowRunDto } from '../api/types';

// The runs list (GET /api/projects/{id}/runs) does not carry a dedicated kind discriminator,
// but a coordinator run is stored with AgentName == "Coordinator" (CoordinatorRunService.cs).
// Detect it from agent_name so coordinator runs route to the topology view instead of the
// generic 5-node workflow page. Tolerant of an explicit discriminator the backend may add later.
export const COORDINATOR_AGENT_NAME = 'Coordinator';

type RunLike = Pick<WorkflowRunDto, 'agent_name'> & {
  kind?: string;
  is_coordinator?: boolean;
};

export function isCoordinatorRun(run: RunLike): boolean {
  if (run.is_coordinator === true) return true;
  if (typeof run.kind === 'string' && run.kind.toLowerCase() === 'coordinator') return true;
  return run.agent_name?.trim().toLowerCase() === COORDINATOR_AGENT_NAME.toLowerCase();
}
