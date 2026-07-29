import test from 'node:test';
import assert from 'node:assert/strict';
import { parseBeatPlan } from '../lib/beats.mjs';
import { buildKeepSegments, summarizeTrim } from '../lib/pacing.mjs';
import { classifyZoom } from '../lib/zoom.mjs';
import { renderCaptureScript } from '../lib/capture-plan.mjs';

class FakeApprovalCard {
  constructor({ text = 'Tool Approval Required', buttonName = 'Allow once', appearAtMs = 0 } = {}) {
    this.text = text;
    this.buttonName = buttonName;
    this.appearAtMs = appearAtMs;
    this.clickedAtMs = [];
    this.dismissed = false;
    this.node = { dataset: {}, textContent: text };
  }

  isVisible(nowMs) {
    return !this.dismissed && nowMs >= this.appearAtMs;
  }
}

class FakeApprovalButtonLocator {
  constructor(card, page) {
    this.card = card;
    this.page = page;
  }

  first() {
    return this;
  }

  async isVisible() {
    return this.card.isVisible(this.page.nowMs);
  }

  async scrollIntoViewIfNeeded() {}

  async boundingBox() {
    return { x: 32, y: 24, width: 140, height: 32 };
  }

  async click() {
    this.card.clickedAtMs.push(this.page.nowMs);
    this.card.dismissed = true;
  }
}

class FakeApprovalCardLocator {
  constructor(card, page) {
    this.card = card;
    this.page = page;
  }

  async isVisible() {
    return this.card.isVisible(this.page.nowMs);
  }

  getByRole(role, { name } = {}) {
    assert.equal(role, 'button');
    assert.match(this.card.buttonName, name);
    return new FakeApprovalButtonLocator(this.card, this.page);
  }

  async evaluate(fn) {
    return fn(this.card.node);
  }
}

class FakeApprovalSourceLocator {
  constructor(cards, page, hasText = null) {
    this.cards = cards;
    this.page = page;
    this.hasText = hasText;
  }

  filteredCards() {
    if (!this.hasText) return this.cards;
    return this.cards.filter((card) => card.text.includes(this.hasText));
  }

  filter({ hasText }) {
    return new FakeApprovalSourceLocator(this.cards, this.page, hasText);
  }

  async count() {
    return this.filteredCards().length;
  }

  nth(index) {
    return new FakeApprovalCardLocator(this.filteredCards()[index], this.page);
  }
}

class FakeCapturePage {
  constructor(locatorCards = {}) {
    this.locatorCards = locatorCards;
    this.nowMs = 0;
    this.currentUrl = 'about:blank';
    this.gotoCalls = [];
    this.mouse = { move: async () => {} };
    this.screencast = {
      start: async () => {},
      stop: async () => {},
      showOverlay: async () => {},
    };
  }

  locator(selector) {
    return new FakeApprovalSourceLocator(this.locatorCards[selector] ?? [], this);
  }

  async waitForTimeout(ms) {
    let remaining = ms;
    while (remaining > 0) {
      const advance = Math.min(remaining, 50);
      this.nowMs += advance;
      remaining -= advance;
      await new Promise((resolve) => setImmediate(resolve));
    }
  }

  async addInitScript() {}

  async setViewportSize() {}

  async goto(url) {
    this.gotoCalls.push(url);
    this.currentUrl = url;
  }

  async evaluate(arg, data) {
    if (typeof arg !== 'function') return undefined;
    return arg(data);
  }

  async waitForFunction(fn) {
    return fn();
  }

  url() {
    return this.currentUrl;
  }
}

async function runCaptureScriptOnPage(plan, page) {
  const src = renderCaptureScript(plan);
  const capture = eval(`(${src})`);
  const previousNow = Date.now;
  const previousWindow = globalThis.window;
  const previousSessionStorage = globalThis.sessionStorage;
  const previousDocument = globalThis.document;
  globalThis.window = {
    sessionStorage: { setItem() {}, removeItem() {} },
    __demoCursorMove() {},
    __demoActivityMark() {},
    __demoZoomFocus() {},
    __demoZoomReset() {},
    __demoCursorClick() {},
    __demoStopActivity() { return []; },
    __demoGetActivityLog() { return []; },
  };
  globalThis.sessionStorage = globalThis.window.sessionStorage;
  globalThis.document = { body: { innerText: 'done' } };
  globalThis.__demoApprovalWatcherNextId = 0;
  Date.now = () => page.nowMs;
  try {
    await capture(page);
    return page;
  } finally {
    Date.now = previousNow;
    if (previousWindow === undefined) {
      delete globalThis.window;
    } else {
      globalThis.window = previousWindow;
    }
    if (previousSessionStorage === undefined) {
      delete globalThis.sessionStorage;
    } else {
      globalThis.sessionStorage = previousSessionStorage;
    }
    if (previousDocument === undefined) {
      delete globalThis.document;
    } else {
      globalThis.document = previousDocument;
    }
    delete globalThis.__demoApprovalWatcherNextId;
  }
}

