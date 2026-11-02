/**
 * Board task deletion against fixture Gateway responses only. This journey
 * checks the browser's keyboard, focus, denial, and narrow-screen behavior;
 * it does not prove live owner persistence or authorization.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { startStaticServer, openApp } from './harness.mjs';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });
const evidence = process.env.AGENTWEAVER_EVIDENCE_DIR;

const task = {
  reference: { projectId: 'p1', taskId: 'task-1' },
  revision: 3,
  state: 'backlog',
  isArchived: false,
  isAutomationInvocationPending: false,
  title: 'Review retention policy',
  description: null,
  lifecycle: 'open',
  repositoryIssue: null,
  position: 0,
};
const json = (status, body) => new Response(JSON.stringify(body), {
  status,
  headers: { 'content-type': 'application/json' },
});

test('delete confirmation is keyboard-safe, denied without removal, and contained at 390px', async () => {
  let graph = { projectId: 'p1', revision: 5, tasks: [task], dependencies: [] };
  let deny = true;
  const writes = [];
  const app = await openApp({
    origin: server.origin,
    extra: (method, path, url) => {
      if (method === 'GET' && path === '/projects/p1/backlog') return json(200, graph);
      if (method === 'DELETE' && path === '/projects/p1/backlog/tasks/task-1') {
        writes.push({ method, path, body: JSON.parse(url.body) });
        if (deny) return json(403, { error: 'project_permission_denied' });
        graph = { projectId: 'p1', revision: 6, tasks: [], dependencies: [] };
        return json(200, graph);
      }
      return undefined;
    },
  });

  try {
    const { page } = app;
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(`${server.origin}/projects/p1/board`);
    await page.getByRole('heading', { name: 'Agent task board' }).waitFor();
    const trigger = page.getByRole('button', { name: 'Delete task Review retention policy' });
    await trigger.waitFor();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false);

    await trigger.click();
    let dialog = page.getByRole('dialog', { name: 'Delete task?' });
    await dialog.waitFor();
    const cancel = dialog.getByRole('button', { name: 'Cancel' });
    assert.equal(await cancel.evaluate((element) => element === document.activeElement), true);
    await page.keyboard.press('Escape');
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(writes.length, 0);
    assert.equal(await trigger.evaluate((element) => element === document.activeElement), true);

    await trigger.click();
    dialog = page.getByRole('dialog', { name: 'Delete task?' });
    await dialog.getByRole('button', { name: 'Delete task', exact: true }).click();
    await dialog.getByRole('alert').getByText("You don't have permission to delete tasks in this project.").waitFor();
    await page.getByTestId('task-card-task-1').waitFor();
    assert.equal(writes.length, 1);
    assert.deepEqual(writes[0].body, { expectedGraphRevision: 5, expectedTaskRevision: 3 });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false);

    await dialog.getByRole('button', { name: 'Cancel' }).click();
    await dialog.waitFor({ state: 'hidden' });
    assert.equal(await trigger.evaluate((element) => element === document.activeElement), true);
    deny = false;
    await trigger.click();
    dialog = page.getByRole('dialog', { name: 'Delete task?' });
    await dialog.getByRole('button', { name: 'Delete task', exact: true }).click();
    await page.getByTestId('task-card-task-1').waitFor({ state: 'detached' });
    assert.equal(writes.length, 2);
    assert.equal(await page.getByRole('heading', { name: 'Agent task board' }).evaluate((element) => element === document.activeElement), true);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false);

    if (evidence) {
      await mkdir(evidence, { recursive: true });
      await writeFile(join(evidence, 'board-delete-browser.json'), JSON.stringify({
        head: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
        fixtureBacked: true,
        liveAcceptance: false,
        viewport: { width: 390, height: 844 },
        writes,
      }, null, 2));
    }
  } finally {
    await app.close();
  }
});
