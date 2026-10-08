import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient } from './api';

describe('Gateway API client', () => {
  it('does not send a request without a Broker access token', async () => {
    const fetcher = vi.fn<typeof fetch>();
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.listProjects('')).rejects.toMatchObject({
      status: 401,
      code: 'unauthorized',
    });
    expect(fetcher).not.toHaveBeenCalled();
  });

  it('preserves explicit project authorization errors from the Gateway', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        type: 'about:blank',
        title: 'Forbidden',
        status: 403,
        detail: 'The current identity has no access to this project.',
        code: 'project_access_denied',
      }), { status: 403, headers: { 'Content-Type': 'application/problem+json' } }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    const request = client.getProject('broker-issued-token', 'team/project');
    await expect(request).rejects.toMatchObject({
      name: 'GatewayError',
      status: 403,
      code: 'project_access_denied',
      message: 'The current identity has no access to this project.',
    });

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe('https://gateway.example.test/api/v1/projects/team%2Fproject');
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer broker-issued-token');
    expect(init?.credentials).toBe('omit');
    expect(init?.cache).toBe('no-store');
  });

  it('keeps structured Gateway errors available for stale exact-gate handling', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ code: 'stale_decision_state', detail: 'The decision version changed.' }), {
        status: 409,
        headers: { 'Content-Type': 'application/problem+json' },
      }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.getProject('token', 'p1')).rejects.toMatchObject({
      name: 'GatewayError',
      status: 409,
      code: 'stale_decision_state',
      message: 'The decision version changed.',
    });
  });

  it('sends explicit denial to the exact owner gate and expected decision version', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const idempotencyKey = 'stable-user-intent-key';

    await client.resolveGate('token', 'project-1', 'run-1', 'session-1', 'request/7', 19, false, idempotencyKey);

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/project-1/runs/run-1/coordination/sessions/session-1/decisions/gates/request%2F7/reject',
    );
    const body = JSON.parse(String(init?.body)) as { expectedStateVersion: number; idempotencyKey: string };
    expect(body.expectedStateVersion).toBe(19);
    expect(body.idempotencyKey).toBe(idempotencyKey);
    expect(init?.method).toBe('POST');
  });

  it('sends Knowledge idempotency keys in the required header', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('{}', { status: 200 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const idempotencyKey = 'knowledge-create-intent';

    await client.createKnowledgeRecord('token', 'p1', 'r1', 'agent-1', {
      kind: 'memory',
      type: 'fact',
      content: 'A tested fact',
      importance: 'normal',
      tags: [],
    }, idempotencyKey);

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records',
    );
    expect(new Headers(init?.headers).get('Idempotency-Key')).toBe(idempotencyKey);
    expect(init?.method).toBe('POST');
  });

  it('can replay a user intent with the same coordinator key', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const idempotencyKey = 'answer-intent-retry';

    await client.answerGate('token', 'p1', 'r1', 's1', 'request-1', 8, { choiceId: 'yes' }, idempotencyKey);
    await client.answerGate('refreshed-token', 'p1', 'r1', 's1', 'request-1', 8, { choiceId: 'yes' }, idempotencyKey);

    const bodies = fetcher.mock.calls.map(([, init]) =>
      JSON.parse(String(init?.body)) as { idempotencyKey: string });
    expect(bodies.map((body) => body.idempotencyKey)).toEqual([idempotencyKey, idempotencyKey]);
  });

  it('parses ordered server-sent event frames with the Broker token', async () => {
    const envelope = {
      schemaVersion: 1,
      eventVersion: 1,
      eventId: 'event-1',
      identity: { projectId: 'project-1', runId: 'run-1', sessionId: 'session-1' },
      position: 12,
      occurredAt: '2026-01-01T00:00:00Z',
      kind: 'addressedMessage',
      payload: { messageId: 'message-1' },
      objectReferences: [],
    };
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        const bytes = new TextEncoder().encode(
          `id: cursor-12\r\ndata: ${JSON.stringify(envelope)}\r\n\r\n`,
        );
        const split = bytes.length - 2;
        controller.enqueue(bytes.slice(0, split));
        controller.enqueue(bytes.slice(split));
        controller.close();
      },
    });
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(body, { status: 200, headers: { 'Content-Type': 'text/event-stream' } }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const frames = [];

    for await (const frame of client.streamEvents(
      'broker-token',
      'project-1',
      'run-1',
      'cursor-11',
      new AbortController().signal,
    )) frames.push(frame);

    expect(frames).toEqual([{ cursor: 'cursor-12', event: envelope }]);
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/project-1/runs/run-1/events/live?cursor=cursor-11',
    );
    expect(new Headers(init?.headers).get('Accept')).toBe('text/event-stream');
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer broker-token');
    expect(init?.credentials).toBe('omit');
  });
});
