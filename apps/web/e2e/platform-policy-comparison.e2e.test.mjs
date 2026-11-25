import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { startStaticServer, openApp } from './harness.mjs';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

const json = (status, body) => new Response(JSON.stringify(body), {
  status,
  headers: { 'content-type': 'application/json' },
});
const bytes = (value) => [...new TextEncoder().encode(value)];

test('policy comparison contains long paths and hashes at 320px', async () => {
  const hash = 'abcdef0123456789'.repeat(4);
  const longPath = `${'long-segment-'.repeat(15)}/manifest`;
  const app = await openApp({
    origin: server.origin,
    extra: (method, path) => {
      if (method !== 'GET') return undefined;
      if (path === '/policies/platform/revisions') return json(200, {
        scopeType: 'platform',
        scopeId: 'default',
        policyId: 'policy-1',
        snapshotRevision: 1,
        items: [{
          revision: 1,
          contentSha256: hash,
          createdAt: '2026-01-02T03:04:05Z',
        }],
        nextCursor: null,
      });
      if (path === '/policies/platform/revisions/1') return json(200, {
        scopeType: 'platform',
        scopeId: 'default',
        policyId: 'policy-1',
        revision: 1,
        contentSha256: hash,
        createdAt: '2026-01-02T03:04:05Z',
        artifact: {
          manifestBytes: bytes('name: saved\n'),
          files: [],
          contentSha256: hash,
        },
      });
      return undefined;
    },
  });
  try {
    const { page } = app;
    await page.setViewportSize({ width: 320, height: 900 });
    await page.goto(`${server.origin}/policies`);
    await page.getByRole('button', { name: 'View' }).waitFor();
    const skipTour = page.getByRole('button', { name: 'Skip product tour' });
    if (await skipTour.isVisible())
      await skipTour.click();
    const view = page.getByRole('button', { name: 'View' });
    await view.focus();
    await view.press('Enter');
    await page.getByRole('heading', { name: 'Revision 1' }).waitFor({ state: 'attached' });
    const addFile = page.getByRole('button', { name: 'Add file' });
    await addFile.focus();
    await addFile.press('Enter');
    await page.getByRole('textbox', { name: 'Relative path' }).fill(longPath);
    await page.getByRole('textbox', {
      name: `Content for ${longPath}`,
    }).fill('draft content');
    const compare = page.getByRole('button', { name: 'Compare draft' });
    await compare.focus();
    await compare.press('Enter');
    const comparison = page.getByRole('region', {
      name: 'Draft comparison with revision 1',
    });
    await comparison.waitFor({ state: 'attached' });
    const picker = page.getByRole('combobox', { name: 'Changed Policy item' });
    await picker.selectOption(`file:${longPath}`);
    const widths = await comparison.evaluate((element) => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    assert.ok(
      widths.scrollWidth <= widths.clientWidth + 1,
      `comparison overflowed at 320px: ${JSON.stringify(widths)}`,
    );
    assert.equal(await picker.inputValue(), `file:${longPath}`);
    assert.ok((await comparison.textContent()).includes(hash));
  } finally {
    await app.close();
  }
});
