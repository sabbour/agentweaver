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
import { computeDriverP0 } from '../lib/reporter-ui.mjs';

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
const reachabilityFixture = `<!doctype html>
<html>
  <head>
    <style>
      html, body { margin: 0; overflow-x: hidden; }
      nav, button { height: 40px; }
      main { min-height: 1600px; }
      [data-testid="reachable-content"] { height: 60px; margin-top: 1100px; }
      [data-testid="clipped-container"] { height: 80px; overflow: clip; position: relative; }
      [data-testid="clipped-content"] { height: 40px; position: absolute; top: 160px; }
    </style>
  </head>
  <body>
    <nav data-testid="app-navigation-menu">Runs</nav>
    <button data-testid="run-focus-toggle" aria-label="Enter focus mode" aria-pressed="false">Focus</button>
    <main>
      <section data-testid="reachable-content">Reachable content</section>
      <div data-testid="clipped-container">
        <section data-testid="clipped-content">Clipped content</section>
      </div>
    </main>
  </body>
</html>`;
const rootOverflowFixture = (overflowY) => `<!doctype html>
<html style="overflow-y: ${overflowY}">
  <head>
    <style>
      html, body { margin: 0; }
      main { height: 1600px; }
    </style>
  </head>
  <body>
    <nav data-testid="app-navigation-menu">Runs</nav>
    <button data-testid="run-focus-toggle" aria-label="Enter focus mode" aria-pressed="false">Focus</button>
    <main data-testid="run-operator-console">Run content</main>
  </body>
</html>`;
const workflowListFixture = `<!doctype html>
<html>
  <head>
    <style>
      html, body { margin: 0; }
      nav { height: 40px; }
      main { padding: 12px; }
      [data-testid^="workflow-card-"] { box-sizing: border-box; width: 100%; padding: 8px; }
      [data-testid="workflow-card-custom-fan-coordinator-demo"] > div { display: flex; flex-wrap: wrap; gap: 4px; }
      button { min-height: 32px; }
    </style>
  </head>
  <body>
    <nav data-testid="app-navigation-menu">Workflows</nav>
    <main>
      <article data-testid="workflow-card-other"><button>View graph</button><input aria-label="Instructions"></article>
      <article data-testid="workflow-card-custom-fan-coordinator-demo">
        <div data-testid="workflow-summary">Summary</div>
        <div data-testid="workflow-controls">
          <button>View graph</button><button>Run</button><button>Edit</button>
          <button>Copy</button><button>History</button><button>Delete</button>
          <input aria-label="Instructions">
        </div>
      </article>
    </main>
    <script>
      document.querySelectorAll('button').forEach((button) =>
        button.addEventListener('click', () => button.setAttribute('data-clicked', 'true')));
    </script>
  </body>
</html>`;
const HERE = path.dirname(fileURLToPath(import.meta.url));
const TEST_BROWSER_ENVIRONMENT = Object.freeze({
  NODE_ENV: 'test',
  AGENTWEAVER_UI_HARNESS_TEST_BROWSER: '1',
});

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

async function cleanupFixture(runtime, directory, server) {
  const errors = [];
  for (const cleanup of [
    () => runtime?.close(),
    () => rm(directory, { recursive: true, force: true }),
    () => close(server),
  ]) {
    try {
      await cleanup();
    } catch (error) {
      errors.push(error);
    }
  }
  if (errors.length === 1) throw errors[0];
  if (errors.length > 1) throw new AggregateError(errors, 'failed to clean responsive viewport fixture');
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
  const directory = path.join(HERE, `.responsive-${randomUUID()}`);
  let runtime;

  try {
    runtime = await openBrowserSession({
      baseUrl: `http://127.0.0.1:${port}`,
      headless: true,
    }, { chromium, environment: TEST_BROWSER_ENVIRONMENT });
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
    assert.equal(mobile.responsive.navigation.scroll.attempted, true);
    assert.equal(mobile.responsive.navigation.inViewport, true);
    assert.equal(mobile.responsive.focusMode.scroll.attempted, true);
    assert.deepEqual(mobile.responsive.focusMode.clippedBy, []);
    assert.equal(mobile.responsive.content.scroll.attempted, true);
    assert.equal(mobile.responsive.content.inViewport, true);
    assert.deepEqual(
      mobile.assertions.map((assertion) => assertion.category),
      ['navigation-reachability', 'overflow', 'overflow', 'focus-mode', 'content-visibility'],
    );
    assert.equal(mobile.assertions.every((assertion) => assertion.observed), true);
    assert.equal(JSON.stringify([desktop, constrained, mobile]).includes('viewport-canary'), false);
  } finally {
    await cleanupFixture(runtime, directory, server);
  }
});

