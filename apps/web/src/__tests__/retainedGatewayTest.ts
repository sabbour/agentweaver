/** In-memory published Gateway reads for the retained 0.x page tests. */
export function gatewayFixtureFetch(input: RequestInfo | URL): Promise<Response> {
  const url = new URL(String(input), 'http://localhost');
  const path = url.pathname;
  const now = new Date().toISOString();
  const project = { projectId: 'p1', name: 'Demo', state: 'active', revision: 1, configurationRevision: 1, createdAt: now, updatedAt: now };
  const metrics = {
    projectId: 'p1', from: now, to: now, activeRuns: 2, blockedRuns: 1,
    runsInRange: 5, runsTotal: 20, completedRuns: 4, succeededRuns: 3,
    series: [{ date: now.slice(0, 10), created: 5, completed: 4 }],
  };
  const usage = {
    projectId: 'p1', from: now, to: now,
    usageByModel: [{
      id: 'gpt-5', requestCount: 2, inputTokens: 10, outputTokens: 20,
      cachedTokens: 0, cacheWriteTokens: 0, reasoningTokens: 0,
      avgDurationMs: 100, runs: 1, unpricedRequestCount: 0, cost: [],
    }],
    usageByAgent: [],
  };
  const responses: Record<string, unknown> = {
    '/api/v1/projects': [project],
    '/api/v1/projects/p1': project,
    '/api/v1/projects/p1/configuration': {
      revision: 1,
      configuration: { sourceControl: { repository: { owner: 'sabbour', name: 'demo' } }, agentCharters: [{ name: 'Ada' }] },
    },
    '/api/v1/projects/p1/runs': {
      projectId: 'p1',
      items: [{
        runId: 'r1', rootSessionId: null, title: 'Run failed', goal: null, state: 'failed',
        createdAt: now, updatedAt: now, completedAt: now, startedBy: { issuer: 'test', subject: 'test' },
      }],
      nextCursor: null,
    },
    '/api/v1/projects/p1/backlog': {
      projectId: 'p1', revision: 1, tasks: [{ taskId: 't1', isArchived: false }], dependencies: [],
    },
    '/api/v1/projects/p1/metrics': metrics,
    '/api/v1/projects/p1/usage': usage,
    '/api/v1/notifications': { count: 0, items: [], nextCursor: null },
  };
  return Promise.resolve(new Response(JSON.stringify(responses[path] ?? { detail: 'Missing fixture' }), {
    status: Object.hasOwn(responses, path) ? 200 : 404,
    headers: { 'Content-Type': 'application/json' },
  }));
}

/** Returns a fetch stub that fails selected published routes and serves the shared fixture elsewhere. */
export function gatewayFailureFetch(paths: readonly string[]): (input: RequestInfo | URL) => Promise<Response> {
  const failedPaths = new Set(paths);
  return (input) => {
    // Match on the public route path so the test remains independent of query-string order.
    const path = new URL(String(input), 'http://localhost').pathname;
    if (failedPaths.has(path)) {
      return Promise.resolve(new Response(JSON.stringify({ detail: 'Gateway read failed' }), {
        status: 503,
        headers: { 'Content-Type': 'application/json' },
      }));
    }
    return gatewayFixtureFetch(input);
  };
}
