import { browserZoomBootstrapSource } from './zoom.mjs';

// buildInstallSource used to be a hand-duplicated copy of zoom.mjs's bootstrap
// (with the same '#root'-only transform bug). Now delegates to the single
// canonical implementation in zoom.mjs so the zoom/cursor/activity-tracking
// bootstrap only exists in one place and any future fix (like the body- vs
// root-transform fix) can't silently drift out of sync between the two files.
function buildInstallSource() {
  return browserZoomBootstrapSource();
}

function locatorExpression(selector) {
  return `(${selector})`;
}

export function renderCaptureScript(plan) {
  const installSource = JSON.stringify(buildInstallSource());
  const lines = [
    'async page => {',
    '  const pause = ms => page.waitForTimeout(ms);',
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
    `  await page.goto(${JSON.stringify(plan.startUrl)}, { waitUntil: 'domcontentloaded' });`,
    "  await page.evaluate(() => { try { sessionStorage.removeItem('__demoCaptureEpoch'); sessionStorage.removeItem('__demoActivityLog'); } catch (e) {} });",
    '  await page.evaluate(installSource);',
    `  await page.screencast.start({ path: ${JSON.stringify(plan.videoPath)}, size: { width: ${plan.viewport?.width ?? 1920}, height: ${plan.viewport?.height ?? 1080} } });`,
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
    '    const zoom = scale > 1.02;',
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
    } else if (step.type === 'type') {
      lines.push(`    await typeInto(${locatorExpression(step.selector)}, ${JSON.stringify(step.text)}, ${step.scale ?? 1.6}, ${step.delay ?? 12}, ${step.after ?? 700});`);
    } else if (step.type === 'press') {
      const pressTarget = step.selector ? `${locatorExpression(step.selector)}.press(${JSON.stringify(step.key)})` : `page.keyboard.press(${JSON.stringify(step.key)})`;
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('press', { key: ${JSON.stringify(step.key)} }));`);
      lines.push(`    await ${pressTarget};`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'eval') {
      // Run an arbitrary in-page expression (e.g. a guarded cleanup that removes a
      // duplicate list item before it is captured). Marked so the idle-trimmer keeps
      // the surrounding frames. Kept intentionally simple: the expression string is
      // authored in-repo, never from user input.
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('eval'));`);
      lines.push(`    await page.evaluate(() => { ${step.expression} });`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'waitFor') {
      // Wait for a real element (e.g. a rendered dashboard chart / topology node) to be
      // visible before narrating over it — replaces fixed short timeouts that let beats
      // move on before the view had actually loaded.
      lines.push(`    await ${locatorExpression(step.selector)}.first().waitFor({ state: 'visible', timeout: ${step.timeout ?? 60000} });`);
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('waitFor'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'select') {
      lines.push(`    await focus(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, 18, ${step.hold ?? 260});`);
      lines.push(`    await ${locatorExpression(step.selector)}.selectOption(${JSON.stringify(step.option)});`);
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('select'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    } else if (step.type === 'waitText') {
      lines.push(`    await page.waitForFunction(() => document.body.innerText.includes(${JSON.stringify(step.text)}), { timeout: ${step.timeout ?? 180000} });`);
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('waitText', { text: ${JSON.stringify(step.text)} }));`);
    } else if (step.type === 'goto') {
      lines.push(`    await page.goto(${JSON.stringify(step.url)}, { waitUntil: 'domcontentloaded' });`);
      lines.push('    await page.evaluate(installSource);');
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('goto'));`);
      if (step.after) lines.push(`    await pause(${step.after});`);
    }
  }

  lines.push(
    '  } finally {',
    "    await page.evaluate(() => window.__demoZoomReset?.()).catch(() => {});",
    '    await pause(350);',
    '    await page.screencast.stop().catch(() => {});',
    '  }',
    "  const activityLog = await page.evaluate(() => window.__demoStopActivity?.() ?? window.__demoGetActivityLog?.() ?? []).catch(() => []);",
    '  return { url: page.url(), activityLog };',
    '}',
  );

  return lines.join('\n');
}
