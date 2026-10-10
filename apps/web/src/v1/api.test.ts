import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient } from './api';
import type { KnowledgeRecordTransferBundle } from './contracts';

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

  it('validates the owner authorization context and forwards only the explicit tenant selector', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        contractVersion: 1,
        issuer: 'https://identity.test/',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 2,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    const context = await client.getAuthorizationContext('broker-token', 'tenant-1');

    expect(context.tenantId).toBe('tenant-1');
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe('https://gateway.example.test/api/v1/authorization/context');
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(init?.cache).toBe('no-store');
  });

  it('rejects an authorization context outside the exact owner DTO', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        contractVersion: 1,
        issuer: 'https://identity.test/',
        actorId: 'actor-1',
        tenantId: 'tenant-1',
        membershipRevision: 2,
        boundProjectId: null,
        boundRunId: null,
        effectiveAuthority: [],
        role: 'tenantAdmin',
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.getAuthorizationContext('broker-token')).rejects.toMatchObject({
      status: 502,
      code: 'owner_contract_invalid',
    });
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
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
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

  it('uses the Knowledge revision, Decision lifecycle, and bounded transfer routes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => Response.json({}),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const transfer: KnowledgeRecordTransferBundle = {
      format: 'agentweaver.knowledge-transfer.v1',
      schemaVersion: 1,
      projectId: 'p1',
      agentId: 'agent-1',
      records: [],
    };

    await client.readKnowledgeRevisions(
      'token', 'p1', 'r1', 'agent-1', 'record/1', { page: 2, pageSize: 25 },
    );
    await client.restoreKnowledgeRecord(
      'token', 'p1', 'r1', 'agent-1', 'record/1', 8, 3, 'Restore reviewed revision', 'restore-intent',
    );
    await client.approveKnowledgeDecision(
      'token', 'p1', 'r1', 'agent-1', 'decision-1', 9, null, 'approval-intent',
    );
    await client.exportKnowledgeRecords('token', 'p1', 'r1', 'agent-1');
    await client.importKnowledgeRecords(
      'token', 'p1', 'r1', 'agent-1', transfer, 'import-intent',
    );

    expect(fetcher.mock.calls.map(([url]) => String(url))).toEqual([
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records/record%2F1/revisions?page=2&pageSize=25',
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records/record%2F1/restore',
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records/decision-1/approve',
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records/export',
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/agents/agent-1/records/import',
    ]);

    expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({
      expectedRevision: 8,
      revision: 3,
      reason: 'Restore reviewed revision',
    });
    expect(JSON.parse(String(fetcher.mock.calls[2][1]?.body))).toEqual({
      expectedRevision: 9,
      reason: null,
    });
    expect(JSON.parse(String(fetcher.mock.calls[4][1]?.body))).toEqual(transfer);
    expect([
      fetcher.mock.calls[1],
      fetcher.mock.calls[2],
      fetcher.mock.calls[4],
    ].map(([, init]) => new Headers(init?.headers).get('Idempotency-Key'))).toEqual([
      'restore-intent',
      'approval-intent',
      'import-intent',
    ]);
  });

  it('uses the public Skills and marketplace routes with owner-shaped requests', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const candidate = { skillMarkdown: 'IyBUZXN0', resources: [] };

    await client.listMarketplaceSources('token', 'project/1', 'tenant-1');
    await client.createMarketplaceSource('token', 'project/1', {
      repository: 'example/skills',
      requestedRef: 'main',
    }, 'tenant-1');
    await client.updateMarketplaceSource('token', 'project/1', 'source/1', {
      expectedRevision: 4,
      repository: 'example/skills',
    }, 'tenant-1');
    await client.removeMarketplaceSource('token', 'project/1', 'source/1', 5, 'tenant-1');
    await client.browseMarketplaceSource('token', 'project/1', 'source/1', {
      expectedSourceRevision: 6,
      query: 'skill author',
      page: 2,
      pageSize: 10,
    }, 'tenant-1');
    await client.previewSkillContent('token', candidate, 'tenant-1');
    await client.importSkillContent('token', 'project/1', {
      idempotencyKey: 'upload-intent',
      expectedContentDigest: 'sha256:digest',
      candidate,
    }, 'tenant-1');
    await client.updateSkillAssignment('token', 'project/1', 'skill/1', {
      expectedProjectConfigurationRevision: 8,
      revision: 3,
      contentDigest: 'sha256:digest',
      enabled: true,
      order: 0,
      agentIds: ['agent-1'],
    }, 'tenant-1');

    expect(fetcher.mock.calls.map(([url, init]) => [
      String(url),
      init?.method ?? 'GET',
      init?.body ? JSON.parse(String(init.body)) : undefined,
      new Headers(init?.headers).get('X-Agentweaver-Tenant'),
      init?.cache,
    ])).toEqual([
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources', 'GET', undefined, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources', 'POST', {
        repository: 'example/skills',
        requestedRef: 'main',
      }, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources/source%2F1', 'PUT', {
        expectedRevision: 4,
        repository: 'example/skills',
      }, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources/source%2F1?expectedRevision=5', 'DELETE', undefined, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources/source%2F1/browse?expectedSourceRevision=6&query=skill+author&page=2&pageSize=10', 'GET', undefined, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/skills/preview', 'POST', { candidate }, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skills/import', 'POST', {
        idempotencyKey: 'upload-intent',
        expectedContentDigest: 'sha256:digest',
        candidate,
      }, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skills/skill%2F1/assignment', 'PUT', {
        expectedProjectConfigurationRevision: 8,
        revision: 3,
        contentDigest: 'sha256:digest',
        enabled: true,
        order: 0,
        agentIds: ['agent-1'],
      }, 'tenant-1', 'no-store'],
    ]);
    for (const [, init] of fetcher.mock.calls) {
      expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer token');
      expect(init?.credentials).toBe('omit');
    }
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
      'tenant-1',
    )) frames.push(frame);

    expect(frames).toEqual([{ cursor: 'cursor-12', event: envelope }]);
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/project-1/runs/run-1/events/live?cursor=cursor-11',
    );
    expect(new Headers(init?.headers).get('Accept')).toBe('text/event-stream');
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer broker-token');
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(init?.credentials).toBe('omit');
  });

  it('keeps Repo App authorization at the Gateway root and stores its callback cookie', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.beginRepoAppAuthorization('broker-token');
    await client.getRepoAppAuthorizationStatus('broker-token');
    await client.listRepoAppRepositorySelections('broker-token');
    await client.issueRepoAppRepositorySelection('broker-token', 'octo/agentweaver');

    const [beginUrl, beginInit] = fetcher.mock.calls[0];
    expect(String(beginUrl)).toBe(
      'https://gateway.example.test/api/auth/github/repo-app/authorizations',
    );
    expect(JSON.parse(String(beginInit?.body))).toEqual({ returnRouteKey: 'projects' });
    expect(beginInit?.credentials).toBe('include');
    expect(new Headers(beginInit?.headers).get('X-Agentweaver-Tenant')).toBeNull();
    expect(String(fetcher.mock.calls[1][0])).toBe(
      'https://gateway.example.test/api/auth/github/repo-app/authorization/status',
    );
    expect(String(fetcher.mock.calls[2][0])).toBe(
      'https://gateway.example.test/api/github/repository-selections',
    );
    expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('X-Agentweaver-Tenant')).toBeNull();
    expect(JSON.parse(String(fetcher.mock.calls[3][1]?.body))).toEqual({
      fullName: 'octo/agentweaver',
    });
    expect(fetcher.mock.calls.slice(1).every(([, init]) => init?.credentials === 'omit')).toBe(true);
  });

  it('sends only Remote MCP OAuth code and state through the authenticated Gateway route', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.completeRemoteMcpOAuthCallback(
      'current-user-token',
      { state: 'A'.repeat(43), code: 'provider-code' },
      'tenant-1',
    );

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/connections/remote-mcp/v1/callback',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      state: 'A'.repeat(43),
      code: 'provider-code',
    });
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(init?.credentials).toBe('omit');
  });

  it('sends provider cancellation without adding a code to the callback request', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.completeRemoteMcpOAuthCallback(
      'current-user-token',
      { state: 'B'.repeat(43), error: 'access_denied' },
      'tenant-1',
    );

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/connections/remote-mcp/v1/callback',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      state: 'B'.repeat(43),
      error: 'access_denied',
    });
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(init?.credentials).toBe('omit');
  });

  it('sends refresh revisions to the connection-bound Gateway route', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.refreshRemoteMcpOAuthConnection(
      'current-user-token',
      'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      {
        expectedConnectionRevision: 4,
        expectedCredentialRevision: 2,
        expectedConfigurationRevision: 2,
      },
      'tenant-1',
    );

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/connections/remote-mcp/v1/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/refresh',
    );
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({
      expectedConnectionRevision: 4,
      expectedCredentialRevision: 2,
      expectedConfigurationRevision: 2,
    });
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('keeps repository pin run-bound and preserves legacy bodyless pin requests', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.pinSourceControlRepository(
      'broker-token', 'project-1', 'run-1', 'root-1', 'tenant-1', 'opaque-selection-code',
    );
    await client.pinSourceControlRepository(
      'broker-token', 'project-1', 'run-1', 'root-1', 'tenant-1',
    );

    const [selectionUrl, selectionInit] = fetcher.mock.calls[0];
    expect(String(selectionUrl)).toBe(
      'https://gateway.example.test/api/v1/projects/project-1/runs/run-1/source-control/sessions/root-1/pin',
    );
    expect(selectionInit?.method).toBe('POST');
    expect(new Headers(selectionInit?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(JSON.parse(String(selectionInit?.body))).toEqual({
      selectionCode: 'opaque-selection-code',
    });
    const [, legacyInit] = fetcher.mock.calls[1];
    expect(legacyInit?.method).toBe('POST');
    expect(new Headers(legacyInit?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(legacyInit?.body).toBeUndefined();
  });

});
