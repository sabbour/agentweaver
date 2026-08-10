import { createHash } from 'node:crypto';
import { readFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

const REDACTED = '[REDACTED]';
const SENSITIVE_KEY = /token|cookie|authorization|storage.?state|secret|password/i;

export function redact(value, key = '') {
  if (SENSITIVE_KEY.test(key)) return REDACTED;
  if (Array.isArray(value)) return value.map((item) => redact(item));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([name, item]) => [name, redact(item, name)]));
  return value;
}

export function evidenceHash(value) {
  return createHash('sha256').update(typeof value === 'string' ? value : JSON.stringify(value)).digest('hex');
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

export async function captureTurn({ page, capture, directory, id, intent, action, target, readiness = null, frustrationSignals = [] }) {
  await mkdir(directory, { recursive: true });
  const screenshotPath = path.join(directory, `turn-${id}.png`);
  await page.screenshot({ path: screenshotPath, fullPage: true });
  const domSnapshot = await structuredDomSnapshot(page);
  // A non-2xx fetch alone is not a UI P0; it must have visibly surfaced to the user.
  const visibleAlert = domSnapshot.some((element) => element.visible && element.role === 'alert');
  const network = capture.network.splice(0).map((entry) => ({
    ...entry,
    userFacing: visibleAlert && entry.status >= 400 && ['fetch', 'xhr'].includes(entry.resourceType),
  }));
  return redact({
    id, at: new Date().toISOString(), intent, action, target, readiness, url: page.url(), domSnapshot, screenshotPath,
    screenshotHash: evidenceHash(await readFile(screenshotPath)),
    console: capture.console.splice(0), network, frustrationSignals,
  });
}
