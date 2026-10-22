import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient } from './api';

const base = 'https://gateway.example.test/api/v1';

/** Builds a client whose fetch returns the given JSON bodies in order. */
function clientReturning(...bodies: Array<{ status?: number; body?: unknown }>) {
  const fetcher = vi.fn<typeof fetch>();
  for (const { status = 200, body } of bodies)
    fetcher.mockResolvedValueOnce(new Response(body === undefined ? null : JSON.stringify(body), { status }));
  return { fetcher, client: new AgentweaverGatewayClient(base, fetcher) };
}

describe('Gateway run, metrics, and notification routes', () => {
  it('lists project runs with only the set query parameters', async () => {
    const { fetcher, client } = clientReturning(
      { body: { projectId: 'p1', items: [], nextCursor: null } },
      { body: { projectId: 'p1', items: [], nextCursor: null } },
    );

    await client.listProjectRuns('token', 'p1');
    await client.listProjectRuns('token', 'p1', {
      states: ['open', 'cancelled'], startedBy: 'me', cursor: 'c1', pageSize: 10,
    });

    expect(String(fetcher.mock.calls[0][0])).toBe(`${base}/projects/p1/runs`);
    expect(String(fetcher.mock.calls[1][0])).toBe(
      `${base}/projects/p1/runs?state=open%2Ccancelled&startedBy=me&cursor=c1&pageSize=10`,
    );
  });

  it('rejects a run page for a different project', async () => {
    const { client } = clientReturning({ body: { projectId: 'other', items: [], nextCursor: null } });

    await expect(client.listProjectRuns('token', 'p1')).rejects.toMatchObject({ status: 502, code: 'owner_contract_invalid' });
  });

  it('sends create and cancel with an idempotency key and start without a body', async () => {
    const { fetcher, client } = clientReturning(
      { status: 201, body: { runId: 'r1', rootSessionId: 's1', state: 'awaiting_binding', binding: {} } },
      { body: { runId: 'r1', state: 'active' } },
      { body: { runId: 'r1', state: 'cancelled', cancelledAt: '2026-01-01T00:00:00Z', alreadyTerminal: false } },
    );

    await client.createProjectRun('token', 'p1', { firstMessage: 'Hello', expectedProjectConfigRevision: 4 }, 'key-1');
    await client.startProjectRun('run-token', 'p1', 'r1');
    await client.cancelProjectRun('token', 'p1', 'r1', 'key-2');

    const [createUrl, createInit] = fetcher.mock.calls[0];
    expect(String(createUrl)).toBe(`${base}/projects/p1/runs`);
    expect(createInit?.method).toBe('POST');
    expect(new Headers(createInit?.headers).get('Idempotency-Key')).toBe('key-1');
    expect(JSON.parse(String(createInit?.body))).toEqual({ firstMessage: 'Hello', expectedProjectConfigRevision: 4 });

    const [startUrl, startInit] = fetcher.mock.calls[1];
    expect(String(startUrl)).toBe(`${base}/projects/p1/runs/r1/start`);
    expect(startInit?.method).toBe('POST');
    expect(startInit?.body).toBeUndefined();

    const [cancelUrl, cancelInit] = fetcher.mock.calls[2];
    expect(String(cancelUrl)).toBe(`${base}/projects/p1/runs/r1/cancel`);
    expect(new Headers(cancelInit?.headers).get('Idempotency-Key')).toBe('key-2');
    expect(JSON.parse(String(cancelInit?.body))).toEqual({});
  });

  it('reads metrics by day and usage for a range, and checks the project', async () => {
    const { fetcher, client } = clientReturning(
      { body: { projectId: 'p1', series: [] } },
      { body: { projectId: 'p1', usageByModel: [], usageByAgent: [] } },
      { body: { projectId: 'p2', series: [] } },
    );

    await client.getProjectMetrics('token', 'p1', { from: '2026-01-01', to: '2026-01-31' });
    await client.getProjectUsage('token', 'p1');

    expect(String(fetcher.mock.calls[0][0])).toBe(`${base}/projects/p1/metrics?from=2026-01-01&to=2026-01-31&bucket=day`);
    expect(String(fetcher.mock.calls[1][0])).toBe(`${base}/projects/p1/usage`);
    await expect(client.getProjectMetrics('token', 'p1')).rejects.toMatchObject({ status: 502 });
  });

  it('lists and dismisses notifications', async () => {
    const { fetcher, client } = clientReturning(
      { body: { items: [], count: 0, nextCursor: null } },
      { status: 204 },
    );

    await client.listNotifications('token', { projectId: 'p1', includeDismissed: false });
    await expect(client.dismissNotification('token', 'n 1')).resolves.toBeUndefined();

    expect(String(fetcher.mock.calls[0][0])).toBe(`${base}/notifications?projectId=p1&includeDismissed=false`);
    expect(String(fetcher.mock.calls[1][0])).toBe(`${base}/notifications/n%201/dismiss`);
    expect(fetcher.mock.calls[1][1]?.method).toBe('POST');
  });

  it('reads platform components', async () => {
    const { fetcher, client } = clientReturning({ body: { observedAt: '2026-01-01T00:00:00Z', areas: [] } });

    await client.getPlatformComponents('token');

    expect(String(fetcher.mock.calls[0][0])).toBe(`${base}/platform/components`);
  });
});
