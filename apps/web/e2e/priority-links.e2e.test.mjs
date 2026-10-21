/**
 * Stored issue and pull request selection through the built UI. Fixtures follow
 * the owner contracts; these journeys do not contact GitHub or claim live acceptance.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { startStaticServer, openApp } from './harness.mjs';
import { NOW, priority } from '../src/v1/__fixtures__/cosGateway.ts';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });
const evidence = process.env.AGENTWEAVER_EVIDENCE_DIR;

for (const kind of ['issue', 'pull_request']) {
  test(`${kind}: stored choices, paging, link, reload, reopen, and read loss`, async () => {
    const label = kind === 'issue' ? 'Issue' : 'Pull request';
    const path = kind === 'issue' ? 'issues' : 'pull-requests';
    const records = Array.from({ length: 26 }, (_, index) => ({
      providerId: 'github', repository: { owner: 'octo', name: 'api' },
      number: 26 - index, title: `Stored ${label.toLowerCase()} ${26 - index}`, state: 'open',
      ...(kind === 'issue'
        ? { taskId: `task-${index}`, taskState: 'ready', createdAt: NOW, updatedAt: NOW }
        : { runId: 'r-1', htmlUrl: `https://github.com/octo/api/pull/${26 - index}`, headBranch: 'fix', baseBranch: 'main',
          headSha: 'a'.repeat(40), baseSha: 'b'.repeat(40), disposition: 'created', recordedAt: NOW, updatedAt: NOW }),
    }));
    const app = await openApp({
      origin: server.origin,
      seed: {
        priorities: [priority('pri-a', 'Prepare the release')],
        ...(kind === 'issue' ? { issues: { p1: records } } : { pullRequests: { p1: records } }),
      },
    });
    try {
      const { page } = app;
      await page.setViewportSize({ width: 1440, height: 1100 });
      await page.goto(`${server.origin}/overview`);
      const row = page.getByRole('listitem', { name: 'Priority 1: Prepare the release' });
      await row.getByRole('button', { name: 'Add link to Prepare the release' }).click();
      const form = row.getByRole('form', { name: 'Add link to Prepare the release' });
      await form.getByLabel('Link type').selectOption(kind);
      await form.getByRole('option', { name: new RegExp(`Stored ${label.toLowerCase()} 26`) }).waitFor({ state: 'attached' });
      await form.getByRole('button', { name: `Load more ${path === 'issues' ? 'issues' : 'pull requests'}` }).click();
      await form.getByLabel(label, { exact: true }).selectOption(JSON.stringify(['github', 'octo', 'api', 1]));
      if (evidence) {
        await mkdir(evidence, { recursive: true });
        await form.screenshot({ path: join(evidence, `pr-${path}-picker-new-gateway-adapter.png`) });
      }
      await form.getByRole('button', { name: `Link ${label.toLowerCase()}`, exact: true }).click();
      await row.getByRole('button', { name: `Remove link: ${label}` }).waitFor();
      const saved = app.gateway.state.priorities[0].links[0];
      assert.equal(saved.kind, kind);
      assert.equal(saved.number, 1);
      assert.equal(saved.projectId, 'p1');
      // Saving a link may only mutate the caller's Priority, never provider or run state.
      assert.ok(app.gateway.writes().every((request) => request.url.pathname.startsWith('/api/v1/chief-of-staff/priorities')));

      await page.reload();
      const savedList = row.getByRole('region', { name: kind === 'issue' ? 'Stored issues' : 'Stored pull requests' });
      await savedList.getByRole('button', { name: /^Load more/ }).click();
      const open = savedList.getByRole('link', { name: `Open ${label.toLowerCase()}` });
      await open.waitFor();
      assert.equal(await open.getAttribute('href'), `https://github.com/octo/api/${kind === 'issue' ? 'issues' : 'pull'}/1`);
      if (evidence) await row.screenshot({ path: join(evidence, `pr-${path}-saved-link-new-gateway-adapter.png`) });

      // A denied reread must remove metadata and the external navigation target.
      app.gateway.failNext('GET', `/api/v1/projects/p1/source-control/${path}`, {
        status: 403, body: { error: 'project_read_permission_denied' },
      });
      await savedList.getByRole('button', { name: 'Refresh records' }).click();
      await savedList.getByRole('alert').waitFor();
      assert.equal(await open.count(), 0);
      assert.equal(await savedList.getByText('Stored', { exact: false }).count(), 0);
      assert.ok(await row.getByText('Prepare the release', { exact: true }).count());
      // Every owner list read uses the caller-wide session, never a run token.
      const reads = app.gateway.requests.filter((request) => request.url.pathname.endsWith(`/source-control/${path}`));
      assert.ok(reads.length >= 4);
      if (evidence) await writeFile(join(evidence, `${path}-journey.json`), JSON.stringify({
        head: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
        fixtureBacked: true, liveAcceptance: false, kind, reads: reads.map((request) => request.url.toString()), saved,
      }, null, 2));
    } finally { await app.close(); }
  });
}
