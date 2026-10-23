/**
 * Shared casting helpers for the Agents page and the Cast a team page. Kept
 * outside component files so fast refresh keeps working.
 */
import { GatewayError } from './api';
import type { ProjectCastingRoleDefinition, ProjectConfiguration } from './contracts';
import { errorMessage } from './errors';

/** Address of the Agents page for a project. */
export function agentsPath(projectId: string): string {
  return `/projects/${encodeURIComponent(projectId)}/team`;
}

/** Address of the Cast a team page. */
export function castPath(projectId: string): string {
  return `${agentsPath(projectId)}/cast`;
}

/** Address of the review step for one proposal. */
export function reviewPath(projectId: string, proposalId: string): string {
  return `${castPath(projectId)}?proposal=${encodeURIComponent(proposalId)}`;
}

/** Roles the service reserves for orchestration. It rejects drafts that cast them. */
const RESERVED_ROLE_IDS = new Set(['scribe', 'work-monitor', 'coordinator', 'rai']);

/** Keeps only catalog roles that a user may cast. */
export function castableRoles(roles: ProjectCastingRoleDefinition[]): ProjectCastingRoleDefinition[] {
  // The service compares reserved IDs without case, so do the same here.
  return roles.filter((role) => !RESERVED_ROLE_IDS.has(role.id.toLowerCase()));
}

/** Builds charter text from a catalog role, in the same layout the service uses. */
export function roleCharter(role: ProjectCastingRoleDefinition): string {
  return [
    role.summary,
    '',
    'Responsibilities:',
    ...role.responsibilities.map((item) => `- ${item}`),
    '',
    'Boundaries:',
    ...role.boundaries.map((item) => `- ${item}`),
  ].join('\n').trim();
}

/**
 * Returns a copy of `configuration` with one new member for `role` added at the
 * end of the team. Other settings and existing members stay the same.
 */
export function draftWithMember(configuration: ProjectConfiguration, role: ProjectCastingRoleDefinition): ProjectConfiguration {
  // Agent IDs must be unique. Use the role ID, and add a number when that ID is taken.
  const taken = new Set(configuration.agentCharters.map((item) => item.agentId));
  let agentId = role.id;
  for (let suffix = 2; taken.has(agentId); suffix += 1) agentId = `${role.id}-${suffix}`;
  // Place the new member after the last one. An empty team starts at 0, like the service does.
  const order = configuration.casting.reduce((max, item) => Math.max(max, item.order + 1), 0);
  return {
    ...configuration,
    agentCharters: [...configuration.agentCharters, { agentId, name: role.title, role: role.id, charter: roleCharter(role) }],
    casting: [...configuration.casting, { agentId, role: role.id, order }],
  };
}

/** Adds the selected catalog roles to the current team without replacing its members. */
export function draftWithRoles(
  configuration: ProjectConfiguration,
  roles: ProjectCastingRoleDefinition[],
): ProjectConfiguration {
  // Reuse the single-member path so IDs and team order stay consistent for every added role.
  return roles.reduce((draft, role) => draftWithMember(draft, role), configuration);
}

/** Keeps only model overrides that still target a configured role or agent. */
function retainConfiguredModelOverrides(configuration: ProjectConfiguration): ProjectConfiguration {
  const roles = new Set(configuration.casting.map((item) => item.role));
  const agents = new Set(configuration.agentCharters.map((item) => item.agentId));
  // A removed role or agent cannot keep a model override in the next configuration revision.
  return {
    ...configuration,
    modelRoleOverrides: (configuration.modelRoleOverrides ?? []).filter((item) => roles.has(item.roleId)),
    modelAgentOverrides: (configuration.modelAgentOverrides ?? []).filter((item) => agents.has(item.agentId)),
  };
}

/** Returns a copy with one cast member's charter and roster role changed together. */
export function draftWithRole(
  configuration: ProjectConfiguration,
  agentId: string,
  role: ProjectCastingRoleDefinition,
): ProjectConfiguration {
  // The owner requires the charter and casting role to match exactly.
  if (!configuration.agentCharters.some((item) => item.agentId === agentId) ||
      !configuration.casting.some((item) => item.agentId === agentId))
    throw new Error('This agent is not in the current team.');
  const changed = {
    ...configuration,
    agentCharters: configuration.agentCharters.map((item) =>
      item.agentId === agentId ? { ...item, role: role.id, charter: roleCharter(role) } : item),
    casting: configuration.casting.map((item) =>
      item.agentId === agentId ? { ...item, role: role.id } : item),
  };
  return retainConfiguredModelOverrides(changed);
}

/** One member of a draft team: its charter joined with its casting entry. */
export interface DraftMember {
  agentId: string;
  name: string;
  role: string;
  charter: string;
  order: number;
}

/** Lists the cast members of a configuration in team order. */
export function draftMembers(configuration: ProjectConfiguration): DraftMember[] {
  const charters = new Map(configuration.agentCharters.map((item) => [item.agentId, item]));
  return [...configuration.casting]
    .sort((left, right) => left.order - right.order)
    .map((item) => {
      // A casting entry without a charter still shows, using its ID as the name.
      const charter = charters.get(item.agentId);
      return { agentId: item.agentId, name: charter?.name || item.agentId, role: item.role, charter: charter?.charter ?? '', order: item.order };
    });
}

/** Returns a copy of `configuration` without one agent's charter and casting entry. */
export function draftWithoutMember(configuration: ProjectConfiguration, agentId: string): ProjectConfiguration {
  const changed = {
    ...configuration,
    agentCharters: configuration.agentCharters.filter((item) => item.agentId !== agentId),
    casting: configuration.casting.filter((item) => item.agentId !== agentId),
    skills: configuration.skills.map((skill) => {
      if (!skill.agentIds?.includes(agentId)) return skill;
      const remainingAgentIds = skill.agentIds.filter((item) => item !== agentId);
      // An empty explicit assignment must not become an all-agents assignment.
      return { ...skill, enabled: remainingAgentIds.length > 0 && skill.enabled, agentIds: remainingAgentIds };
    }),
  };
  return retainConfiguredModelOverrides(changed);
}

/** Plain text for casting failures. `write` selects the copy for changes and exports. */
export function castingError(reason: unknown, write: boolean): string {
  if (reason instanceof GatewayError) {
    // Reads need project read access. Writes and exports need permission to change the project.
    if (reason.status === 403) return write
      ? 'You need permission to change this project to cast, add, export, or import team members.'
      : 'You do not have permission to view this project\'s team proposals.';
    if (reason.status === 409) return 'The project or proposal changed since you loaded it. The latest version is now shown. Review it and try again.';
    if (reason.status === 413) return 'The file is larger than 1 MiB.';
    if (reason.status === 422) return 'The file is not a valid team export, or its checksum does not match.';
  }
  return errorMessage(reason);
}

/** True for a revision conflict, which means the caller should re-read and retry. */
export function isConflict(reason: unknown): boolean {
  return reason instanceof GatewayError && reason.status === 409;
}
