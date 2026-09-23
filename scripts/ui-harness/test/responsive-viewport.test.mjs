import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createServer } from 'node:http';
import { rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';
import { openBrowserSession } from '../lib/browser.mjs';
import { attachPageCapture } from '../lib/evidence.mjs';
import { executeUiAction } from '../lib/ui-actions.mjs';

const fixture = `<!doctype html>
<html>
  <head>
    <style>
      html, body { margin: 0; overflow-x: hidden; }
      [data-testid="app-navigation-menu"] { display: block; height: 40px; }
      main { height: 650px; }
      [data-testid="run-operator-console"] { height: 560px; }
    </style>
  </head>
  <body>
    <nav data-testid="app-navigation-menu">Runs</nav>
    <main>
      <button data-testid="run-focus-toggle" aria-label="Enter focus mode" aria-pressed="false">Focus</button>
      <section data-testid="run-operator-console">Run content</section>
    </main>
    <script>
      document.querySelector('[data-testid="run-focus-toggle"]').addEventListener('click', (event) => {
        const active = event.currentTarget.getAttribute('aria-pressed') !== 'true';
        event.currentTarget.setAttribute('aria-pressed', String(active));
        event.currentTarget.setAttribute('aria-label', active ? 'Exit focus mode' : 'Enter focus mode');
      });
    </script>
  </body>
</html>`;
const HERE = path.dirname(fileURLToPath(import.meta.url));

async function listen(server) {
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  return server.address().port;
}

async function close(server) {
  await new Promise((resolve) => server.close(resolve));
}

test('desktop, constrained-height, and mobile viewport evidence is deterministic', { timeout: 180_000 }, async () => {
  const server = createServer((_request, response) => {
    response.writeHead(200, {
      connection: 'close',
      'content-length': Buffer.byteLength(fixture),
      'content-type': 'text/html; charset=utf-8',
    });
    response.end(fixture);
  });
  const port = await listen(server);
  const runtime = await openBrowserSession({
    baseUrl: `http://127.0.0.1:${port}`,
    headless: true,
  }, { chromium });
  const directory = path.join(HERE, `.responsive-${randomUUID()}`);
  const capture = attachPageCapture(runtime.page);
  const session = { persona: { text: 'Test persona' } };
  const execute = (eventId, args) => executeUiAction({
    runtime,
    capture,
    session,
    args,
    eventId,
    transcriptDirectory: directory,
  });

  try {
    await runtime.goto('/runs/fixture?credential=viewport-canary#responsive');
    const desktop = await execute(1, {
      _: ['viewport'], width: '1280', height: '900', 'focus-mode': 'standard',
    });
    const constrained = await execute(2, {
      _: ['viewport'], width: '1280', height: '480', 'focus-mode': 'standard',
    });
    await runtime.page.getByTestId('run-focus-toggle').click();
    const mobile = await execute(3, {
      _: ['viewport'], mobile: true, 'focus-mode': 'focused',
    });

    assert.deepEqual(desktop.viewport, { width: 1280, height: 900, deviceScaleFactor: 1 });
    assert.equal(desktop.overflow.vertical, false);
    assert.deepEqual(constrained.viewport, { width: 1280, height: 480, deviceScaleFactor: 1 });
    assert.equal(constrained.overflow.vertical, true);
    assert.equal(constrained.overflow.maximumScrollY > 0, true);
    assert.deepEqual(mobile.viewport, { width: 390, height: 844, deviceScaleFactor: 1 });
    assert.equal(mobile.overflow.horizontal, false);
    assert.equal(mobile.responsive.focusMode.active, true);
    assert.deepEqual(
      mobile.assertions.map((assertion) => assertion.category),
      ['navigation-reachability', 'overflow', 'overflow', 'focus-mode', 'content-visibility'],
    );
    assert.equal(mobile.assertions.every((assertion) => assertion.observed), true);
    assert.equal(JSON.stringify([desktop, constrained, mobile]).includes('viewport-canary'), false);
  } finally {
    await runtime.close();
    await rm(directory, { recursive: true, force: true });
    await close(server);
  }
});