async function runCaptureScriptWithCards({ plan, locatorCards }) {
  const page = new FakeCapturePage(locatorCards);
  await runCaptureScriptOnPage(plan, page);
  return page;
}

test('parseBeatPlan extracts beats and narration', () => {
  const beats = parseBeatPlan(`
## Beat 2.5 — Ship it

Narration: “Open the preview and inspect the live page.”

**BLOCKED(example-blocker)**
`);
  assert.equal(beats.length, 1);
  assert.equal(beats[0].id, '2.5');
  assert.equal(beats[0].title, 'Ship it');
  assert.equal(beats[0].narrationSource, 'Open the preview and inspect the live page.');
  assert.deepEqual(beats[0].blockers, ['example-blocker']);
});

test('parseBeatPlan handles CRLF line endings and ignores On screen annotations', () => {
  const crlf = '## Beat 3.1 — Schedule\r\n\r\nNarration: "Put the workflow on a schedule."\r\n\r\nOn screen: operate on the real workflow, not a stray copy.\r\n\r\n## Beat 3.2 — Webhook\r\n\r\nNarration: "Trigger it from GitHub."\r\n';
  const beats = parseBeatPlan(crlf);
  assert.equal(beats.length, 2);
  assert.equal(beats[0].narrationSource, 'Put the workflow on a schedule.');
  assert.ok(!beats[0].narrationSource.includes('On screen'), 'On screen annotation must not leak into narration');
  assert.equal(beats[1].narrationSource, 'Trigger it from GitHub.');
});

test('parseBeatPlan extracts optional capture navigation metadata', () => {
  const beats = parseBeatPlan(`
## Beat 1.1 — Create the project

Narration: "Create it."

Start URL: /projects/new
Fresh navigation: true
`);
  assert.equal(beats[0].startUrl, '/projects/new');
  assert.equal(beats[0].freshNavigation, true);
});

test('classifyZoom biases detail-heavy beats closer', () => {
  const previewZoom = classifyZoom({ title: 'Preview the repaired behavior', narrationSource: 'Preview the fix on a narrow tablet.' });
  const createZoom = classifyZoom({ title: 'Create the project', narrationSource: 'Paste the repo and name the project.' });
  assert.equal(previewZoom.semantic, 'detail');
  assert.ok(previewZoom.scale > createZoom.scale);
});

test('buildKeepSegments removes the middle of long inactive gaps', () => {
  const segments = buildKeepSegments({
    durationMs: 30000,
    events: [{ t: 0 }, { t: 2200 }, { t: 25000 }, { t: 30000 }],
    maxStaticMs: 2500,
    retainAfterActivityMs: 900,
    retainBeforeActivityMs: 1200,
  });
  assert.deepEqual(segments, [
    { startMs: 0, endMs: 3100 },
    { startMs: 23800, endMs: 25900 },
    { startMs: 28800, endMs: 30000 },
  ]);
  const summary = summarizeTrim({ durationMs: 30000, segments });
  assert.equal(summary.trimmedDurationMs, 6400);
  assert.equal(summary.removedMs, 23600);
});

test('capture script moves the cursor AFTER the zoom transform settles (recomputed box)', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'click', selector: "page.getByRole('button', { name: 'X' })", scale: 1.45 }],
  });
  // The zoom transform is applied first...
  const zoomIdx = src.indexOf('__demoZoomFocus');
  // ...then the element box is recomputed post-transform...
  const recomputeIdx = src.indexOf('const zbox = (await locator.boundingBox())');
  // ...and only then is the cursor pointed at the post-transform center.
  const pointIdx = src.indexOf('const pointAt');
  assert.ok(zoomIdx > 0, 'expected a zoom-focus call');
  assert.ok(recomputeIdx > 0, 'expected the post-transform bounding box recompute');
  assert.ok(pointIdx > 0, 'expected a pointAt cursor helper');
  // Regression guard: the old bug placed the cursor in the SAME evaluate() as the zoom,
  // using pre-transform coordinates. That coupling must be gone.
  assert.ok(
    !/__demoZoomFocus\?\.\(x, y, scale\); window\.__demoCursorMove\?\.\(x, y\)/.test(src),
    'cursor must not be moved to pre-zoom coordinates in the same call as the zoom',
  );
});

