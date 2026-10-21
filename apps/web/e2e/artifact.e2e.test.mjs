/**
 * Saved-output (artifact) journey against the built web app, with fixtures shaped
 * like the real owner contracts.
 * Optional AGENTWEAVER_EVIDENCE_DIR keeps screenshots outside the repository.
 * The 0.x comparison renders the retained FilesTabPanel/FileViewer components,
 * not made-up legacy capture or Priority screens. No live services, work or
 * mutations are involved.
 */
import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { join, resolve } from 'node:path';
import { createServer as createViteServer } from 'vite';
import { startStaticServer, openApp, runRoom } from './harness.mjs';
import { priority, run } from '../src/v1/__fixtures__/cosGateway.ts';
import { captureDetail, capturedPath, capturedText } from '../src/v1/__fixtures__/outputCapture.ts';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });
const evidence = process.env.AGENTWEAVER_EVIDENCE_DIR;
/** Builds a JSON Gateway response. */
const json = (status, body) => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

test('artifact: choose run/session/capture/file, link whole capture, reopen after reload, then lose read access', async () => {
  const detail = captureDetail();
  // The room supplies the run's sessions and records any run writes.
  const room = runRoom({ projectId: 'p1', runId: 'r-1' });
  // Every capture read is kept, for the assertions and the evidence file.
  const outputReads = [];
  // Flipped near the end to simulate losing project read access.
  let revoked = false;
  const app = await openApp({
    origin: server.origin,
    seed: { runs: { p1: [run('r-1', 'active', 'Release notes')] }, priorities: [priority('pri-a', 'Prepare the release')] },
    extra: (method, path, url, binding) => {
      const prefix = `/projects/p1/runs/r-1/source-control/sessions/`;
      if (path.startsWith(prefix)) {
        // Capture reads must use the run-bound token and must never write.
        assert.deepEqual(binding, { projectId: 'p1', runId: 'r-1' }, 'every capture read is run-bound');
        assert.equal(method, 'GET');
        outputReads.push({ path, url: url.toString() });
        if (revoked) return json(403, { error: 'project_read_permission_denied' });
        const session = path.slice(prefix.length).split('/')[0];
        if (path.endsWith('/output-captures')) {
          // The root session has no captures, so the picker shows its empty state first.
          if (session === room.rootSessionId) return json(200, { captures: [] });
          // The child session pages its list: the first page holds only a newer
          // capture, so the user must press "Load more" to reach capture-1.
          if (!url.searchParams.has('beforeCaptureId')) return json(200, {
            captures: [captureDetail('r-1', 'newer-capture').capture],
            nextBeforeCapturedAt: detail.capture.capturedAt, nextBeforeCaptureId: 'newer-capture',
          });
          // The second page must use the cursor from the first page.
          assert.equal(url.searchParams.get('beforeCapturedAt'), detail.capture.capturedAt);
          assert.equal(url.searchParams.get('beforeCaptureId'), 'newer-capture');
          return json(200, { captures: [detail.capture] });
        }
        if (path.endsWith('/output-captures/capture-1')) return json(200, detail);
        if (path.endsWith('/output-captures/capture-1/files')) {
          // A file is read by its path inside the capture, as raw bytes.
          assert.equal(url.searchParams.get('path'), capturedPath);
          return new Response(capturedText, { headers: { 'content-type': 'application/octet-stream' } });
        }
        return json(404, { error: 'output_capture_not_found' });
      }
      // All other run routes (sessions, decisions, writes) come from the room.
      return room.handle(method, path, url, binding);
    },
  });
  /** Saves a screenshot of one element, only when an evidence folder is set. */
  const shot = async (name, locator) => {
    if (!evidence) return;
    await mkdir(evidence, { recursive: true });
    await locator.screenshot({ path: join(evidence, `${name}.png`) });
  };
  try {
    const { page } = app;
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.goto(`${server.origin}/overview`);
    const row = page.getByRole('listitem', { name: 'Priority 1: Prepare the release' });
    await row.getByRole('button', { name: 'Add link to Prepare the release' }).click();
    const form = row.getByRole('form', { name: 'Add link to Prepare the release' });
    await form.getByLabel('Link type').selectOption('artifact');
    // The picker opens on the root session, which has no captures.
    await form.getByText('No saved captures', { exact: true }).waitFor();
    await form.getByLabel('Session', { exact: true }).selectOption(room.childSessionId);
    await form.getByLabel('Saved capture').waitFor();
    await shot('pr-capture-list-new-gateway-adapter', form);
    // capture-1 is on the second page, so the user must load more to pick it.
    await form.getByRole('button', { name: 'Load more captures' }).click();
    await form.getByLabel('Saved capture').selectOption('capture-1');
    const details = form.getByRole('region', { name: 'Capture details' });
    await details.getByText('1 files', { exact: false }).waitFor();
    await shot('pr-capture-detail-new-gateway-adapter', details);
    // Opening the Markdown file shows its rendered preview first.
    await details.getByText('release notes.md', { exact: true }).click();
    await details.getByRole('heading', { name: 'Release notes' }).waitFor();
    // Screenshot the parent so the file tree and the viewer are both in the shot.
    const fileSurface = details.getByRole('region', { name: 'Captured files' }).locator('..');
    const desktopBounds = await fileSurface.boundingBox();
    await shot('pr-files-preview', fileSurface);
    await details.getByRole('tab', { name: 'Source' }).click();
    assert.equal(await details.getByRole('tab', { name: 'Source' }).getAttribute('aria-selected'), 'true');
    await shot('pr-files-source', fileSurface);
    // Phone width with the navigation collapsed: the file browser must fit
    // without horizontal scrolling.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Collapse navigation' }).click();
    await shot('pr-files-mobile', fileSurface);
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'no mobile horizontal overflow');
    const bounds = await fileSurface.boundingBox();
    assert.ok(bounds && bounds.x >= 0 && bounds.x + bounds.width <= 390, 'file browser fits the visible content area');
    await page.setViewportSize({ width: 1440, height: 1100 });
    await page.getByRole('button', { name: 'Expand navigation' }).click();
    // Link the whole capture, not a single file.
    await details.getByRole('button', { name: 'Link capture' }).click();
    await row.getByRole('button', { name: 'Open artifact' }).waitFor();
    assert.deepEqual(app.gateway.state.priorities[0].links.map(({ kind, projectId, runId, artifactId }) => ({ kind, projectId, runId, artifactId })),
      [{ kind: 'artifact', projectId: 'p1', runId: 'r-1', artifactId: 'capture-1' }]);
    const linkWrite = app.gateway.writes().filter((request) => request.url.pathname.endsWith('/links'));
    assert.equal(linkWrite.length, 1);
    assert.equal(linkWrite[0].headers.get('authorization'), 'Bearer tok.unbound');
    assert.deepEqual(room.writes, [], 'linking starts no work');
    // After a reload the link must come from the server and reopen the same capture.
    await page.reload();
    await page.getByRole('button', { name: 'Open artifact' }).click();
    await page.waitForURL(/view=changes&captureId=capture-1/);
    await page.getByRole('region', { name: 'Artifacts' }).getByLabel('Session', { exact: true }).selectOption(room.childSessionId);
    await page.getByRole('button', { name: 'Load more captures' }).click();
    const reopened = page.getByRole('region', { name: 'Capture details' });
    await reopened.getByText('release notes.md', { exact: true }).click();
    await reopened.getByRole('heading', { name: 'Release notes' }).waitFor();
    // Now the owner refuses capture reads. After a reload the page must show the
    // error and keep no file list or file text from before.
    revoked = true;
    await page.reload();
    await page.getByRole('region', { name: 'Artifacts' }).getByLabel('Session', { exact: true }).selectOption(room.childSessionId);
    await page.getByRole('alert').filter({ hasText: 'project_read_permission_denied' }).waitFor();
    assert.equal(await page.getByRole('region', { name: 'Captured files' }).count(), 0, 'revocation removes manifest and text');
    assert.equal(await page.getByRole('heading', { name: 'Release notes' }).count(), 0);
    assert.ok(outputReads.length >= 8);
    // Write a summary of the journey for reviewers. It says plainly that this
    // is fixture-backed and not a live acceptance run.
    if (evidence) await writeFile(join(evidence, 'artifact-journey.json'), JSON.stringify({
      fixtureBacked: true, liveAcceptance: false,
      prHead: execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(),
      mobileAppViewportWidth: 390, mobileFileSurfaceWidth: Math.round(bounds.width),
      desktopFileSurfaceWidth: Math.round(desktopBounds.width),
      reference: 'origin/dev fb24c71c4d912354998ccaf775af12be4a049798',
      reused: ['FilesTabPanel', 'FileViewer'], newGatewayAdapters: ['saved capture list', 'capture detail', 'Priority artifact picker'],
      outputReads, writes: linkWrite.map((request) => ({ method: request.method, path: request.url.pathname, body: request.body })),
    }, null, 2));
  } finally { await app.close(); }
});

