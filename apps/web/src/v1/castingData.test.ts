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
    const changed = draftWithRole(config(), 'lead', 'editor');
    expect(changed.agentCharters[0].role).toBe('editor');
    expect(changed.casting[0].role).toBe('editor');
    expect(changed.skills).toEqual(config().skills);
    expect(() => draftWithRole(config(), 'missing', 'editor')).toThrow('This agent is not in the current team.');
  });

  it('lists members in team order and removes one', () => {
    const draft = config({
      agentCharters: [{ agentId: 'b', name: 'Bea', role: 'tester', charter: 'Test.' }],
      casting: [{ agentId: 'b', role: 'tester', order: 2 }, { agentId: 'a', role: 'lead', order: 1 }],
    });
    expect(draftMembers(draft).map((item) => [item.agentId, item.name])).toEqual([['a', 'a'], ['b', 'Bea']]);
    const removed = draftWithoutMember(draft, 'b');
    expect(removed.casting.map((item) => item.agentId)).toEqual(['a']);
    expect(removed.agentCharters).toEqual([]);
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
