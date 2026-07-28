import { getSessionToken } from './auth.mjs';

export function createAgentweaverApi({ baseUrl, token }) {
  const root = new URL(baseUrl);
  const apiRoot = new URL('/api/', root);

  async function request(path, init = {}) {
    const response = await fetch(new URL(path, apiRoot), {
      ...init,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...(init.headers ?? {}),
      },
    });
    const text = await response.text();
    if (!response.ok) {
      throw new Error(`${init.method ?? 'GET'} ${path} failed: ${response.status} ${text}`);
    }
    return text ? JSON.parse(text) : null;
  }

  return {
    listProjects(pageSize = 100) {
      return request(`projects?page_size=${pageSize}`);
    },
    deleteProject(projectId) {
      return request(`projects/${encodeURIComponent(projectId)}?confirm=true`, { method: 'DELETE' });
    },
    createProject(body) {
      return request('projects', { method: 'POST', body: JSON.stringify(body) });
    },
    startOrchestration(projectId, body) {
      return request(`projects/${encodeURIComponent(projectId)}/orchestrations`, {
        method: 'POST',
        body: JSON.stringify(body),
      });
    },
    getOutcomeSpec(runId) {
      return request(`runs/${encodeURIComponent(runId)}/outcome-spec`);
    },
    reviseOutcomeSpec(runId, feedback) {
      return request(`runs/${encodeURIComponent(runId)}/outcome-spec/revise`, {
        method: 'POST',
        body: JSON.stringify({ feedback }),
      });
    },
    confirmOutcomeSpec(runId, allowTaskPromotion = false) {
      return request(`runs/${encodeURIComponent(runId)}/outcome-spec/confirm`, {
        method: 'POST',
        body: JSON.stringify({ allowTaskPromotion }),
      });
    },
    listProjectRuns(projectId, { pageSize = 50, includeChildren = false } = {}) {
      const query = new URLSearchParams({
        page_size: String(pageSize),
        include_children: includeChildren ? 'true' : 'false',
      });
      return request(`projects/${encodeURIComponent(projectId)}/runs?${query.toString()}`);
    },
    getRunEvents(runId) {
      return request(`runs/${encodeURIComponent(runId)}/events`);
    },
  };
}

export async function createApiFromSession({ baseUrl, sessionStoragePath }) {
  const token = await getSessionToken(sessionStoragePath);
  return createAgentweaverApi({ baseUrl, token });
}
