/**
 * Builds the Dashboard status card from the project's saved configuration.
 * Every check reads a real configuration field; nothing is estimated. Run
 * counts and success rates need the metrics contract (gap G3) and are not shown.
 */
import type { ProjectConfiguration } from './contracts';

/** One setup check on the Dashboard status card. */
export interface ReadinessCheck {
  key: 'model' | 'repository' | 'team' | 'skills';
  label: string;
  done: boolean;
  /** Project-relative path that fixes the check. */
  path: string;
  action: string;
}

/** Status card content: badge, title, body, and the next action. */
export interface ProjectReadiness {
  ready: boolean;
  badge: { label: string; color: 'success' | 'warning' | 'subtle' };
  title: string;
  body: string;
  checks: ReadinessCheck[];
  primaryAction: { label: string; path: string };
}

/** Evaluates setup checks in the order a new project usually completes them. */
export function projectReadiness(configuration: ProjectConfiguration, archived = false): ProjectReadiness {
  const repository = configuration.sourceControl?.repository;
  const enabledSkills = configuration.skills.filter((skill) => skill.enabled).length;
  const checks: ReadinessCheck[] = [
    {
      key: 'model',
      label: configuration.modelSelection?.reference
        ? `Default model: ${configuration.modelSelection.reference}`
        : 'No default model',
      done: Boolean(configuration.modelSelection?.reference),
      path: 'settings?section=models',
      action: 'Choose a model',
    },
    {
      key: 'repository',
      label: repository ? `Repository: ${repository.owner}/${repository.name}` : 'No repository',
      done: Boolean(repository),
      path: 'settings?section=repository',
      action: 'Connect a repository',
    },
    {
      key: 'team',
      label: configuration.agentCharters.length > 0
        ? `${configuration.agentCharters.length} agent${configuration.agentCharters.length === 1 ? '' : 's'} in the team`
        : 'No agents in the team',
      done: configuration.agentCharters.length > 0,
      path: 'team',
      action: 'Add agents',
    },
    {
      key: 'skills',
      label: enabledSkills > 0 ? `${enabledSkills} skill${enabledSkills === 1 ? '' : 's'} assigned` : 'No skills assigned',
      done: enabledSkills > 0,
      path: 'skills',
      action: 'Assign skills',
    },
  ];
  if (archived) {
    return {
      ready: false,
      badge: { label: 'Archived', color: 'subtle' },
      title: 'This project is archived',
      body: 'Archived projects keep their settings and memories. Restore the project in Project settings to use it again.',
      checks,
      primaryAction: { label: 'Open Project settings', path: 'settings?section=danger' },
    };
  }
  const missing = checks.filter((check) => !check.done);
  // Skills are optional for a run, so only model, repository, and team block readiness.
  const blocking = missing.filter((check) => check.key !== 'skills');
  if (blocking.length === 0) {
    return {
      ready: true,
      badge: { label: 'Ready', color: 'success' },
      title: 'Project is set up for runs',
      body: missing.length === 0
        ? 'The model, repository, team, and skills are configured.'
        : 'The model, repository, and team are configured. Assign skills to give agents reusable instructions.',
      checks,
      primaryAction: { label: 'Open Orchestrations', path: 'orchestrations' },
    };
  }
  return {
    ready: false,
    badge: { label: 'Setup needed', color: 'warning' },
    title: 'Finish project setup',
    body: `${blocking.map((check) => check.action).join(', ')} before you start runs.`,
    checks,
    primaryAction: { label: blocking[0].action, path: blocking[0].path },
  };
}
