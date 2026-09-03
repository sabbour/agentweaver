/**
 * login-edge-default.mjs
 *
 * Capture a staging Entra/GitHub login session using the managed Edge Default
 * profile, which satisfies Conditional Access policies that block plain Chromium
 * (device-code flow) and unenrolled browsers.
 *
 * Usage:
 *   node scripts/ui-harness/login-edge-default.mjs [--base-url <url>] [--cdp]
 *
 * Options:
 *   --base-url <url>   Staging app URL (default: AGENTWEAVER_STAGING_URL env var)
 *   --cdp              Connect to an already-running Edge with --remote-debugging-port=9222
 *                      instead of launching a new persistent Edge context.
 *
 * Two modes (auto-selected):
 *
 * Mode A — launchPersistentContext (default, preferred):
 *   Launches a new managed Edge window using the real Windows Default profile at
 *   %LOCALAPPDATA%\Microsoft\Edge\User Data. This works when Edge is not already
 *   running (or all existing Edge windows can be closed first).
 *
 *   ⚠ On Windows, launching a second Edge process with the same User Data directory
 *   while another Edge instance already owns the lock will fail or open a new
 *   incognito-like session instead of the Default profile. If Edge is currently open,
 *   either:
 *     (a) Close all Edge windows first (save any open work), then run this script, OR
 *     (b) Enable remote debugging on the existing Edge instance:
 *         Go to edge://inspect/#devices → tick "Discover network targets" and open
 *         port 9222 (or restart Edge with --remote-debugging-port=9222), then use
 *         --cdp mode below.
 *
 * Mode B — connectOverCDP (--cdp flag):
 *   Attaches to an already-running Edge that was launched with:
 *     msedge.exe --remote-debugging-port=9222 \
 *       --user-data-dir="%LOCALAPPDATA%\Microsoft\Edge\User Data" \
 *       --profile-directory=Default --no-first-run <staging-url>
 *   Or you re-launched it via tools/playwright-cli:
 *     playwright-cli -s=api-auth attach --cdp=http://127.0.0.1:9222
 *   In CDP mode this script does NOT close Edge when it finishes — it only
 *   reads the session state from the already-open page.
 *
 * Output (all written to scripts/ui-harness/.auth/ — git-ignored):
 *   staging.storageState.json             — Playwright storageState (cookies + localStorage)
 *   staging.storageState.json.sessionStorage.json — sessionStorage seed (Agentweaver token)
 *   session-token.txt                     — plain-text token for API harness AGENTWEAVER_TOKEN
 *                                           (gitignored, optional; write only when token found)
 *
 * Never print token values to stdout/stderr. Keep this script's output short.
 */

import { chromium } from '@playwright/test';
import { writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import os from 'node:os';
import { sanitizeUrl } from '../harness-shared/redaction.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const AUTH_DIR = path.join(HERE, '.auth');
const STATE_PATH = path.join(AUTH_DIR, 'staging.storageState.json');
const SEED_PATH = `${STATE_PATH}.sessionStorage.json`;
const TOKEN_PATH = path.join(AUTH_DIR, 'session-token.txt');

// ---------------------------------------------------------------------------
// CLI args
// ---------------------------------------------------------------------------
const argv = process.argv.slice(2);
function arg(flag) {
  const i = argv.indexOf(flag);
  return i !== -1 ? argv[i + 1] : null;
}
const CDP_MODE = argv.includes('--cdp');
const BASE_URL = arg('--base-url') || process.env.AGENTWEAVER_STAGING_URL || null;
const CDP_URL = arg('--cdp-url') || 'http://127.0.0.1:9222';

if (!BASE_URL) {
  console.error('Error: provide --base-url <staging-url> or set AGENTWEAVER_STAGING_URL');
  process.exit(1);
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
async function captureState(page, context) {
  await mkdir(AUTH_DIR, { recursive: true });
  await context.storageState({ path: STATE_PATH });
  const origin = await page.evaluate(() => window.location.origin);
  const entries = await page.evaluate(() => ({ ...window.sessionStorage }));
  await writeFile(SEED_PATH, JSON.stringify({ origin, entries }, null, 2), 'utf8');
  const token = entries['agentweaver.sessionToken'] || null;
  if (token) {
    await writeFile(TOKEN_PATH, String(token), 'utf8');
    console.log('session-token.txt written (contents not printed).');
  } else {
    console.warn('Warning: agentweaver.sessionToken not found in sessionStorage.');
    console.warn('  If you see the sign-in page, complete Entra sign-in first.');
  }
  console.log(JSON.stringify({
    savedState: STATE_PATH,
    savedSeed: SEED_PATH,
    hasToken: Boolean(token),
    tokenLength: token ? String(token).length : 0,
    sessionStorageKeys: Object.keys(entries),
    url: sanitizeUrl(page.url()),
  }, null, 2));
}

// ---------------------------------------------------------------------------
// Mode A: launchPersistentContext with Edge Default profile
// ---------------------------------------------------------------------------
async function runWithPersistentContext() {
  const userDataDir = path.join(os.homedir(), 'AppData', 'Local', 'Microsoft', 'Edge', 'User Data');
  console.log(`Launching Edge Default profile from: ${userDataDir}`);
  console.log('⚠ Close all Edge windows before running, or use --cdp if Edge is already open.\n');

  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: 'msedge',
    headless: false,
    args: ['--profile-directory=Default', '--no-first-run'],
    timeout: 30000,
  });

  const page = context.pages()[0] || await context.newPage();
  page.setDefaultTimeout(180000);
  page.setDefaultNavigationTimeout(180000);

  console.log(`Navigating to ${BASE_URL}`);
  await page.goto(BASE_URL, { waitUntil: 'domcontentloaded', timeout: 60000 });
  console.log('If Entra sign-in appears, complete it in the Edge window.');
  console.log('SSO often completes automatically with the Default profile.');
  console.log('When the authenticated app shell is visible, press Resume in the Playwright Inspector.');
  await page.pause();

  await captureState(page, context);
  await context.close();
}

// ---------------------------------------------------------------------------
// Mode B: connectOverCDP to existing Edge with --remote-debugging-port=9222
// ---------------------------------------------------------------------------
async function runWithCDP() {
  console.log(`Connecting to Edge via CDP at ${CDP_URL}`);
  console.log('Make sure Edge was launched with:');
  console.log(`  msedge.exe --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\\Microsoft\\Edge\\User Data" --profile-directory=Default --no-first-run ${BASE_URL}\n`);

  const browser = await chromium.connectOverCDP(CDP_URL);
  const contexts = browser.contexts();
  if (contexts.length === 0) throw new Error('No browser contexts found on CDP target.');
  const context = contexts[0];
  const pages = context.pages();
  let page = pages.find((p) => p.url().startsWith(BASE_URL)) || pages[0];
  if (!page) throw new Error('No open pages found. Navigate to the staging app first.');

  page.setDefaultTimeout(180000);
  console.log(`Using page: ${sanitizeUrl(page.url())}`);

  if (!page.url().startsWith(BASE_URL)) {
    console.log(`Navigating to ${BASE_URL}`);
    await page.goto(BASE_URL, { waitUntil: 'domcontentloaded', timeout: 60000 });
  }

  console.log('If Entra sign-in appears, complete it. When authenticated, press Resume.');
  await page.pause();

  await captureState(page, context);
  // Do NOT close browser in CDP mode — the user's Edge session stays open.
  console.log('Done. Edge session left open (CDP mode).');
  await browser.close(); // disconnects Playwright, does not close Edge
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
if (CDP_MODE) {
  await runWithCDP();
} else {
  await runWithPersistentContext();
}
