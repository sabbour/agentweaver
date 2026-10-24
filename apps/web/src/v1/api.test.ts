import { describe, expect, it, vi } from 'vitest';
import { AgentweaverGatewayClient } from './api';
import type { KnowledgeRecordTransferBundle, ProjectConfiguration } from './contracts';

describe('Gateway API client', () => {
  it('reads exact captured file bytes and the digest with the explicit tenant', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(new Uint8Array([0, 255, 65]), {
      headers: { 'X-Source-Control-Output-Sha256': 'digest' },
    }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const result = await client.readSourceControlOutputCaptureFile('token', 'p1', 'r1', 's1', 'tenant', 'capture/1', 'folder/a #.bin');
    expect([...result.bytes]).toEqual([0, 255, 65]);
    expect(result.sha256).toBe('digest');
    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toContain('/output-captures/capture%2F1/files?path=folder%2Fa+%23.bin');
    expect(new Headers(init?.headers).get('Accept')).toBe('application/octet-stream');
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant');
  });

  it('keeps capture file errors structured instead of returning success-shaped bytes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response('{"code":"forbidden"}', { status: 403 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    await expect(client.readSourceControlOutputCaptureFile('token', 'p', 'r', 's', null, 'c', 'a.txt'))
      .rejects.toMatchObject({ status: 403, code: 'forbidden' });
  });

  it('rejects a manifest belonging to a different capture', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(new Response(JSON.stringify({
      capture: { captureId: 'other' }, manifest: { files: [] },
    })));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    await expect(client.readSourceControlOutputCapture('token', 'p', 'r', 's', null, 'requested'))
      .rejects.toMatchObject({ status: 502, code: 'invalid_owner_response' });
  });

  it('sends empty provider overrides on project configuration and casting draft writes', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => new Response('{}', {
      status: 200, headers: { 'Content-Type': 'application/json' },
    }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const configuration = {
      modelSelection: { reference: 'model-a' },
      providerOverrides: [{ seam: 'sandbox', providerId: 'legacy-sandbox' }],
      orderedProviderOverrides: [{ seam: 'guardrails', providerIds: ['legacy-a', 'legacy-b'] }],
    } as unknown as ProjectConfiguration;

    await client.updateProjectConfiguration('token', 'p1', 3, configuration);
    await client.createCastingDraftProposal('token', 'p1', { expectedConfigurationRevision: 3, draft: configuration });
    await client.updateCastingProposal('token', 'p1', 'proposal-1', { expectedDraftRevision: 2, draft: configuration });

    const bodies = fetcher.mock.calls.map(([, init]) => JSON.parse(String(init?.body)));
    expect(bodies[0]).toEqual({
      expectedRevision: 3,
      configuration: { modelSelection: { reference: 'model-a' }, providerOverrides: [], orderedProviderOverrides: [] },
    });
    for (const body of bodies.slice(1)) {
      expect(body.draft.modelSelection).toEqual({ reference: 'model-a' });
      expect(body.draft.providerOverrides).toEqual([]);
      expect(body.draft.orderedProviderOverrides).toEqual([]);
    }
    expect(configuration.providerOverrides).toHaveLength(1);
  });

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

  // The revision the user saw is sent so the server can reject an edit based on stale data.
  it('renames or archives a project with the displayed project revision', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.updateProject('token', 'project/1', { expectedRevision: 4, name: 'Launch', state: 'archived' });

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe('https://gateway.example.test/api/v1/projects/project%2F1');
    expect(init?.method).toBe('PATCH');
    expect(JSON.parse(String(init?.body))).toEqual({ expectedRevision: 4, name: 'Launch', state: 'archived' });
  });

  // Revoke must send the assignment revision as `expectedRevision` so a changed
  // assignment is not revoked by mistake.
  it('lists, assigns and revokes project roles on the role-assignment contract', async () => {
    const assignment = {
      assignmentId: '7f8c1a2e-0000-4000-8000-000000000001', principalId: 'user-2', role: 'viewer',
      revision: 3, createdBy: 'admin', createdAt: '2026-01-01T00:00:00Z',
    };
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify({ projectId: 'p1', assignments: [assignment] }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(assignment), { status: 201 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.listProjectRoleAssignments('token', 'p1')).resolves.toMatchObject({ assignments: [assignment] });
    await client.assignProjectRole('token', 'p1', { principalId: 'user-2', role: 'viewer' });
    await client.revokeProjectRoleAssignment('token', 'p1', assignment.assignmentId, 3);

    expect(String(fetcher.mock.calls[0][0])).toBe('https://gateway.example.test/api/v1/projects/p1/role-assignments');
    expect(fetcher.mock.calls[1][1]?.method).toBe('POST');
    expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ principalId: 'user-2', role: 'viewer' });
    expect(String(fetcher.mock.calls[2][0])).toBe(
      `https://gateway.example.test/api/v1/projects/p1/role-assignments/${assignment.assignmentId}?expectedRevision=3`,
    );
    expect(fetcher.mock.calls[2][1]?.method).toBe('DELETE');
  });

  it('returns the assignment view from add and remove', async () => {
    const view = {
      assignmentId: 'as-1', projectId: 'p1', principalId: 'user-2', role: 'viewer', state: 'revoked',
      revision: 4, grantedBy: 'admin', grantedAt: '2026-01-01T00:00:00Z', revokedBy: 'admin', revokedAt: '2026-01-02T00:00:00Z',
    };
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...view, state: 'active', revision: 3, revokedBy: null, revokedAt: null }), { status: 201 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(view), { status: 200 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.assignProjectRole('token', 'p1', { principalId: 'user-2', role: 'viewer' }))
      .resolves.toMatchObject({ state: 'active', revision: 3 });
    await expect(client.revokeProjectRoleAssignment('token', 'p1', 'as-1', 3)).resolves.toMatchObject({ state: 'revoked', revision: 4 });
    expect(String(fetcher.mock.calls[1][0])).toBe('https://gateway.example.test/api/v1/projects/p1/role-assignments/as-1?expectedRevision=3');
  });

  it('rejects a role-assignment list for a different project', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ projectId: 'other', assignments: [] }), { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.listProjectRoleAssignments('token', 'p1')).rejects.toMatchObject({
      status: 502, code: 'owner_contract_invalid',
    });
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

  it('searches project Knowledge without requiring a run or agent in the request path', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => Response.json({ items: [], totalCount: 0, page: 2, pageSize: 10 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.searchProjectKnowledge(
      'token',
      'p1',
      { query: 'deployment', kind: 'memory', includeInactive: false, page: 2, pageSize: 10 },
      'tenant-1',
    );

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/p1/knowledge/records?q=deployment&kind=memory&page=2&pageSize=10&includeInactive=false',
    );
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
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
    const selectedSourceContent = {
      expectedSourceRevision: 6,
      resolvedCommitSha: '0123456789abcdef',
      selectedPath: 'skills/sample',
    };
    await client.previewMarketplaceSkillContent(
      'token', 'project/1', 'source/1', selectedSourceContent, 'tenant-1',
    );
    await client.importMarketplaceSkillContent('token', 'project/1', 'source/1', {
      ...selectedSourceContent,
      idempotencyKey: 'source-import-intent',
      expectedContentDigest: 'sha256:source-digest',
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
    await client.listProjectSkillInventory('token', 'project/1', {
      cursor: 'cursor/opaque',
      pageSize: 25,
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
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources/source%2F1/preview', 'POST', selectedSourceContent, 'tenant-1', 'no-store'],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/sources/source%2F1/import', 'POST', {
        ...selectedSourceContent,
        idempotencyKey: 'source-import-intent',
        expectedContentDigest: 'sha256:source-digest',
      }, 'tenant-1', 'no-store'],
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
      ['https://gateway.example.test/api/v1/projects/project%2F1/skills?cursor=cursor%2Fopaque&pageSize=25', 'GET', undefined, 'tenant-1', 'no-store'],
    ]);
    for (const [, init] of fetcher.mock.calls) {
      expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer token');
      expect(init?.credentials).toBe('omit');
    }
  });

  it('routes AgentSuite import, record and policy reads to the project owner', async () => {
    const fetcher = vi.fn<typeof fetch>().mockImplementation(
      async () => new Response('{}', { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);
    const request = {
      expectedConfigurationRevision: 3,
      format: 'oci-layout-tar' as const,
      archive: 'AAAA',
      toolConnections: [{ toolProvider: 'search@1.0.0', connectionId: '6f1c2d4e-8a9b-4c3d-9e2f-1a2b3c4d5e6f' }],
    };

    await client.importProjectAgentSuite('token', 'project/1', request, 'tenant-1');
    await client.getProjectAgentSuiteImport('token', 'project/1', 'sha256:abc', 'tenant-1');
    await client.getProjectAgentSuiteAgentPolicy('token', 'project/1', 'agentsuite:team:writer:0123456789ab', 'tenant-1');

    expect(fetcher.mock.calls.map(([url, init]) => [
      String(url),
      init?.method ?? 'GET',
      init?.body ? JSON.parse(String(init.body)) : undefined,
    ])).toEqual([
      ['https://gateway.example.test/api/v1/projects/project%2F1/agentsuite-imports', 'POST', request],
      ['https://gateway.example.test/api/v1/projects/project%2F1/agentsuite-imports/sha256%3Aabc', 'GET', undefined],
      ['https://gateway.example.test/api/v1/projects/project%2F1/agentsuite-agents/agentsuite%3Ateam%3Awriter%3A0123456789ab/policy', 'GET', undefined],
    ]);
  });

  it('routes AgentSuite package export to the project owner with the selected revision', async () => {
    const response = {
      format: 'content-tar-gzip',
      suiteName: 'Team One',
      sourceConfigurationRevision: 8,
      archive: 'H4sIAAAAAAAA',
    };
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify(response), { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.exportProjectAgentSuite('token', 'project/1', 8, 'tenant-1')).resolves.toEqual(response);

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/project%2F1/agentsuite-exports?revision=8',
    );
    expect(init?.method ?? 'GET').toBe('GET');
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
  });

  it('reads suggested marketplaces and rejects a malformed list', async () => {
    const definition = { name: 'Public', repository: 'example/skills', requestedRef: 'main', subpath: null, layoutNote: null, sourceId: null };
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify([definition]), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify([{ name: 'No repository' }]), { status: 200 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.listMarketplaceDefinitions('token', 'project/1', 'tenant-1')).resolves.toEqual([definition]);
    expect(String(fetcher.mock.calls[0][0]))
      .toBe('https://gateway.example.test/api/v1/projects/project%2F1/skill-marketplaces/definitions');
    expect(fetcher.mock.calls[0][1]?.method ?? 'GET').toBe('GET');
    await expect(client.listMarketplaceDefinitions('token', 'project/1')).rejects.toMatchObject({
      status: 502, code: 'invalid_owner_response',
    });
  });

  it('packages skill content and revokes a skill revision on the public routes', async () => {
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify({ skills: [] }), { status: 200 }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.packageSkillContent('token', { draft: { name: 'code-review', instructions: 'Check it.' } }, 'tenant-1'))
      .resolves.toEqual({ skills: [] });
    await expect(client.revokeProjectSkillRevision('token', 'project/1', 'skill/1', 3, 'No longer used.', 'tenant-1'))
      .resolves.toBeUndefined();

    expect(fetcher.mock.calls.map(([url, init]) => [String(url), init?.method, JSON.parse(String(init?.body))])).toEqual([
      ['https://gateway.example.test/api/v1/skills/package', 'POST', { draft: { name: 'code-review', instructions: 'Check it.' } }],
      ['https://gateway.example.test/api/v1/projects/project%2F1/skills/skill%2F1/revisions/3/revoke', 'POST', { reason: 'No longer used.' }],
    ]);
  });

  it('lists recorded pull requests with only the set filters and checks the project', async () => {
    const page = { projectId: 'p1', coverage: {}, items: [], nextCursor: null };
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify(page), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ ...page, projectId: 'other' }), { status: 200 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await client.listProjectPullRequests('token', 'p1', { state: 'open', pageSize: 20 });
    expect(String(fetcher.mock.calls[0][0]))
      .toBe('https://gateway.example.test/api/v1/projects/p1/source-control/pull-requests?state=open&pageSize=20');
    await expect(client.listProjectPullRequests('token', 'p1')).rejects.toMatchObject({ status: 502 });
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
    // window.fetch throws "Illegal invocation" unless it is called on the global object.
    expect(fetcher.mock.contexts[0]).toBe(globalThis);
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

  it('lists project Remote MCP connections through the Gateway directory route', async () => {
    const entry = {
      connectionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee',
      displayName: 'Docs',
      endpointUri: 'https://mcp.example.test/mcp',
      resourceUri: null,
      authenticationMode: 'delegatedOAuth',
      transportProfile: 'streamableHttp20250618',
      state: 'enabled',
      rowRevision: 3,
      configurationRevision: 2,
      configurationSha256: 'a'.repeat(64),
      identityBound: true,
      registryServerName: null,
      registryExactVersion: null,
      discoveryRevision: 1,
      catalogSha256: 'b'.repeat(64),
      toolCount: 2,
    };
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({ connections: [entry], nextConnectionId: null }), { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    const page = await client.listRemoteMcpConnections(
      'current-user-token',
      'project-1',
      { pageSize: 10, after: 'aaaaaaaa-bbbb-cccc-dddd-000000000000' },
      'tenant-1',
    );

    const [url, init] = fetcher.mock.calls[0];
    expect(String(url)).toBe(
      'https://gateway.example.test/api/v1/projects/project-1/remote-mcp/connections?pageSize=10&after=aaaaaaaa-bbbb-cccc-dddd-000000000000',
    );
    expect(new Headers(init?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');
    expect(page.connections[0].configurationSha256).toBe('a'.repeat(64));
  });

  it('rejects a Remote MCP directory page with a malformed checksum', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(
      new Response(JSON.stringify({
        connections: [{ connectionId: 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee', configurationSha256: 'nope' }],
        nextConnectionId: null,
      }), { status: 200 }),
    );
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    await expect(client.listRemoteMcpConnections('current-user-token', 'project-1'))
      .rejects.toMatchObject({ status: 502, code: 'invalid_owner_response' });
  });

  it('lists run output captures with a paired cursor and reads one diff', async () => {
    const capture = {
      captureId: 'cap 1', eventId: 'e1', eventPosition: 1, capturedAt: '2026-10-08T00:00:00Z',
      workspaceId: 'w1', branchName: 'b', baseSha: 'a', outputTreeSha: 't', manifestSha256: 'm',
      manifestByteLength: 1, patchSha256: 'p', patchByteLength: 1, packageByteLength: 1,
    };
    const fetcher = vi.fn<typeof fetch>()
      .mockResolvedValueOnce(new Response(JSON.stringify({ captures: [capture], nextBeforeCapturedAt: null, nextBeforeCaptureId: null }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ captures: [], nextBeforeCapturedAt: null, nextBeforeCaptureId: null }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ capture, patch: '+x' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ capture: { ...capture, captureId: 'other' }, patch: '' }), { status: 200 }));
    const client = new AgentweaverGatewayClient('https://gateway.example.test/api/v1', fetcher);

    const page = await client.listRunOutputCaptures('t', 'p1', 'r1', 's1', { limit: 20 }, 'tenant-1');
    expect(page.captures).toHaveLength(1);
    expect(String(fetcher.mock.calls[0][0])).toBe(
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/source-control/sessions/s1/output-captures?limit=20');
    expect(new Headers(fetcher.mock.calls[0][1]?.headers).get('X-Agentweaver-Tenant')).toBe('tenant-1');

    // A half cursor is not sent; the service needs both parts together.
    await client.listRunOutputCaptures('t', 'p1', 'r1', 's1', { beforeCapturedAt: '2026-10-08T00:00:00Z', beforeCaptureId: null }, 'tenant-1');
    expect(String(fetcher.mock.calls[1][0])).toBe(
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/source-control/sessions/s1/output-captures');

    const diff = await client.readRunOutputCaptureDiff('t', 'p1', 'r1', 's1', 'cap 1', 'tenant-1');
    expect(diff.patch).toBe('+x');
    expect(String(fetcher.mock.calls[2][0])).toBe(
      'https://gateway.example.test/api/v1/projects/p1/runs/r1/source-control/sessions/s1/output-captures/cap%201/diff');
    await expect(client.readRunOutputCaptureDiff('t', 'p1', 'r1', 's1', 'cap 1', 'tenant-1')).rejects.toMatchObject({ status: 502 });
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
