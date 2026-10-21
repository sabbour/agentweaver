import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient, GatewayError } from './api';
import type { BacklogGraph } from './contracts';

const graph: BacklogGraph = { projectId: 'p 1', revision: 7, tasks: [], dependencies: [] };

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function client(fetcher: typeof fetch) {
  return new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
}

describe('Gateway backlog and issue client', () => {
  it('reads the project backlog with the bearer token and tenant', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(graph));
    await expect(client(fetcher).readProjectBacklog('token', 'p 1', 'tenant-1')).resolves.toEqual(graph);
    const [url, init] = fetcher.mock.calls[0];
    expect(url).toBe('https://gateway.example.test/api/v1/projects/p%201/backlog');
    expect(init?.method).toBeUndefined();
    const headers = new Headers(init?.headers);
    expect(headers.get('Authorization')).toBe('Bearer token');
    expect(headers.get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('rejects a backlog for another project', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({ ...graph, projectId: 'other' }));
    await expect(client(fetcher).readProjectBacklog('token', 'p 1')).rejects.toMatchObject({ status: 502, code: 'owner_contract_invalid' });
  });

  it('captures a task with POST and the expected graph revision', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(graph));
    const request = { taskId: 'task-1', content: { title: 'Write docs' }, expectedGraphRevision: 6 };
    await client(fetcher).captureBacklogTask('token', 'p 1', request);
    const [url, init] = fetcher.mock.calls[0];
    expect(url).toBe('https://gateway.example.test/api/v1/projects/p%201/backlog/tasks');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual(request);
  });

  it('edits task content with PUT and both revisions', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(graph));
    const request = { content: { title: 'New title', description: null }, expectedGraphRevision: 7, expectedTaskRevision: 2 };
    await client(fetcher).editBacklogTaskContent('token', 'p 1', 'task/1', request);
    const [url, init] = fetcher.mock.calls[0];
    expect(url).toBe('https://gateway.example.test/api/v1/projects/p%201/backlog/tasks/task%2F1/content');
    expect(init?.method).toBe('PUT');
    expect(JSON.parse(String(init?.body))).toEqual(request);
  });

  it('uses the first backlog issue message when a write is rejected', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({
      issues: [{ code: 'staleGraphRevision', path: 'expectedGraphRevision', message: 'The graph revision is stale.' }],
    }, 409));
    const failure = client(fetcher).captureBacklogTask('token', 'p 1', { taskId: 't', content: { title: 'x' }, expectedGraphRevision: 1 });
    await expect(failure).rejects.toBeInstanceOf(GatewayError);
    await expect(failure).rejects.toMatchObject({ status: 409, message: 'The graph revision is stale.' });
  });

  it('lists issues with only the query parameters that are set', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(json({ projectId: 'p 1', items: [], nextCursor: null }))
      .mockResolvedValueOnce(json({ projectId: 'p 1', items: [], nextCursor: null }));
    await client(fetcher).listProjectIssues('token', 'p 1');
    await client(fetcher).listProjectIssues('token', 'p 1', { state: 'all', cursor: 'next page', pageSize: 100 });
    expect(fetcher.mock.calls[0][0]).toBe('https://gateway.example.test/api/v1/projects/p%201/source-control/issues');
    expect(fetcher.mock.calls[1][0]).toBe(
      'https://gateway.example.test/api/v1/projects/p%201/source-control/issues?state=all&cursor=next+page&pageSize=100');
  });

  it('rejects an issue page for another project', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json({ projectId: 'other', items: [], nextCursor: null }));
    await expect(client(fetcher).listProjectIssues('token', 'p 1')).rejects.toMatchObject({ status: 502 });
  });
});
