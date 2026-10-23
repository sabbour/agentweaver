import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient, GatewayError } from './api';
import {
  castableRoles, castingError, draftMembers, draftWithMember, draftWithRole, draftWithRoles, draftWithoutMember, isConflict, reviewPath, roleCharter,
} from './castingData';
import type { ProjectCastingRoleDefinition, ProjectCastingTransfer, ProjectConfiguration } from './contracts';

function role(id: string, title = id): ProjectCastingRoleDefinition {
  return { id, title, summary: `${title} summary`, responsibilities: ['Do the work'], boundaries: ['Stay in scope'] };
}

function config(overrides: Partial<ProjectConfiguration> = {}): ProjectConfiguration {
  return {
    providerOverrides: [],
    orderedProviderOverrides: [],
    modelRoleOverrides: [],
    modelAgentOverrides: [],
    agentCharters: [{ agentId: 'lead', name: 'Lead', role: 'lead', charter: 'Lead.' }],
    casting: [{ agentId: 'lead', role: 'lead', order: 0 }],
    blueprintWorkflowReferences: [],
    skills: [{ skillId: 'kept', enabled: true, order: 0 }],
    runLimits: {},
    ...overrides,
  };
}

describe('casting helpers', () => {
  it('hides reserved roles, ignoring case', () => {
    expect(castableRoles([role('lead'), role('Scribe'), role('work-monitor'), role('COORDINATOR'), role('rai')]).map((item) => item.id))
      .toEqual(['lead']);
  });

  it('builds the charter in the service layout', () => {
    expect(roleCharter(role('tester', 'Tester'))).toBe('Tester summary\n\nResponsibilities:\n- Do the work\n\nBoundaries:\n- Stay in scope');
  });

  it('adds one member with a unique ID after the last one and keeps other settings', () => {
    const once = draftWithMember(config(), role('lead', 'Lead two'));
    expect(once.casting.at(-1)).toEqual({ agentId: 'lead-2', role: 'lead', order: 1 });
    expect(once.agentCharters.at(-1)).toMatchObject({ agentId: 'lead-2', name: 'Lead two', role: 'lead' });
    expect(once.skills).toEqual(config().skills);
    const empty = draftWithMember(config({ agentCharters: [], casting: [] }), role('tester'));
    expect(empty.casting).toEqual([{ agentId: 'tester', role: 'tester', order: 0 }]);
  });

  it('adds several roles without replacing the existing team', () => {
    const augmented = draftWithRoles(config(), [role('lead', 'Second lead'), role('tester')]);
    expect(augmented.casting).toEqual([
      { agentId: 'lead', role: 'lead', order: 0 },
      { agentId: 'lead-2', role: 'lead', order: 1 },
      { agentId: 'tester', role: 'tester', order: 2 },
    ]);
    expect(augmented.agentCharters[0]).toEqual(config().agentCharters[0]);
    expect(augmented.skills).toEqual(config().skills);
  });

  it('changes a team member role in both the charter and casting draft', () => {
    const source = config({
      agentCharters: [
        { agentId: 'lead', name: 'Lead', role: 'lead', charter: 'Lead.', customRoleTitle: 'Old title' },
        { agentId: 'tester', name: 'Tester', role: 'tester', charter: 'Test.' },
      ],
      casting: [
        { agentId: 'lead', role: 'lead', order: 0 },
        { agentId: 'tester', role: 'tester', order: 1 },
      ],
      modelRoleOverrides: [
        { roleId: 'lead', selection: { reference: 'lead-model' } },
        { roleId: 'tester', selection: { reference: 'tester-model' } },
      ],
      modelAgentOverrides: [{ agentId: 'lead', selection: { reference: 'agent-model' } }],
    });
    const changed = draftWithRole(source, 'lead', role('editor', 'Editor'));
    expect(changed.agentCharters[0].role).toBe('editor');
    expect(changed.agentCharters[0].charter).toBe(roleCharter(role('editor', 'Editor')));
    expect(changed.agentCharters[0].customRoleTitle).toBeNull();
    expect(changed.casting[0].role).toBe('editor');
    expect(changed.modelRoleOverrides).toEqual([{ roleId: 'tester', selection: { reference: 'tester-model' } }]);
    expect(changed.modelAgentOverrides).toEqual(source.modelAgentOverrides);
    expect(changed.skills).toEqual(source.skills);
    expect(() => draftWithRole(source, 'missing', role('editor'))).toThrow('This agent is not in the current team.');

    const titleOnly = draftWithRole(source, 'lead', role('lead', 'Lead'));
    expect(titleOnly.agentCharters[0].charter).toBe('Lead.');
    expect(titleOnly.agentCharters[0].customRoleTitle).toBe('Old title');
  });

  it('removes a member and cleans up assignments and overrides that would become invalid', () => {
    const draft = config({
      agentCharters: [
        { agentId: 'a', name: 'Ada', role: 'lead', charter: 'Lead.' },
        { agentId: 'b', name: 'Bea', role: 'tester', charter: 'Test.' },
      ],
      casting: [{ agentId: 'b', role: 'tester', order: 2 }, { agentId: 'a', role: 'lead', order: 1 }],
      modelRoleOverrides: [
        { roleId: 'lead', selection: { reference: 'lead-model' } },
        { roleId: 'tester', selection: { reference: 'tester-model' } },
      ],
      modelAgentOverrides: [
        { agentId: 'a', selection: { reference: 'ada-model' } },
        { agentId: 'b', selection: { reference: 'bea-model' } },
      ],
      skills: [
        { skillId: 'ada-only', enabled: true, order: 0, revision: 1, contentDigest: 'a'.repeat(64), agentIds: ['a'] },
        { skillId: 'shared', enabled: true, order: 1, revision: 1, contentDigest: 'b'.repeat(64), agentIds: ['a', 'b'] },
      ],
    });
    expect(draftMembers(draft).map((item) => [item.agentId, item.name])).toEqual([['a', 'Ada'], ['b', 'Bea']]);
    const removed = draftWithoutMember(draft, 'a');
    expect(removed.casting.map((item) => item.agentId)).toEqual(['b']);
    expect(removed.agentCharters.map((item) => item.agentId)).toEqual(['b']);
    expect(removed.modelRoleOverrides).toEqual([{ roleId: 'tester', selection: { reference: 'tester-model' } }]);
    expect(removed.modelAgentOverrides).toEqual([{ agentId: 'b', selection: { reference: 'bea-model' } }]);
    expect(removed.skills).toEqual([
      { skillId: 'ada-only', enabled: false, order: 0, revision: 1, contentDigest: 'a'.repeat(64), agentIds: [] },
      { skillId: 'shared', enabled: true, order: 1, revision: 1, contentDigest: 'b'.repeat(64), agentIds: ['b'] },
    ]);
  });

  it('gives plain copy for permission, conflict, and file errors', () => {
    const error = (status: number) => new GatewayError(status, {}, 'raw');
    expect(castingError(error(403), false)).toMatch(/permission to view/);
    expect(castingError(error(403), true)).toMatch(/permission to change this project/);
    expect(castingError(error(409), true)).toMatch(/changed since you loaded it/);
    expect(castingError(error(413), true)).toBe('The file is larger than 1 MiB.');
    expect(castingError(error(422), true)).toMatch(/not a valid team export/);
    expect(isConflict(error(409))).toBe(true);
    expect(isConflict(error(400))).toBe(false);
    expect(reviewPath('p 1', 'x/y')).toBe('/projects/p%201/team/cast?proposal=x%2Fy');
  });
});

