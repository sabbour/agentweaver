import { AgentweaverApiClient } from '../api/client';
import { afterEach, describe, expect, it, vi } from 'vitest';

describe('setup state API reads', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('bypasses the browser cache for session and provider readiness reads', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      text: vi.fn().mockResolvedValue('{}'),
    });
    vi.stubGlobal('fetch', fetchMock);
    const client = new AgentweaverApiClient('https://agentweaver.example', 'token');

    await client.getAuthSession();
    await client.listByokProviders();
    await client.getPlatformDefaultCopilotConnection();

    expect(fetchMock).toHaveBeenCalledTimes(3);
    for (const [, options] of fetchMock.mock.calls) {
      expect(options).toMatchObject({ method: 'GET', cache: 'no-store' });
    }
  });
});
