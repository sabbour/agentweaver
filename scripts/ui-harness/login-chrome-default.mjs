/**
 * Capture Agentweaver authentication using a disposable clone of the managed
 * Chrome Default profile. The live Chrome profile is never launched by Playwright.
 */
import { chromium } from '@playwright/test';
import { mkdir, rm, writeFile, chmod } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { sanitizeUrl } from '../harness-shared/redaction.mjs';
import {
  assertAuthenticatedAgentweaverSession,
  buildChromeLaunchOptions,
  navigateAndStartAgentweaverSignIn,
  refreshDisposableChromeProfile,
  resolveChromeDefaultProfile,
} from './lib/chrome-default-profile.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const AUTH_DIR = path.join(HERE, '.auth');
const STATE_PATH = path.join(AUTH_DIR, 'staging.storageState.json');
const SEED_PATH = `${STATE_PATH}.sessionStorage.json`;
const TOKEN_PATH = path.join(AUTH_DIR, 'session-token.txt');

function option(argv, name) {
  const index = argv.indexOf(name);
  return index === -1 ? null : argv[index + 1];
}

export function parseLoginOptions(argv, environment = process.env) {
  if (argv.includes('--cdp') || argv.includes('--cdp-url')) {
    throw new Error(
      'CDP/DevTools attach is not supported for Chrome Default authentication. '
      + 'Close Chrome and rerun this command without --cdp so the harness can use its safe disposable-profile flow.',
    );
  }
  const baseUrl = option(argv, '--base-url') ?? environment.AGENTWEAVER_STAGING_URL;
  if (!baseUrl) throw new Error('Provide --base-url <staging-url> or set AGENTWEAVER_STAGING_URL.');
  const target = new URL(baseUrl);
  if (target.protocol !== 'https:') throw new Error('--base-url must use HTTPS.');
  return { baseUrl: target.toString() };
}

export async function captureState(page, context, baseUrl) {
  const origin = await page.evaluate(() => window.location.origin);
  const entries = await page.evaluate(() => ({ ...window.sessionStorage }));
  assertAuthenticatedAgentweaverSession(origin, entries, baseUrl);
  await mkdir(AUTH_DIR, { recursive: true, mode: 0o700 });
  await context.storageState({ path: STATE_PATH });
  await chmod(STATE_PATH, 0o600).catch(() => {});
  await writeFile(SEED_PATH, JSON.stringify({ origin, entries }, null, 2), { encoding: 'utf8', mode: 0o600 });
  await chmod(SEED_PATH, 0o600).catch(() => {});
  const token = entries['agentweaver.sessionToken'];
  if (token) {
    await writeFile(TOKEN_PATH, String(token), { encoding: 'utf8', mode: 0o600 });
    await chmod(TOKEN_PATH, 0o600).catch(() => {});
  }
  console.log(`Authentication state saved locally for ${sanitizeUrl(page.url())}; contents were not printed.`);
}

export async function runWithDisposableProfile(baseUrl, dependencies = {}) {
  const browser = dependencies.chromium ?? chromium;
  const authDir = dependencies.authDir ?? AUTH_DIR;
  const automationUserDataDir = path.join(authDir, 'chrome-default-automation');
  const chromeProfile = dependencies.chromeProfile ?? resolveChromeDefaultProfile();
  await refreshDisposableChromeProfile({
    authRoot: authDir,
    automationUserDataDir,
    chromeProfile,
    processIds: dependencies.processIds,
  });

  let context;
  try {
    const launch = buildChromeLaunchOptions(automationUserDataDir);
    const { userDataDir, ...launchOptions } = launch;
    context = await browser.launchPersistentContext(userDataDir, launchOptions);
    const page = context.pages()[0] ?? await context.newPage();
    page.setDefaultTimeout(180_000);
    page.setDefaultNavigationTimeout(180_000);
    await navigateAndStartAgentweaverSignIn(page, baseUrl);
    console.log('When the authenticated Agentweaver app is visible, press Resume in the Playwright Inspector.');
    await page.pause();
    await captureState(page, context, baseUrl);
  } finally {
    await context?.close().catch(() => {});
    await rm(automationUserDataDir, { recursive: true, force: true }).catch(() => {});
  }
}

async function main() {
  const { baseUrl } = parseLoginOptions(process.argv.slice(2));
  await runWithDisposableProfile(baseUrl);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    console.error(`Login failed: ${error.message}`);
    process.exitCode = 1;
  });
}
