import { AgentweaverApiClient } from '../api/client';
import { afterEach, describe, expect, it, vi } from 'vitest';

describe('workflow public contract client', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('requests the published workflow grammar', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue('{}'),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://agentweaver.example', 'token');

    await client.getWorkflowGrammar();

    expect(fetchMock).toHaveBeenCalledWith(
      'https://agentweaver.example/api/workflows/grammar',
      expect.objectContaining({ method: 'GET' }),
    );
  });

  it('serializes supported run-event filters', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue('[]'),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://agentweaver.example', 'token');

    await client.getRunEvents('run id', { type: 'run.failed', after: 7, limit: 25 });

    expect(fetchMock).toHaveBeenCalledWith(
      'https://agentweaver.example/api/runs/run%20id/events?type=run.failed&after=7&limit=25',
      expect.objectContaining({ method: 'GET' }),
    );
  });
});
