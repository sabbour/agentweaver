import { getSessionToken } from './auth.mjs';

function pageItems(response, expectedPage, resource) {
  if (!response || typeof response !== 'object' || Array.isArray(response)
    || !Array.isArray(response.items)
    || !Number.isInteger(response.page)
    || !Number.isInteger(response.total_pages)
    || !Number.isInteger(response.total_count)
    || response.page !== expectedPage
    || response.total_pages < 0
    || response.total_count < 0) {
    throw new Error(`${resource} pagination response is incomplete; refusing to treat a partial page as a complete list.`);
  }
  return response.items;
}

async function listAllPages(loadPage, resource) {
  const items = [];
  for (let page = 1; ; page += 1) {
    const response = await loadPage(page);
    items.push(...pageItems(response, page, resource));
    if (page >= response.total_pages) {
      if (items.length !== response.total_count) {
        throw new Error(`${resource} pagination changed or is incomplete; refusing to treat a partial list as complete.`);
      }
      return items;
    }
  }
}

export function createAgentweaverApi({ baseUrl, token, fetchImpl = fetch }) {
  const root = new URL(baseUrl);
  const apiRoot = new URL('/api/', root);

  async function request(path, init = {}) {
    const response = await fetchImpl(new URL(path, apiRoot), {
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
    listProjects(pageSize = 100, page = 1) {
      return request(`projects?page=${page}&page_size=${pageSize}`);
    },
    listAllProjects() {
      return listAllPages((page) => this.listProjects(100, page), 'Project');
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
    listProjectSessions(projectId, pageSize = 100, page = 1) {
      return request(`projects/${encodeURIComponent(projectId)}/sessions?page=${page}&page_size=${pageSize}`);
    },
    listAllProjectSessions(projectId) {
      return listAllPages(
        (page) => this.listProjectSessions(projectId, 100, page),
        `Project ${projectId} session`,
      );
    },
    listProjectWorkflows(projectId) {
      return request(`projects/${encodeURIComponent(projectId)}/workflows`);
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