test('capture script treats scale <= 1.02 as no-zoom (resets transform, no pan)', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'click', selector: "page.getByRole('button', { name: 'Y' })", scale: 1 }],
  });
  assert.ok(src.includes('const zoom = scale > 1.02'), 'expected a no-zoom threshold branch');
  assert.ok(src.includes('__demoZoomReset'), 'expected the no-zoom path to reset any prior transform');
});

test('capture script re-installs the overlay bootstrap after every in-plan goto', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'goto', url: 'https://x/z', after: 500 }],
  });
  // After a full-page navigation the addInitScript bootstrap runs at document-start with
  // a null document.body and bails, so the cursor/zoom/activity tracking must be
  // re-installed explicitly once the new document is ready.
  const gotoIdx = src.indexOf("await page.goto(\"https://x/z\"");
  const reinstallIdx = src.indexOf('await page.evaluate(installSource);', gotoIdx);
  const markIdx = src.indexOf("__demoActivityMark?.('goto')");
  assert.ok(gotoIdx > 0, 'expected the goto navigation');
  assert.ok(reinstallIdx > gotoIdx, 'expected installSource to be re-evaluated after the goto');
  assert.ok(markIdx > reinstallIdx, 'expected the goto activity mark after re-install');
});

test('capture script continues same-page beats unless a fresh navigation is requested', async () => {
  const page = new FakeCapturePage();
  await runCaptureScriptOnPage({
    startUrl: 'https://x/y',
    videoPath: 'beat-1.webm',
    steps: [],
  }, page);
  await runCaptureScriptOnPage({
    startUrl: 'https://x/y',
    videoPath: 'beat-2.webm',
    steps: [],
  }, page);
  await runCaptureScriptOnPage({
    startUrl: 'https://x/y',
    freshNavigation: true,
    videoPath: 'beat-3.webm',
    steps: [],
  }, page);
  assert.deepEqual(page.gotoCalls, ['https://x/y', 'https://x/y']);
});

test('capture script still navigates when a later beat targets a different startUrl', async () => {
  const page = new FakeCapturePage();
  await runCaptureScriptOnPage({
    startUrl: 'https://x/y',
    videoPath: 'beat-1.webm',
    steps: [],
  }, page);
  await runCaptureScriptOnPage({
    startUrl: 'https://x/z',
    videoPath: 'beat-2.webm',
    steps: [],
  }, page);
  assert.deepEqual(page.gotoCalls, ['https://x/y', 'https://x/z']);
});

test('capture script runs and stops the approval watcher around the step loop', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'waitText', text: 'done' }],
  });
  const screencastIdx = src.indexOf('await page.screencast.start');
  const watcherStartIdx = src.indexOf('const approvalWatcher = approvalWatcherEnabled ? (async () => {');
  const tryIdx = src.indexOf('  try {', watcherStartIdx);
  const clickIdx = src.indexOf("await click(approvalButton, 1.02, 700, true);");
  const watcherStopIdx = src.indexOf('await approvalWatcher.catch(() => {});');
  const screencastStopIdx = src.indexOf('await page.screencast.stop().catch(() => {});');
  assert.ok(screencastIdx > 0, 'expected screencast startup');
  assert.ok(watcherStartIdx > screencastIdx, 'expected approval watcher after screencast startup');
  assert.ok(tryIdx > watcherStartIdx, 'expected approval watcher before the step loop try block');
  assert.ok(clickIdx > watcherStartIdx, 'expected the approval watcher to auto-click through the shared helper');
  assert.ok(watcherStopIdx > tryIdx, 'expected the approval watcher to be awaited in finally');
  assert.ok(screencastStopIdx > watcherStopIdx, 'expected approval watcher shutdown before screencast stop');
});

