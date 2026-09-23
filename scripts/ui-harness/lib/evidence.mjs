import { createHash } from 'node:crypto';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { redact as redactSecrets } from '../../harness-shared/redaction.mjs';

export function redact(value) {
  return redactSecrets(value);
}

export function evidenceHash(value) {
  const sanitized = Buffer.isBuffer(value) ? value : redact(value);
  return createHash('sha256').update(typeof sanitized === 'string' || Buffer.isBuffer(sanitized)
    ? sanitized
    : JSON.stringify(sanitized)).digest('hex');
}

export async function structuredDomSnapshot(page) {
  return page.evaluate(() => [...document.querySelectorAll('[data-testid], [role], main, nav, h1, button, input, textarea, a')]
    .slice(0, 300).map((element) => ({
      testId: element.getAttribute('data-testid'),
      role: element.getAttribute('role') || element.tagName.toLowerCase(),
      name: element.getAttribute('aria-label') || element.textContent?.trim().slice(0, 500) || null,
      title: element.getAttribute('title'),
      visible: !!(element.offsetWidth || element.offsetHeight || element.getClientRects().length),
    })));
}

export async function viewportEvidence(page, responsiveTargets = null) {
  return page.evaluate((targets) => {
    const elementFacts = (testId) => {
      const element = document.querySelector(`[data-testid="${CSS.escape(testId)}"]`);
      if (!element) return { testId, exists: false, visible: false, inViewport: false, reachable: false };
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      const visible = style.display !== 'none'
        && style.visibility !== 'hidden'
        && Number(style.opacity) !== 0
        && rect.width > 0
        && rect.height > 0;
      const inViewport = visible
        && rect.bottom > 0
        && rect.right > 0
        && rect.top < window.innerHeight
        && rect.left < window.innerWidth;
      return {
        testId,
        exists: true,
        visible,
        inViewport,
        reachable: visible && (inViewport || document.scrollingElement?.scrollHeight > window.innerHeight),
        bounds: {
          x: Math.round(rect.x),
          y: Math.round(rect.y),
          width: Math.round(rect.width),
          height: Math.round(rect.height),
        },
        ariaPressed: element.getAttribute('aria-pressed'),
        ariaLabel: element.getAttribute('aria-label'),
      };
    };

    const root = document.scrollingElement ?? document.documentElement;
    const viewport = {
      width: window.innerWidth,
      height: window.innerHeight,
      deviceScaleFactor: window.devicePixelRatio,
    };
    const overflow = {
      scrollWidth: root.scrollWidth,
      scrollHeight: root.scrollHeight,
      horizontal: root.scrollWidth > viewport.width,
      vertical: root.scrollHeight > viewport.height,
      maximumScrollX: Math.max(0, root.scrollWidth - viewport.width),
      maximumScrollY: Math.max(0, root.scrollHeight - viewport.height),
    };
    if (!targets) return { viewport, overflow, responsive: null, assertions: [] };

    const navigation = elementFacts(targets.navigationTestId);
    const focus = elementFacts(targets.focusTestId);
    const content = elementFacts(targets.contentTestId);
    const focusActive = focus.ariaPressed === 'true' || /^Exit focus mode$/i.test(focus.ariaLabel ?? '');
    const focusObserved = targets.focusMode === 'focused'
      ? focus.reachable && focusActive
      : targets.focusMode === 'standard'
        ? focus.reachable && !focusActive
        : focus.reachable;
    return {
      viewport,
      overflow,
      responsive: {
        navigation,
        focusMode: { ...focus, active: focusActive, expected: targets.focusMode },
        content,
      },
      assertions: [
        {
          category: 'navigation-reachability',
          target: targets.navigationTestId,
          required: true,
          observed: navigation.reachable,
        },
        {
          category: 'overflow',
          target: 'horizontal-overflow-contained',
          required: true,
          observed: !overflow.horizontal,
        },
        {
          category: 'overflow',
          target: 'vertical-overflow-scroll-reachable',
          required: true,
          observed: !overflow.vertical || overflow.maximumScrollY > 0,
        },
        {
          category: 'focus-mode',
          target: `${targets.focusTestId}:${targets.focusMode}`,
          required: true,
          observed: focusObserved,
        },
        {
          category: 'content-visibility',
          target: targets.contentTestId,
          required: true,
          observed: content.reachable,
        },
      ],
    };
  }, responsiveTargets);
}

export function attachPageCapture(page) {
  const console = [];
  const network = [];
  page.on('console', (entry) => console.push({ type: entry.type(), text: entry.text(), at: new Date().toISOString() }));
  page.on('pageerror', (error) => console.push({ type: 'error', text: String(error.message ?? error), at: new Date().toISOString() }));
  page.on('response', (response) => network.push({
    url: response.url(), status: response.status(), method: response.request().method(),
    resourceType: response.request().resourceType(), userFacing: false, at: new Date().toISOString(),
  }));
  return { console, network };
}

export async function captureTurn({
  page,
  capture,
  directory,
  id,
  intent,
  action,
  target,
  outcome = 'succeeded',
  error = null,
  readiness = null,
  frustrationSignals = [],
  responsiveTargets = null,
}) {
  await mkdir(directory, { recursive: true });
  const screenshotPath = path.join(directory, `turn-${id}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  const domSnapshot = await structuredDomSnapshot(page);
  const viewport = await viewportEvidence(page, responsiveTargets);
  // A non-2xx fetch alone is not a UI P0; it must have visibly surfaced to the user.
  const visibleAlert = domSnapshot.some((element) => element.visible && element.role === 'alert');
  const network = capture.network.splice(0).map((entry) => ({
    ...entry,
    userFacing: visibleAlert && entry.status >= 400 && ['fetch', 'xhr'].includes(entry.resourceType),
  }));
  return redact({
    id, at: new Date().toISOString(), intent, action, target, outcome, error, readiness, url: page.url(), domSnapshot,
    viewport: viewport.viewport, overflow: viewport.overflow, responsive: viewport.responsive, assertions: viewport.assertions, screenshotPath,
    screenshotHash: evidenceHash(await readFile(screenshotPath)),
    console: capture.console.splice(0), network, frustrationSignals,
  });
}