test('target-specific scrolling distinguishes reachable and nested-clipped content', { timeout: 180_000 }, async () => {
  const server = createServer((_request, response) => {
    response.writeHead(200, {
      connection: 'close',
      'content-length': Buffer.byteLength(reachabilityFixture),
      'content-type': 'text/html; charset=utf-8',
    });
    response.end(reachabilityFixture);
  });
  const port = await listen(server);
  const directory = path.join(HERE, `.responsive-${randomUUID()}`);
  let runtime;

  try {
    runtime = await openBrowserSession({
      baseUrl: `http://127.0.0.1:${port}`,
      headless: true,
    }, { chromium, environment: TEST_BROWSER_ENVIRONMENT });
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
    await runtime.goto('/reachability');
    const reachable = await execute(1, {
      _: ['viewport'],
      width: '800',
      height: '480',
      'content-test-id': 'reachable-content',
    });
    const clipped = await execute(2, {
      _: ['viewport'],
      width: '800',
      height: '480',
      'content-test-id': 'clipped-content',
    });

    assert.equal(reachable.responsive.content.scroll.attempted, true);
    assert.equal(reachable.responsive.content.scroll.changed, true);
    assert.equal(reachable.responsive.content.scroll.startedInViewport, false);
    assert.deepEqual(reachable.responsive.content.scroll.changedContainers.map((item) => item.label), ['document']);
    assert.deepEqual(reachable.responsive.content.clippedBy, []);
    assert.equal(reachable.responsive.content.reachable, true);
    assert.equal(
      reachable.assertions.find((assertion) => assertion.category === 'content-visibility').observed,
      true,
    );

    assert.equal(clipped.responsive.content.scroll.attempted, true);
    assert.equal(clipped.responsive.content.inViewport, true);
    assert.equal(clipped.responsive.content.reachable, false);
    assert.equal(clipped.responsive.content.clippedBy[0].testId, 'clipped-container');
    assert.equal('containers' in clipped.responsive.content.scroll, false);
    assert.equal('clippingAncestors' in clipped.responsive.content, false);
    assert.equal(
      clipped.assertions.find((assertion) => assertion.category === 'content-visibility').observed,
      false,
    );
  } finally {
    await cleanupFixture(runtime, directory, server);
  }
});