test('capture script watches approval testid gates without a heading-text filter', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'waitText', text: 'done' }],
  });
  assert.ok(src.includes('page.locator(\'[data-testid="session-approval-gate"]\')'), 'expected the session approval gate locator');
  assert.ok(src.includes('page.locator(\'[data-testid="assistant-approval-gate"]\')'), 'expected the assistant approval gate locator');
  assert.ok(src.includes('page.locator(\'[data-testid="shell-approval-gate"]\')'), 'expected the shell approval gate locator');
  assert.ok(src.includes('page.locator(\'[role="alert"]\').filter({ hasText: \'Tool Approval Required\' })'), 'expected the lifecycle alert branch to stay scoped by heading text');
  assert.ok(
    !src.includes('.locator(\'[data-testid="session-approval-gate"], [data-testid="assistant-approval-gate"], [data-testid="shell-approval-gate"], [role="alert"]\')\n      .filter({ hasText: \'Tool Approval Required\' })'),
    'expected testid-scoped approval gates to no longer require the Tool Approval Required heading',
  );
});

test('capture script auto-clicks shell approval cards after the grace period', async () => {
  const card = new FakeApprovalCard({ text: 'Command approval required', appearAtMs: 0 });
  await runCaptureScriptWithCards({
    plan: {
      startUrl: 'https://x/y',
      videoPath: 'a.webm',
      approvalWatcherGraceMs: 1000,
      steps: [{ type: 'pause', ms: 2800 }],
    },
    locatorCards: {
      '[data-testid="session-approval-gate"]': [card],
    },
  });
  assert.equal(card.clickedAtMs.length, 1, 'expected the shell approval card to be auto-clicked');
  assert.ok(card.clickedAtMs[0] >= 1000, 'expected auto-click after the configured grace period');
});

test('capture script auto-clicks timeline shell approval cards after the grace period', async () => {
  const card = new FakeApprovalCard({
    text: 'Dangerous command — approval required',
    buttonName: 'Approve',
    appearAtMs: 0,
  });
  await runCaptureScriptWithCards({
    plan: {
      startUrl: 'https://x/y',
      videoPath: 'a.webm',
      approvalWatcherGraceMs: 1000,
      steps: [{ type: 'pause', ms: 2800 }],
    },
    locatorCards: {
      '[data-testid="shell-approval-gate"]': [card],
    },
  });
  assert.equal(card.clickedAtMs.length, 1, 'expected the timeline shell approval card to be auto-clicked');
  assert.ok(card.clickedAtMs[0] >= 1000, 'expected timeline shell auto-click after the configured grace period');
});

test('capture script tracks concurrent approval cards with independent grace timers', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [{ type: 'waitText', text: 'done' }],
  });
  assert.ok(src.includes('const approvalWatcherFirstSeen = new Map();'), 'expected per-card first-seen tracking');
  assert.ok(src.includes('const getApprovalCardKey = async (card) => card.evaluate((node) => {'), 'expected stable per-card key generation');
  assert.ok(src.includes('node.dataset.demoApprovalWatcherId = `demo-approval-${nextId}`;'), 'expected DOM-stamped watcher ids for concurrent cards');
  assert.ok(src.includes('const visibleApprovalCards = await collectVisibleApprovalCards();'), 'expected each poll tick to collect all visible approval cards');
  assert.ok(src.includes('const keyedApprovalCards = [];'), 'expected visible cards to be keyed before any clicks happen');
  assert.ok(src.includes('for (const card of visibleApprovalCards) {'), 'expected each visible card to be processed independently');
  assert.ok(src.includes('keyedApprovalCards.push({ card, key });'), 'expected newly-seen cards to keep their original first-seen timestamp for this poll');
  assert.ok(src.includes('for (const { card, key } of keyedApprovalCards) {'), 'expected overdue checks to run after first-seen timestamps are assigned');
  assert.ok(src.includes('if (!approvalWatcherFirstSeen.has(key)) {'), 'expected new cards to start their own grace timer');
  assert.ok(src.includes('if (Date.now() - approvalWatcherFirstSeen.get(key) < approvalWatcherGraceMs) continue;'), 'expected grace timing to be evaluated per card');
  assert.ok(src.includes('approvalWatcherFirstSeen.set(key, Date.now());'), 'expected clicked cards to reset their retry timer independently');
  assert.ok(src.includes('if (!visibleKeys.has(key)) approvalWatcherFirstSeen.delete(key);'), 'expected stale per-card timers to be cleaned up');
});

