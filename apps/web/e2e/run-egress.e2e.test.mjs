import { after, before, test } from 'node:test';
import assert from 'node:assert/strict';
import { openApp, startStaticServer } from './harness.mjs';

let server;
before(async () => { server = await startStaticServer(); });
after(async () => { await server?.close(); });

test('the run egress table shows keyboard focus and scrolls at 320px', async () => {
  const app = await openApp({
    origin: server.origin,
    extra(method, path, _url, binding) {
      if (method !== 'GET' || path !== '/runs/run-1/egress') return undefined;
      if (binding?.projectId !== 'p1' || binding.runId != null)
        return Response.json({ error: 'caller_binding_mismatch' }, { status: 403 });
      return Response.json({
        runId: 'run-1',
        coverage: 'Unavailable',
        delayedUntil: null,
        items: [{
          host: 'artifact-registry.example.test',
          address: '2001:db8:0:0:0:0:0:1',
          port: 443,
          verdict: 'allowed',
          firstSeen: '2026-11-20T10:00:00.123401+00:00',
          lastSeen: '2026-11-20T10:00:00.123402+00:00',
          count: 2,
        }],
        nextCursor: null,
      });
    },
  });

  try {
    const { page } = app;
    await page.setViewportSize({ width: 320, height: 768 });
    await page.goto(`${server.origin}/projects/p1/runs/run-1/history?view=egress`);
    const region = page.getByRole('region', { name: 'Stored run egress records' });
    await region.getByRole('table', { name: 'Stored run egress' }).waitFor();
    assert.ok(await region.evaluate((element) => element.scrollWidth > element.clientWidth),
      'The table must overflow its scroll region at this width.');

    await page.getByRole('tab', { name: 'Egress' }).focus();
    await page.keyboard.press('Tab');
    assert.ok(await region.evaluate((element) => {
      const { outlineStyle, outlineWidth, outlineColor } = getComputedStyle(element);
      return document.activeElement === element && element.matches(':focus-visible') &&
        outlineStyle === 'solid' && parseFloat(outlineWidth) >= 2 && outlineColor !== 'rgba(0, 0, 0, 0)';
    }), 'Tab must focus the scroll region and show its outline.');
    const initialScrollLeft = await region.evaluate((element) => element.scrollLeft);
    await page.keyboard.press('ArrowRight');
    await page.waitForFunction((start) => document.activeElement.scrollLeft > start, initialScrollLeft);
    assert.ok(await region.evaluate((element) => document.activeElement === element));
  } finally {
    await app.close();
  }
});
