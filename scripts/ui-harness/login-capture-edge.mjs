/**
 * DEPRECATED: This script uses plain Chromium (channel msedge) WITHOUT the real Edge
 * Default profile's User Data directory. It will fail Conditional Access on Entra-
 * protected staging deployments.
 *
 * Use scripts/ui-harness/login-edge-default.mjs instead, which supports:
 *   - launchPersistentContext with the real Default profile (Option A, preferred)
 *   - connectOverCDP to an already-running Edge at port 9222 (Option B, --cdp)
 *
 * See scripts/ui-harness/SKILL.md → Authentication section.
 */
import { chromium } from '@playwright/test';
import { writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';

const base = process.argv[2];
const outDir = process.argv[3];

// Use real Edge channel — Conditional Access requires managed browser/device.
const browser = await chromium.launch({
  headless: false,
  channel: 'msedge',
});
const context = await browser.newContext();
const page = await context.newPage();
page.setDefaultTimeout(180000);
page.setDefaultNavigationTimeout(180000);

console.log('Opening app in Edge:', base);
await page.goto(base, { waitUntil: 'domcontentloaded', timeout: 180000 });
console.log('Complete Entra sign-in in the Edge window.');
console.log('When the authenticated app shell is visible, press Resume in the Playwright Inspector.');
await page.pause();

await mkdir(outDir, { recursive: true });
const statePath = path.join(outDir, 'staging.storageState.json');
await context.storageState({ path: statePath });
const origin = await page.evaluate(() => window.location.origin);
const entries = await page.evaluate(() => ({ ...window.sessionStorage }));
const seedPath = `${statePath}.sessionStorage.json`;
await writeFile(seedPath, JSON.stringify({ origin, entries }, null, 2));
const token = entries['agentweaver.sessionToken'] || null;
console.log(JSON.stringify({
  savedState: statePath,
  savedSeed: seedPath,
  hasToken: Boolean(token),
  tokenLen: token ? String(token).length : 0,
  keys: Object.keys(entries),
  url: page.url(),
}, null, 2));
if (token) {
  await writeFile(path.join(outDir, 'session-token.txt'), String(token), 'utf8');
  console.log('Wrote session-token.txt (do not print contents).');
} else {
  console.error('No agentweaver.sessionToken in sessionStorage after login.');
  process.exitCode = 2;
}
await browser.close();
