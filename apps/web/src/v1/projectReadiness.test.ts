import { describe, expect, it } from 'vitest';
import type { ProjectConfiguration } from './contracts';
import { projectReadiness } from './projectReadiness';

function configuration(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return {
    modelSelection: null,
    providerOverrides: [],
    orderedProviderOverrides: [],
    agentCharters: [],
    casting: [],
    blueprintWorkflowReferences: [],
    skills: [],
    runLimits: {},
    sourceControl: null,
    ...overrides,
  };
}

const agent = (agentId: string) => ({ agentId, name: agentId, role: 'Builder', charter: '' });
const skill = (skillId: string, enabled: boolean) => ({ skillId, enabled, order: 0 });

function complete(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return configuration({
    modelSelection: { reference: 'gpt-test' } as ProjectConfiguration['modelSelection'],
    sourceControl: { repository: { owner: 'octo', name: 'repo' } } as ProjectConfiguration['sourceControl'],
    agentCharters: [agent('a1')],
    skills: [skill('s1', true)],
    ...overrides,
  });
}

describe('projectReadiness', () => {
  it('asks to finish setup when nothing is configured, in setup order', () => {
    const result = projectReadiness(configuration());
    expect(result.ready).toBe(false);
    expect(result.badge).toEqual({ label: 'Setup needed', color: 'warning' });
    expect(result.title).toBe('Finish project setup');
    expect(result.body).toBe('Choose a model, Connect a repository, Add agents before you start runs.');
    expect(result.primaryAction).toEqual({ label: 'Choose a model', path: 'settings?section=models' });
    expect(result.checks.map((check) => [check.key, check.done, check.label])).toEqual([
      ['model', false, 'No default model'],
      ['repository', false, 'No repository'],
      ['team', false, 'No agents in the team'],
      ['skills', false, 'No skills assigned'],
    ]);
  });

  it('points the primary action at the first blocking check', () => {
    const result = projectReadiness(complete({ sourceControl: null }));
    expect(result.ready).toBe(false);
    expect(result.body).toBe('Connect a repository before you start runs.');
    expect(result.primaryAction).toEqual({ label: 'Connect a repository', path: 'settings?section=repository' });
  });

  it('is ready when everything is configured', () => {
    const result = projectReadiness(complete({ agentCharters: [agent('a1'), agent('a2')], skills: [skill('s1', true), skill('s2', true)] }));
    expect(result.ready).toBe(true);
    expect(result.badge).toEqual({ label: 'Ready', color: 'success' });
    expect(result.body).toBe('The model, repository, team, and skills are configured.');
    expect(result.primaryAction).toEqual({ label: 'Open Orchestrations', path: 'orchestrations' });
    expect(result.checks.map((check) => check.label)).toEqual([
      'Default model: gpt-test', 'Repository: octo/repo', '2 agents in the team', '2 skills assigned',
    ]);
  });

  it('treats skills as optional and counts only enabled skills', () => {
    const result = projectReadiness(complete({ skills: [skill('s1', false)] }));
    expect(result.ready).toBe(true);
    expect(result.body).toContain('Assign skills');
    expect(result.checks.find((check) => check.key === 'skills')).toMatchObject({ done: false, label: 'No skills assigned' });
  });

  it('uses singular labels for one agent and one skill', () => {
    const labels = projectReadiness(complete()).checks.map((check) => check.label);
    expect(labels).toContain('1 agent in the team');
    expect(labels).toContain('1 skill assigned');
  });

  it('reports an archived project regardless of setup', () => {
    const result = projectReadiness(complete(), true);
    expect(result.ready).toBe(false);
    expect(result.badge).toEqual({ label: 'Archived', color: 'subtle' });
    expect(result.title).toBe('This project is archived');
    expect(result.primaryAction).toEqual({ label: 'Open Project settings', path: 'settings?section=danger' });
    expect(result.checks.every((check) => check.done)).toBe(true);
  });
});