describe('casting client methods', () => {
  function client(body: unknown = {}) {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => new Response(JSON.stringify(body), {
      status: 200, headers: { 'content-type': 'application/json' },
    }));
    return { fetcher, client: new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher) };
  }
  const call = (fetcher: ReturnType<typeof client>['fetcher']) => {
    const [url, init] = fetcher.mock.calls[0];
    return { url: String(url), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : undefined };
  };

  it('reads and edits one proposal', async () => {
    const read = client();
    await read.client.getCastingProposal('token', 'p1', 'pr-1');
    expect(call(read.fetcher)).toMatchObject({ url: 'https://gateway.example.test/api/v1/projects/p1/casting/proposals/pr-1', method: 'GET' });

    const update = client();
    await update.client.updateCastingProposal('token', 'p1', 'pr-1', { expectedDraftRevision: 2, draft: config() });
    expect(call(update.fetcher)).toMatchObject({ method: 'PATCH', body: { expectedDraftRevision: 2 } });
  });

  it('creates a raw draft proposal', async () => {
    const create = client();
    await create.client.createCastingDraftProposal('token', 'p1', { expectedConfigurationRevision: 4, draft: config() });
    expect(call(create.fetcher)).toMatchObject({
      url: 'https://gateway.example.test/api/v1/projects/p1/casting/proposals', method: 'POST', body: { expectedConfigurationRevision: 4 },
    });
  });

  it('exports at a revision and imports a transfer', async () => {
    const exported = client();
    await exported.client.exportCastingTransfer('token', 'p1', 4);
    expect(call(exported.fetcher).url).toBe('https://gateway.example.test/api/v1/projects/p1/casting/export?revision=4');

    const latest = client();
    await latest.client.exportCastingTransfer('token', 'p1');
    expect(call(latest.fetcher).url).toBe('https://gateway.example.test/api/v1/projects/p1/casting/export');

    const imported = client();
    const transfer: ProjectCastingTransfer = {
      formatVersion: 1, sourceProjectId: 'p0', sourceConfigurationRevision: 3, contentDigest: 'd'.repeat(64), agentCharters: [], casting: [],
    };
    await imported.client.importCastingTransfer('token', 'p1', { expectedConfigurationRevision: 4, transfer });
    expect(call(imported.fetcher)).toMatchObject({
      url: 'https://gateway.example.test/api/v1/projects/p1/casting/import', method: 'POST',
      body: { expectedConfigurationRevision: 4, transfer: { sourceProjectId: 'p0' } },
    });
  });
});