test('retained 0.x file tree/viewer screenshot reference', { skip: !evidence }, async () => {
  // Only useful when collecting evidence. Vite serves the retained 0.x
  // components from source, since they are not part of the built app.
  const vite = await createViteServer({ root: resolve('.'), server: { host: '127.0.0.1', port: 0 }, logLevel: 'error' });
  await vite.listen();
  // Reuse the harness only for its browser; this page makes no Gateway calls.
  const app = await openApp({ origin: server.origin });
  try {
    const port = vite.httpServer.address().port;
    const journey = JSON.parse(await readFile(join(evidence, 'artifact-journey.json'), 'utf8'));
    // Both desktop and mobile comparisons use the exact component width inside the PR.
    await app.page.setViewportSize({ width: journey.desktopFileSurfaceWidth, height: 1100 });
    await app.page.goto(`http://127.0.0.1:${port}/e2e/retained-reference.html`);
    const surface = app.page.getByRole('region', { name: '0.x retained file browser' });
    await surface.getByText('release notes.md', { exact: true }).click();
    await surface.getByRole('heading', { name: 'Release notes' }).waitFor();
    await surface.screenshot({ path: join(evidence, '0x-files-preview.png') });
    await surface.getByRole('tab', { name: 'Source' }).click();
    await surface.screenshot({ path: join(evidence, '0x-files-source.png') });
    // The first test wrote this file. It holds the measured mobile width.
    // Match the component width inside the PR shell, not the outer app viewport.
    await app.page.setViewportSize({ width: journey.mobileFileSurfaceWidth, height: 844 });
    await surface.screenshot({ path: join(evidence, '0x-files-mobile.png') });
  } finally { await app.close(); await vite.close(); }
});