test('workflow list scoping and declared focus N/A retain measured card layout and P0 truth', { timeout: 180_000 }, async () => {
    const server = createServer((_request, response) => {
      response.writeHead(200, { connection: 'close', 'content-type': 'text/html; charset=utf-8' });
      response.end(workflowListFixture);
    });
    const port = await listen(server);
    const directory = path.join(HERE, `.responsive-${randomUUID()}`);
    let runtime;
    try {
      runtime = await openBrowserSession({
        baseUrl: `http://127.0.0.1:${port}`, headless: true,
      }, { chromium, environment: TEST_BROWSER_ENVIRONMENT });
      const capture = attachPageCapture(runtime.page);
      const execute = (id, args) => executeUiAction({
        runtime, capture, session: { persona: { text: 'Test persona' } },
        args, eventId: id, transcriptDirectory: directory,
      });
      await runtime.goto('/projects/project-1/workflows?private=redacted');
      const click = await execute(1, {
        _: ['click'], 'within-test-id': 'workflow-card-custom-fan-coordinator-demo',
        role: 'button', name: 'View graph',
      });
      assert.equal(click.target.withinTestId, 'workflow-card-custom-fan-coordinator-demo');
      assert.equal(await runtime.page.locator('[data-testid="workflow-card-other"] button[data-clicked]').count(), 0);
      assert.equal(await runtime.page.locator('[data-testid="workflow-card-custom-fan-coordinator-demo"] button[data-clicked]').count(), 1);
      await assert.rejects(execute(2, {
        _: ['click'], 'within-test-id': 'workflow-card-absent', role: 'button', name: 'View graph',
        timeout: '200',
      }), /Timeout|waiting for/);
      const typed = await execute(8, {
        _: ['type-coordinator'], 'within-test-id': 'workflow-card-custom-fan-coordinator-demo',
        role: 'textbox', name: 'Instructions', text: 'fixture-only',
      });
      assert.equal(typed.target.withinTestId, 'workflow-card-custom-fan-coordinator-demo');
      assert.equal(await runtime.page.locator('[data-testid="workflow-card-other"] input').inputValue(), '');
      assert.equal(await runtime.page.locator('[data-testid="workflow-card-custom-fan-coordinator-demo"] input').inputValue(), 'fixture-only');
      const undeclared = await execute(9, {
        _: ['viewport'], width: '1920', height: '1080',
        'content-test-id': 'workflow-card-custom-fan-coordinator-demo',
      });
      assert.equal(undeclared.assertions.find((entry) => entry.category === 'focus-mode').required, true);
      assert.equal(computeDriverP0([undeclared]).pass, false);

      const steps = [];
      for (const [index, size] of [{ width: '1920', height: '1080' }, { mobile: true }].entries()) {
        const step = await execute(index + 3, {
          _: ['viewport'], ...size, 'focus-mode': 'not-applicable',
          'content-test-id': 'workflow-card-custom-fan-coordinator-demo',
        });
        steps.push(step);
        assert.equal(step.responsive.focusMode.exists, false);
        assert.equal(step.responsive.focusMode.reachable, false);
        assert.equal(step.responsive.focusMode.status, 'not-applicable');
        const focusAssertion = step.assertions.find((entry) => entry.category === 'focus-mode');
        assert.deepEqual([focusAssertion.required, focusAssertion.observed, focusAssertion.status],
          [false, false, 'not-applicable']);
        assert.equal(step.responsive.content.layout.horizontalOverflow, false);
        assert(step.responsive.content.layout.clientWidth > 0);
        assert(step.responsive.content.layout.scrollWidth <= step.responsive.content.layout.clientWidth);
        assert.equal(step.responsive.content.layout.children.length, 2);
        assert(step.responsive.content.layout.children[0].bounds.bottom
          <= step.responsive.content.layout.children[1].bounds.top);
        assert.equal(step.responsive.content.layout.buttons.length, 6);
        assert(step.responsive.content.layout.buttons.every((button) => button.reachable && button.bounds.width > 0));
        assert.equal(step.overflow.horizontal, false);
        assert.equal(computeDriverP0([step]).pass, true);
        assert.equal(JSON.stringify(step).includes('private=redacted'), false);
      }
      assert.deepEqual(steps.map((step) => step.viewport.width), [1920, 390]);
      assert.equal(computeDriverP0([{
        ...steps[0], assertions: steps[0].assertions.map((entry) =>
          entry.category === 'overflow' && entry.target === 'horizontal-overflow-contained'
            ? { ...entry, observed: false } : entry),
      }]).pass, false);
      await runtime.goto('/runs/fixture');
      await assert.rejects(execute(5, {
        _: ['viewport'], width: '1280', height: '720', 'focus-mode': 'not-applicable',
      }), /workflow-list route/);
      await runtime.goto('/projects/project-1/workflows');
      await runtime.page.locator('main').evaluate((main) => {
        const toggle = document.createElement('button');
        toggle.dataset.testid = 'run-focus-toggle';
        main.append(toggle);
      });
      await assert.rejects(execute(6, {
        _: ['viewport'], width: '1280', height: '720', 'focus-mode': 'not-applicable',
      }), /without a run focus control/);
      await assert.rejects(execute(7, {
        _: ['viewport'], width: '1280', height: '720', 'focus-mode': 'not-applicable',
        'focus-test-id': 'missing-toggle',
      }), /without a run focus control/);
    } finally {
      await cleanupFixture(runtime, directory, server);
    }
});

test('root vertical overflow requires a reversible user-scrollable overflow mode', { timeout: 180_000 }, async () => {
  const server = createServer((request, response) => {
    const mode = new URL(request.url, 'http://fixture.test').pathname.slice(1);
    const html = rootOverflowFixture(mode);
    response.writeHead(200, {
      connection: 'close',
      'content-length': Buffer.byteLength(html),
      'content-type': 'text/html; charset=utf-8',
    });
    response.end(html);
  });
  const port = await listen(server);
  const directory = path.join(HERE, `.responsive-${randomUUID()}`);
  let runtime;
  const overflowAssertion = (step) => step.assertions.find(
    (assertion) => assertion.target === 'vertical-overflow-scroll-reachable',
  );

  try {
    runtime = await openBrowserSession({
      baseUrl: `http://127.0.0.1:${port}`,
      headless: true,
    }, { chromium, environment: TEST_BROWSER_ENVIRONMENT });
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
    const results = {};
    for (const [index, mode] of ['auto', 'hidden', 'clip'].entries()) {
      await runtime.goto(`/${mode}`);
      results[mode] = await execute(index + 1, {
        _: ['viewport'], width: '800', height: '480',
      });
    }

    assert.equal(results.auto.overflow.vertical, true);
    assert.equal(results.auto.overflow.verticalScroll.scrollableMode, true);
    assert.equal(results.auto.overflow.verticalScroll.changed, true);
    assert.equal(results.auto.overflow.verticalScroll.restored, true);
    assert.equal(overflowAssertion(results.auto).observed, true);

    for (const mode of ['hidden', 'clip']) {
      assert.equal(results[mode].overflow.vertical, true);
      assert.equal(results[mode].overflow.verticalScroll.overflowY, mode);
      assert.equal(results[mode].overflow.verticalScroll.scrollableMode, false);
      assert.equal(results[mode].overflow.verticalScroll.restored, true);
      assert.equal(overflowAssertion(results[mode]).observed, false);
    }
  } finally {
    await cleanupFixture(runtime, directory, server);
  }
});
