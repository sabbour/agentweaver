import assert from 'node:assert/strict';
import test from 'node:test';
import { createAgentweaverApi } from '../lib/api.mjs';

test('listAllProjects retrieves every API page, including projects past page 100', async () => {
  const projects = Array.from({ length: 101 }, (_value, index) => ({
    project_id: `demo-${index + 1}`,
    name: `Agentweaver Demo ${index + 1}`,
  }));
  const requestedPages = [];
  const api = createAgentweaverApi({
    baseUrl: 'https://staging.example',
    token: 'test-token',
    fetchImpl: async (url) => {
      const page = Number(url.searchParams.get('page'));
      const pageSize = Number(url.searchParams.get('page_size'));
      requestedPages.push({ page, pageSize });
      const start = (page - 1) * pageSize;
      return {
        ok: true,
        text: async () => JSON.stringify({
          items: projects.slice(start, start + pageSize),
          page,
          page_size: pageSize,
          total_count: projects.length,
          total_pages: 2,
        }),
      };
    },
  });

  const result = await api.listAllProjects();

  assert.equal(result.length, 101);
  assert.equal(result.at(-1).project_id, 'demo-101');
  assert.deepEqual(requestedPages, [{ page: 1, pageSize: 100 }, { page: 2, pageSize: 100 }]);
});
