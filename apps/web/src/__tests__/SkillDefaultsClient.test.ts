import { AgentweaverApiClient } from '../api/client';
import { afterEach, describe, expect, it, vi } from 'vitest';

const exactApplyResponse = {
  outcome: 'invalid',
  errors: ['The confirmed team changed while defaults were being applied.'],
  preview: {
    blueprint_id: 'blueprint-software-development',
    blueprint_version: '2026.07.16',
    digest: 'preview-digest-1',
    can_apply: false,
    errors: ['A confirmed team is required before defaults can be applied.'],
    assignments: [{
      role_id: 'frontend-engineer',
      agent_name: 'Trinity',
      skill_name: 'ui-accessibility',
      action: 'blocked',
    }],
  },
};

afterEach(() => vi.unstubAllGlobals());

describe('AgentweaverApiClient skill defaults', () => {
  it('validates the exact apply response shape returned by the server', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => JSON.stringify(exactApplyResponse),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('http://localhost:5100');

    await expect(client.applyBlueprintSkillDefaults('proj-001', 'blueprint-software-development', 'preview-digest-1'))
      .resolves.toEqual(exactApplyResponse);
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5100/api/projects/proj-001/skill-defaults/apply',
      expect.objectContaining({
        method: 'POST',
        body: JSON.stringify({
          blueprint_id: 'blueprint-software-development',
          digest: 'preview-digest-1',
        }),
      }),
    );
  });

  it('rejects apply payloads that omit the required nested preview field', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => JSON.stringify({
        outcome: 'invalid',
        errors: ['Missing preview.'],
      }),
    }));
    const client = new AgentweaverApiClient('http://localhost:5100');

    await expect(client.applyBlueprintSkillDefaults('proj-001', 'blueprint-software-development', 'preview-digest-1'))
      .rejects.toThrow('Invalid apply blueprint skill defaults response.');
  });
});
