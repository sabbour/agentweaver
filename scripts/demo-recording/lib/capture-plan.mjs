import { browserZoomBootstrapSource } from './zoom.mjs';
import { browserDomCueBootstrapSource } from './dom-cues.mjs';
import { browserBugFixPullRequestResolverSource } from './bugfix-pr.mjs';

// buildInstallSource used to be a hand-duplicated copy of zoom.mjs's bootstrap
// (with the same '#root'-only transform bug). Now delegates to the single
// canonical implementation in zoom.mjs so the zoom/cursor/activity-tracking
// bootstrap only exists in one place and any future fix (like the body- vs
// root-transform fix) can't silently drift out of sync between the two files.
function buildInstallSource() {
  return `${browserZoomBootstrapSource()}\n${browserDomCueBootstrapSource()}`;
}

function locatorExpression(selector) {
  return `(${selector})`;
}

export function renderCaptureScript(plan) {
  const installSource = JSON.stringify(buildInstallSource());
  const lines = [
    'async page => {',
    '  const pause = ms => page.waitForTimeout(ms);',
    '  const cueLog = [];',
    `  const beatId = ${JSON.stringify(plan.beatId ?? null)};`,
    `  const passiveCueWatchers = ${JSON.stringify(plan.cueWatchers ?? [])};`,
    '  page.__demoCueSink = cueLog;',
    '  if (!page.__demoCueBindingInstalled && typeof page.exposeBinding === \'function\') {',
    "    await page.exposeBinding('__demoReportCue', (_source, cue) => {",
    '      const sink = page.__demoCueSink;',
    '      if (!Array.isArray(sink)) return;',
    '      if (sink.some((existing) => existing.name === cue.name)) return;',
    '      const captureStartedAtEpochMs = page.__demoCaptureStartedAtEpochMs ?? Date.now();',
    '      sink.push({',
    '        ...cue,',
    '        beatId: cue.beatId ?? page.__demoCaptureBeatId ?? null,',
    '        sequence: sink.length,',
    '        tMs: Math.max(0, Date.now() - captureStartedAtEpochMs),',
    '        receivedAtEpochMs: Date.now(),',
    '      });',
    '    });',
    '    page.__demoCueBindingInstalled = true;',
    '  }',
    `  const installSource = ${installSource};`,
    '  await page.addInitScript(installSource);',
    `  await page.setViewportSize({ width: ${plan.viewport?.width ?? 1920}, height: ${plan.viewport?.height ?? 1080} });`,
  ];

  if (plan.auth?.entries) {
    lines.push(
      `  await page.addInitScript((entries) => { for (const [key, value] of Object.entries(entries)) window.sessionStorage.setItem(key, value); }, ${JSON.stringify(plan.auth.entries)});`,
    );
  }

  lines.push(
    `  const planStartUrl = ${JSON.stringify(plan.startUrl)};`,
    `  const freshNavigation = ${plan.freshNavigation ? 'true' : 'false'};`,
    '  const shouldNavigate = (() => {',
    "    const currentUrl = page.url?.() ?? '';",
    "    if (!planStartUrl) return false;",
    "    if (freshNavigation || !currentUrl || currentUrl === 'about:blank') return true;",
    '    try {',
    '      return new URL(currentUrl).href !== new URL(planStartUrl).href;',
    '    } catch (e) {',
    '      return currentUrl !== planStartUrl;',
    '    }',
    '  })();',
    '  if (shouldNavigate) {',
    "    await page.goto(planStartUrl, { waitUntil: 'domcontentloaded' });",
    '  }',
    "  await page.evaluate(() => { try { sessionStorage.removeItem('__demoCaptureEpoch'); sessionStorage.removeItem('__demoActivityLog'); } catch (e) {} });",
    '  await page.evaluate(installSource);',
    "  await page.evaluate(() => { try { window.__demoZoomReset?.(); } catch (e) {} });",
    '  page.__demoCaptureBeatId = beatId;',
    `  await page.screencast.start({ path: ${JSON.stringify(plan.videoPath)}, size: { width: ${plan.viewport?.width ?? 1920}, height: ${plan.viewport?.height ?? 1080} } });`,
    '  page.__demoCaptureStartedAtEpochMs = Date.now();',
    '  await page.evaluate((watchers) => window.__demoConfigureDomCueWatchers?.(watchers), passiveCueWatchers);',
    '  const centerOf = (box) => ({',
    '    x: box.x + Math.max(8, Math.min(box.width / 2, box.width - 8)),',
    '    y: box.y + Math.max(8, Math.min(box.height / 2, box.height - 8)),',
    '  });',
    '  // Drive the visible cursor + real mouse to a viewport point. Kept as one helper',
    '  // so every interaction points the cursor at exactly where the click will land.',
    '  const pointAt = async (x, y, steps = 18) => {',
    '    await page.evaluate(({ x, y }) => { window.__demoCursorMove?.(x, y); }, { x: Math.round(x), y: Math.round(y) });',
    '    await page.mouse.move(x, y, { steps });',
    '  };',
    '  // focus() optionally zooms toward the target. CRITICAL: a zoom transform MOVES the',
    "  // element on screen, so the cursor must be placed using the element's POST-transform",
    '  // box (recomputed after the transition settles), not the pre-zoom coordinates — that',
    '  // pre-zoom placement was why "the pointer was nowhere near where the clicks are".',
    '  // A scale <= 1.02 means "no zoom": reset any prior transform and just point, so we',
    '  // stop panning the whole page for beats where nothing needs magnifying.',
    '  const focus = async (locator, scale = 1.45, steps = 18, hold = 260) => {',
    '    await locator.scrollIntoViewIfNeeded().catch(() => {});',
    '    const box = await locator.boundingBox();',
    "    if (!box) throw new Error('No bounding box');",
    '    const pre = centerOf(box);',
    '    const zoom = false;',
    '    if (zoom) {',
    "      await page.evaluate(({ x, y, scale }) => { window.__demoActivityMark?.('focus', { x: Math.round(x), y: Math.round(y), scale }); window.__demoZoomFocus?.(x, y, scale); }, { x: pre.x, y: pre.y, scale });",
    '      await pause(500);',
    '    } else {',
    "      await page.evaluate(() => { window.__demoActivityMark?.('focus'); window.__demoZoomReset?.(); });",
    '      await pause(300);',
    '    }',
    '    const zbox = (await locator.boundingBox()) ?? box;',
    '    const post = centerOf(zbox);',
    '    await pointAt(post.x, post.y, steps);',
    '    await pause(hold);',
    '  };',
    '  const click = async (locator, scale = 1.45, after = 620, force = false) => {',
    '    await focus(locator, scale);',
    '    await locator.click(force ? { force: true } : {});',
    "    await page.evaluate(() => { window.__demoActivityMark?.('click'); window.__demoCursorClick?.(); });",
    '    await pause(after);',
    '  };',
    '  const typeInto = async (locator, text, scale = 1.6, delay = 12, after = 700) => {',
    '    await click(locator, scale, 280);',
    "    await locator.fill('');",
    '    await locator.pressSequentially(text, { delay });',
    '    await pause(after);',
    '  };',
    '  const showBadge = async (label, title, duration = 1800) => page.screencast.showOverlay(`<div style="position:fixed;top:16px;left:16px;z-index:2147483644;padding:8px 12px;background:rgba(15,23,42,.92);color:white;border-radius:999px;font:600 13px/1.2 Segoe UI,Arial,sans-serif;box-shadow:0 10px 24px rgba(15,23,42,.28)"><span style="opacity:.78">${label}</span><span style="margin:0 6px;opacity:.45">•</span><span>${title}</span></div>`, { duration });',
    `  const approvalWatcherEnabled = ${plan.disableApprovalWatcher ? 'false' : 'true'};`,
    `  const approvalWatcherGraceMs = ${plan.approvalWatcherGraceMs ?? 2250};`,
    '  let watcherActive = approvalWatcherEnabled;',
    '  // Safety net for ToolApprovalRequired cards: existing beats still script the',
    '  // intentional preview/merge approvals explicitly, so wait briefly before clicking',
    '  // to preserve the on-camera callout, then auto-approve only if the gate is still',
    '  // pending. disableApprovalWatcher lets a future plan intentionally hold longer.',
    '  const approvalWatcher = approvalWatcherEnabled ? (async () => {',
    '    const approvalWatcherFirstSeen = new Map();',
    '    const approvalCardSources = [',
    '      page.locator(\'[data-testid="session-approval-gate"]\'),',
    '      page.locator(\'[data-testid="assistant-approval-gate"]\'),',
    '      page.locator(\'[data-testid="shell-approval-gate"]\'),',
    '      page.locator(\'[role="alert"]\').filter({ hasText: \'Tool Approval Required\' }),',
    '    ];',
    '    const getApprovalCardKey = async (card) => card.evaluate((node) => {',
    '      if (!node.dataset.demoApprovalWatcherId) {',
    '        const nextId = (globalThis.__demoApprovalWatcherNextId ?? 0) + 1;',
    '        globalThis.__demoApprovalWatcherNextId = nextId;',
    '        node.dataset.demoApprovalWatcherId = `demo-approval-${nextId}`;',
    '      }',
    '      return node.dataset.demoApprovalWatcherId;',
    '    }).catch(() => null);',
    '    const collectVisibleApprovalCards = async () => {',
    '      const cards = [];',
    '      for (const source of approvalCardSources) {',
    '        const count = await source.count().catch(() => 0);',
    '        for (let index = 0; index < count; index += 1) {',
    '          const card = source.nth(index);',
    '          if (await card.isVisible().catch(() => false)) cards.push(card);',
    '        }',
    '      }',
    '      return cards;',
    '    };',
    '    while (watcherActive) {',
    '      try {',
    '        const visibleApprovalCards = await collectVisibleApprovalCards();',
    '        const visibleKeys = new Set();',
    '        const keyedApprovalCards = [];',
    '        for (const card of visibleApprovalCards) {',
    '          const key = await getApprovalCardKey(card);',
    '          if (!key) continue;',
    '          visibleKeys.add(key);',
    '          if (!approvalWatcherFirstSeen.has(key)) {',
    '            approvalWatcherFirstSeen.set(key, Date.now());',
    '          }',
    '          keyedApprovalCards.push({ card, key });',
    '        }',
    '        for (const { card, key } of keyedApprovalCards) {',
    '          if (Date.now() - approvalWatcherFirstSeen.get(key) < approvalWatcherGraceMs) continue;',
    '          const approvalButton = card.getByRole(\'button\', { name: /^(Allow once|Approve)$/ }).first();',
    '          if (await approvalButton.isVisible().catch(() => false)) {',
    '            await click(approvalButton, 1.02, 700, true);',
    '            approvalWatcherFirstSeen.set(key, Date.now());',
    '          }',
    '        }',
    '        for (const key of approvalWatcherFirstSeen.keys()) {',
    '          if (!visibleKeys.has(key)) approvalWatcherFirstSeen.delete(key);',
    '        }',
    '      } catch (e) {}',
    '      await pause(500);',
    '    }',
    '  })() : Promise.resolve();',
    '  try {',
  );

  for (const step of plan.steps) {
    if (step.type === 'badge') {
      lines.push(`    await showBadge(${JSON.stringify(step.label)}, ${JSON.stringify(step.title)}, ${step.duration ?? 1800});`);
    } else if (step.type === 'pause') {
      lines.push(`    await pause(${step.ms});`);
    } else if (step.type === 'click') {
      lines.push(`    await click(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, ${step.after ?? 620}, ${step.force ? 'true' : 'false'});`);
    } else if (step.type === 'hover') {
      lines.push(`    await focus(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, 18, ${step.hold ?? 900});`);
    } else if (step.type === 'drag') {
      const source = locatorExpression(step.selector);
      const target = locatorExpression(step.target);
      lines.push(`    await focus(${source}, ${step.scale ?? 1.35});`);
      lines.push(`    await ${target}.scrollIntoViewIfNeeded();`);
      lines.push(`    await ${source}.dragTo(${target});`);
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('drag'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'type') {
      lines.push(`    await typeInto(${locatorExpression(step.selector)}, ${JSON.stringify(step.text)}, ${step.scale ?? 1.6}, ${step.delay ?? 12}, ${step.after ?? 700});`);
    } else if (step.type === 'press') {
      const pressTarget = step.selector ? `${locatorExpression(step.selector)}.press(${JSON.stringify(step.key)})` : `page.keyboard.press(${JSON.stringify(step.key)})`;
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('press', { key: ${JSON.stringify(step.key)} }));`);
      lines.push(`    await ${pressTarget};`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'eval') {
      // Run an arbitrary in-page snippet (e.g. a guarded find-or-create that ensures a
      // single backlog item exists and is promoted, instead of a duplicate create+delete
      // on camera). Wrapped in an async IIFE so the snippet may use await (fetch the
      // board API, etc.). Accepts `expression` or `code`. Marked so the idle-trimmer keeps
      // the surrounding frames. The snippet is authored in-repo, never from user input.
      const evalBody = step.expression ?? step.code ?? '';
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('eval'));`);
      lines.push(`    await page.evaluate(async () => { ${evalBody} });`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'resolveBugFixPullRequest') {
      lines.push(`    { const evidence = ${JSON.stringify({
        runUrl: step.runUrl,
        projectUrl: step.projectUrl,
        expectedPullRequestUrl: step.expectedPullRequestUrl,
      })};`);
      lines.push(`      const resolveEvidenceSource = ${JSON.stringify(browserBugFixPullRequestResolverSource())};`);
      lines.push(`      const resolved = await page.evaluate(async ({ evidence, resolveEvidenceSource }) => {`);
      lines.push(`        const resolve = (0, eval)(resolveEvidenceSource);`);
      lines.push(`        const run = new URL(evidence.runUrl);`);
      lines.push(`        if (location.href !== run.href) throw new Error('Bug Fix pull-request resolution failed: the active page is not the configured current Bug Fix run.');`);
      lines.push(`        const runId = run.pathname.split('/').filter(Boolean).at(-1);`);
      lines.push(`        const request = async (path) => { const response = await fetch(path, { credentials: 'same-origin' }); if (!response.ok) throw new Error(\`Bug Fix pull-request resolution failed: unable to load \${path} (\${response.status}).\`); return response.json(); };`);
      lines.push(`        const [topology, events] = await Promise.all([request(\`/api/runs/\${encodeURIComponent(runId)}/graph\`), request(\`/api/runs/\${encodeURIComponent(runId)}/events\`)]);`);
      lines.push(`        const projectId = new URL(evidence.projectUrl).pathname.split('/').filter(Boolean).at(-1);`);
      lines.push(`        const project = await request(\`/api/projects/\${encodeURIComponent(projectId)}\`);`);
      lines.push(`        return resolve({ ...evidence, topology, events, project });`);
      lines.push(`      }, { evidence, resolveEvidenceSource });`);
      lines.push(`      const response = await page.goto(resolved.url, { waitUntil: 'domcontentloaded' });`);
      lines.push(`      if (!response || response.status() !== 200 || page.url() !== resolved.url) throw new Error('Bug Fix pull-request resolution failed: the resolved GitHub pull request is missing or no longer has the expected repository/number identity.');`);
      lines.push(`      await page.goto(evidence.runUrl, { waitUntil: 'domcontentloaded' });`);
      lines.push('      await page.evaluate(installSource);');
      lines.push('      await page.evaluate((watchers) => window.__demoConfigureDomCueWatchers?.(watchers), passiveCueWatchers);');
      lines.push('      await page.evaluate((url) => sessionStorage.setItem("__demoResolvedBugFixPullRequestUrl", url), resolved.url);');
      lines.push('    }');
    } else if (step.type === 'gotoResolvedBugFixPullRequest') {
      lines.push(`    { const url = await page.evaluate(() => sessionStorage.getItem('__demoResolvedBugFixPullRequestUrl')); if (!url) throw new Error('Bug Fix pull-request resolution failed: no resolved pull request is available for Beat 4.7 navigation.'); await page.goto(url, { waitUntil: 'domcontentloaded' }); }`);
      lines.push('    await page.evaluate(installSource);');
      lines.push('    await page.evaluate((watchers) => window.__demoConfigureDomCueWatchers?.(watchers), passiveCueWatchers);');
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('goto'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'waitFor') {
      // Wait for a real element (e.g. a rendered dashboard chart / topology node) to be
      // visible before narrating over it — replaces fixed short timeouts that let beats
      // move on before the view had actually loaded.
      const cue = step.cue ? {
        ...step.cue,
        source: step.cue.source ?? { kind: 'selector', selector: step.selector },
      } : null;
      lines.push(`    { const waitLocator = ${locatorExpression(step.selector)}.first();`);
      lines.push(`      await waitLocator.waitFor({ state: 'visible', timeout: ${step.timeout ?? 60000} });`);
      if (cue) {
        lines.push(`      await waitLocator.evaluate((node, cue) => window.__demoEmitDomCue?.(cue, node), ${JSON.stringify(cue)});`);
      }
      lines.push('    }');
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('waitFor'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'select') {
      lines.push(`    await focus(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, 18, ${step.hold ?? 260});`);
      lines.push(`    await ${locatorExpression(step.selector)}.selectOption(${JSON.stringify(step.option)});`);
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('select'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'waitText') {
      lines.push(`    await page.waitForFunction(() => document.body.innerText.includes(${JSON.stringify(step.text)}), { timeout: ${step.timeout ?? 180000} });`);
      if (step.cue) {
        const cue = {
          ...step.cue,
          source: step.cue.source ?? { kind: 'text', selector: 'body', includes: step.text },
        };
        lines.push(`    await page.evaluate((cue) => window.__demoEmitDomCue?.(cue, document.body), ${JSON.stringify(cue)});`);
      }
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('waitText', { text: ${JSON.stringify(step.text)} }));`);
    } else if (step.type === 'goto') {
      lines.push(`    await page.goto(${JSON.stringify(step.url)}, { waitUntil: 'domcontentloaded' });`);
      lines.push('    await page.evaluate(installSource);');
      lines.push('    await page.evaluate((watchers) => window.__demoConfigureDomCueWatchers?.(watchers), passiveCueWatchers);');
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('goto'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    }
  }

  lines.push(
    '  } finally {',
    '    watcherActive = false;',
    '    await approvalWatcher.catch(() => {});',
    "    await page.evaluate(() => window.__demoStopDomCueWatchers?.()).catch(() => {});",
    "    await page.evaluate(() => window.__demoZoomReset?.()).catch(() => {});",
    '    await pause(350);',
    '    await page.screencast.stop().catch(() => {});',
    '  }',
    "  const activityLog = await page.evaluate(() => window.__demoStopActivity?.() ?? window.__demoGetActivityLog?.() ?? []).catch(() => []);",
    '  page.__demoCueSink = null;',
    '  return { url: page.url(), activityLog, cueLog, captureStartedAtEpochMs: page.__demoCaptureStartedAtEpochMs };',
    '}',
  );

  return lines.join('\n');
}
