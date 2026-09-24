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
  return page.evaluate(async (targets) => {
    const root = document.scrollingElement ?? document.documentElement;
    const overflowScrolls = (value) => ['auto', 'overlay', 'scroll'].includes(value);
    const overflowClips = (value) => ['auto', 'clip', 'hidden', 'overlay', 'scroll'].includes(value);
    const waitForScroll = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const roundedPosition = (x, y) => ({ x: Math.round(x), y: Math.round(y) });
    const roundedRect = (rect) => ({
      x: Math.round(rect.x ?? rect.left),
      y: Math.round(rect.y ?? rect.top),
      width: Math.round(rect.width ?? rect.right - rect.left),
      height: Math.round(rect.height ?? rect.bottom - rect.top),
      top: Math.round(rect.top),
      right: Math.round(rect.right),
      bottom: Math.round(rect.bottom),
      left: Math.round(rect.left),
    });
    const intersects = (first, second) => first.bottom > second.top
      && first.right > second.left
      && first.top < second.bottom
      && first.left < second.right;

    const elementFacts = async (testId) => {
      const element = document.querySelector(`[data-testid="${CSS.escape(testId)}"]`);
      if (!element) {
        return {
          testId,
          exists: false,
          visible: false,
          inViewport: false,
          reachable: false,
          scroll: { attempted: false, changed: false, restored: true },
          clippedBy: [],
        };
      }

      const ancestors = [];
      for (let ancestor = element.parentElement; ancestor; ancestor = ancestor.parentElement) {
        if (ancestor !== root && ancestor !== document.documentElement && ancestor !== document.body) {
          ancestors.push(ancestor);
        }
      }
      const label = (ancestor) => ancestor.getAttribute('data-testid')
        ?? ancestor.id
        ?? ancestor.tagName.toLowerCase();
      const ancestorState = ancestors.map((ancestor) => {
        const style = getComputedStyle(ancestor);
        return {
          element: ancestor,
          testId: ancestor.getAttribute('data-testid'),
          label: label(ancestor),
          overflowX: style.overflowX,
          overflowY: style.overflowY,
          scrollableX: overflowScrolls(style.overflowX) && ancestor.scrollWidth > ancestor.clientWidth,
          scrollableY: overflowScrolls(style.overflowY) && ancestor.scrollHeight > ancestor.clientHeight,
          scrollLeft: ancestor.scrollLeft,
          scrollTop: ancestor.scrollTop,
        };
      });
      const rootScroll = { x: window.scrollX, y: window.scrollY };

      const inspect = () => {
        const rect = element.getBoundingClientRect();
        const viewportRect = {
          top: 0,
          left: 0,
          right: window.innerWidth,
          bottom: window.innerHeight,
        };
        const visible = [element, ...ancestors].every((candidate) => {
          const style = getComputedStyle(candidate);
          return style.display !== 'none'
            && style.visibility !== 'hidden'
            && style.visibility !== 'collapse'
            && Number(style.opacity) !== 0;
        }) && rect.width > 0 && rect.height > 0;
        const clippedBy = ancestorState.flatMap((state) => {
          const clipsX = overflowClips(state.overflowX);
          const clipsY = overflowClips(state.overflowY);
          if (!clipsX && !clipsY) return [];
          const ancestorRect = state.element.getBoundingClientRect();
          const clipRect = {
            top: ancestorRect.top + state.element.clientTop,
            left: ancestorRect.left + state.element.clientLeft,
            right: ancestorRect.left + state.element.clientLeft + state.element.clientWidth,
            bottom: ancestorRect.top + state.element.clientTop + state.element.clientHeight,
          };
          const clippedX = clipsX && (rect.left < clipRect.left || rect.right > clipRect.right);
          const clippedY = clipsY && (rect.top < clipRect.top || rect.bottom > clipRect.bottom);
          if (!clippedX && !clippedY) return [];
          return [{
            testId: state.testId,
            label: state.label,
            overflowX: state.overflowX,
            overflowY: state.overflowY,
            bounds: roundedRect(clipRect),
          }];
        });
        const intersectsViewport = intersects(rect, viewportRect);
        return {
          bounds: roundedRect(rect),
          visible,
          intersectsViewport,
          clippedBy,
        };
      };

      const before = inspect();
      element.scrollIntoView({ behavior: 'instant', block: 'nearest', inline: 'nearest' });
      for (const state of ancestorState) {
        if (!state.scrollableX) state.element.scrollLeft = state.scrollLeft;
        if (!state.scrollableY) state.element.scrollTop = state.scrollTop;
      }
      await waitForScroll();
      const after = inspect();
      const rootAfter = { x: window.scrollX, y: window.scrollY };
      const changedContainers = [];
      if (rootAfter.x !== rootScroll.x || rootAfter.y !== rootScroll.y) {
        const style = getComputedStyle(root);
        changedContainers.push({
          testId: root.getAttribute('data-testid'),
          label: 'document',
          overflowX: style.overflowX,
          overflowY: style.overflowY,
          userScrollable: (rootAfter.x === rootScroll.x || overflowScrolls(style.overflowX))
            && (rootAfter.y === rootScroll.y || overflowScrolls(style.overflowY)),
          before: roundedPosition(rootScroll.x, rootScroll.y),
          after: roundedPosition(rootAfter.x, rootAfter.y),
        });
      }
      for (const state of ancestorState) {
        const afterX = state.element.scrollLeft;
        const afterY = state.element.scrollTop;
        if (afterX === state.scrollLeft && afterY === state.scrollTop) continue;
        changedContainers.push({
          testId: state.testId,
          label: state.label,
          overflowX: state.overflowX,
          overflowY: state.overflowY,
          userScrollable: (afterX === state.scrollLeft || state.scrollableX)
            && (afterY === state.scrollTop || state.scrollableY),
          before: roundedPosition(state.scrollLeft, state.scrollTop),
          after: roundedPosition(afterX, afterY),
        });
      }

      for (const state of ancestorState) {
        state.element.scrollLeft = state.scrollLeft;
        state.element.scrollTop = state.scrollTop;
      }
      window.scrollTo(rootScroll.x, rootScroll.y);
      await waitForScroll();
      const restored = window.scrollX === rootScroll.x
        && window.scrollY === rootScroll.y
        && ancestorState.every((state) => state.element.scrollLeft === state.scrollLeft
          && state.element.scrollTop === state.scrollTop);

      const reachable = after.visible
        && after.intersectsViewport
        && after.clippedBy.length === 0
        && changedContainers.every((container) => container.userScrollable);
      return {
        testId,
        exists: true,
        visible: after.visible,
        inViewport: after.intersectsViewport,
        reachable,
        bounds: after.bounds,
        scroll: {
          attempted: true,
          changed: changedContainers.length > 0,
          restored,
          startedInViewport: before.intersectsViewport,
          changedContainers,
        },
        clippedBy: after.clippedBy,
        ariaPressed: element.getAttribute('aria-pressed'),
        ariaLabel: element.getAttribute('aria-label'),
      };
    };

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
    const rootStyle = getComputedStyle(root);
    const rootBefore = { x: window.scrollX, y: window.scrollY };
    const attemptedRootY = overflow.maximumScrollY === 0
      ? rootBefore.y
      : rootBefore.y < overflow.maximumScrollY
        ? Math.min(overflow.maximumScrollY, rootBefore.y + 40)
        : Math.max(0, rootBefore.y - 40);
    window.scrollTo(rootBefore.x, attemptedRootY);
    await waitForScroll();
    const rootAfter = { x: window.scrollX, y: window.scrollY };
    window.scrollTo(rootBefore.x, rootBefore.y);
    await waitForScroll();
    const rootRestored = { x: window.scrollX, y: window.scrollY };
    overflow.verticalScroll = {
      overflowY: rootStyle.overflowY,
      scrollableMode: overflowScrolls(rootStyle.overflowY),
      attempted: overflow.vertical,
      changed: rootAfter.y !== rootBefore.y,
      restored: rootRestored.x === rootBefore.x && rootRestored.y === rootBefore.y,
      before: roundedPosition(rootBefore.x, rootBefore.y),
      after: roundedPosition(rootAfter.x, rootAfter.y),
    };
    if (!targets) return { viewport, overflow, responsive: null, assertions: [] };

    const navigation = await elementFacts(targets.navigationTestId);
    const focus = await elementFacts(targets.focusTestId);
    const content = await elementFacts(targets.contentTestId);
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
          observed: !overflow.vertical || (
            overflow.verticalScroll.scrollableMode
            && overflow.verticalScroll.changed
            && overflow.verticalScroll.restored
          ),
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