test('capture script auto-clicks concurrent approval cards from their own appearance times', async () => {
  const firstCard = new FakeApprovalCard({ text: 'Tool Approval Required', appearAtMs: 0 });
  const secondCard = new FakeApprovalCard({ text: 'Command approval required', appearAtMs: 700 });
  await runCaptureScriptWithCards({
    plan: {
      startUrl: 'https://x/y',
      videoPath: 'a.webm',
      approvalWatcherGraceMs: 1000,
      steps: [{ type: 'pause', ms: 5200 }],
    },
    locatorCards: {
      '[data-testid="session-approval-gate"]': [firstCard, secondCard],
    },
  });
  assert.equal(firstCard.clickedAtMs.length, 1, 'expected the first approval card to be clicked once');
  assert.equal(secondCard.clickedAtMs.length, 1, 'expected the second approval card to be clicked once');
  assert.ok(firstCard.clickedAtMs[0] >= 1000, 'expected the first card to honor its own grace period');
  assert.ok(secondCard.clickedAtMs[0] >= 1700, 'expected the second card to honor its later appearance time');
  assert.ok(secondCard.clickedAtMs[0] > firstCard.clickedAtMs[0], 'expected the later-appearing card to be clicked after the first card');
});

test('capture script supports eval, waitFor, forced clicks and selector-scoped press', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y',
    videoPath: 'a.webm',
    steps: [
      { type: 'eval', expression: "document.querySelector('.dup')?.remove();" },
      { type: 'waitFor', selector: "page.getByTestId('dashboard-chart')", timeout: 45000 },
      { type: 'click', selector: "page.getByRole('button', { name: 'Approve' })", scale: 1, force: true },
      { type: 'press', selector: "page.getByRole('textbox')", key: 'Enter', after: 300 },
    ],
  });
  // eval runs the in-page expression (used e.g. to remove a duplicate list item before capture)
  assert.ok(src.includes("document.querySelector('.dup')?.remove();"), 'expected the eval expression to be emitted');
  assert.ok(src.includes("__demoActivityMark?.('eval')"), 'expected an eval activity mark');
  assert.ok(src.includes('await page.evaluate(async () =>'), 'expected eval to be wrapped in an async evaluate so snippets may await');
  // waitFor waits on a real element becoming visible (replaces fixed short timeouts)
  assert.ok(src.includes(".waitFor({ state: 'visible', timeout: 45000 })"), 'expected a visible waitFor with the given timeout');
  // forced clicks pass { force: true } through to locator.click
  assert.ok(src.includes('force ? { force: true } : {}'), 'expected the click helper to honor force');
  assert.ok(/await click\(.*'Approve'.*, true\);/s.test(src), 'expected the Approve click to be forced');
  // press can be scoped to a selector instead of the global keyboard
  assert.ok(src.includes(".press(\"Enter\")"), 'expected a selector-scoped press');
});

test('unknown step types are still emitted as nothing (no throw)', () => {
  assert.doesNotThrow(() => renderCaptureScript({
    startUrl: 'https://x/y', videoPath: 'a.webm', steps: [{ type: 'badge', label: 'L', title: 'T' }],
  }));
});

test('eval accepts a code alias with top-level await', () => {
  const src = renderCaptureScript({
    startUrl: 'https://x/y', videoPath: 'a.webm',
    steps: [{ type: 'eval', code: 'const r = await fetch("/api/x"); await r.json();' }],
  });
  assert.ok(src.includes('await page.evaluate(async () =>'), 'expected async eval wrapper');
  assert.ok(src.includes('const r = await fetch("/api/x");'), 'expected the code snippet to be emitted');
});

test('activity tracking + capture clears persist across navigations via sessionStorage', () => {
  const src = renderCaptureScript({ startUrl: 'https://x/y', videoPath: 'a.webm', steps: [] });
  // stale per-capture state is cleared once at capture start...
  assert.ok(src.includes("sessionStorage.removeItem('__demoCaptureEpoch')"), 'expected epoch reset at capture start');
  assert.ok(src.includes("sessionStorage.removeItem('__demoActivityLog')"), 'expected activity-log reset at capture start');
  // ...and the bootstrap persists the log in sessionStorage so it survives page.goto.
  assert.ok(src.includes('__demoActivityLog'), 'expected the activity log to be persisted in sessionStorage');
  assert.ok(src.includes('__demoCaptureEpoch'), 'expected a wall-clock capture epoch');
});
