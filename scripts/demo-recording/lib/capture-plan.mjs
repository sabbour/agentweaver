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
    '  await page.evaluate(installSource);',
    `  await page.screencast.start({ path: ${JSON.stringify(plan.videoPath)}, size: { width: ${plan.viewport?.width ?? 1920}, height: ${plan.viewport?.height ?? 1080} } });`,
    '  const focus = async (locator, scale = 1.45, steps = 18, hold = 260) => {',
    '    await locator.scrollIntoViewIfNeeded().catch(() => {});',
    '    const box = await locator.boundingBox();',
    "    if (!box) throw new Error('No bounding box');",
    '    const x = box.x + Math.max(8, Math.min(box.width / 2, box.width - 8));',
    '    const y = box.y + Math.max(8, Math.min(box.height / 2, box.height - 8));',
    "    await page.evaluate(({ x, y, scale }) => { window.__demoActivityMark?.('focus', { x: Math.round(x), y: Math.round(y), scale }); window.__demoZoomFocus?.(x, y, scale); window.__demoCursorMove?.(x, y); }, { x, y, scale });",
    '    await pause(220);',
    '    await page.mouse.move(x, y, { steps });',
    '    await pause(hold);',
    '  };',
    '  const click = async (locator, scale = 1.45, after = 620) => {',
    '    await focus(locator, scale);',
    '    await locator.click();',
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
      lines.push(`    await click(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, ${step.after ?? 620});`);
    } else if (step.type === 'hover') {
      lines.push(`    await focus(${locatorExpression(step.selector)}, ${step.scale ?? 1.45}, 18, ${step.hold ?? 900});`);
    } else if (step.type === 'type') {
      lines.push(`    await typeInto(${locatorExpression(step.selector)}, ${JSON.stringify(step.text)}, ${step.scale ?? 1.6}, ${step.delay ?? 12}, ${step.after ?? 700});`);
    } else if (step.type === 'press') {
      lines.push(`    await page.evaluate(() => window.__demoActivityMark?.('press', { key: ${JSON.stringify(step.key)} }));`);
      lines.push(`    await page.keyboard.press(${JSON.stringify(step.key)});`);
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
