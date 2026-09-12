import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertAuthenticatedAgentweaverSession,
  assertChromeProfileIsUnlocked,
  buildChromeLaunchOptions,
  navigateAndStartAgentweaverSignIn,
  resolveGoogleChromeExecutable,
} from '../lib/chrome-default-profile.mjs';
import { parseLoginOptions } from '../login-chrome-default.mjs';
import { assertSupportedLoginCommand } from '../agent-driver-ui/tools.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));

test('Chrome launches from the disposable clone with the Default profile selected', () => {
  const clone = path.resolve('scripts/ui-harness/.auth/chrome-default-automation');
  const installedChrome = resolveGoogleChromeExecutable({
    localAppData: 'C:\\Users\\test\\AppData\\Local',
    exists: (candidate) => candidate === 'C:\\Users\\test\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe',
  });
  const launch = buildChromeLaunchOptions(clone, installedChrome);

  assert.equal(launch.channel, 'chrome');
  assert.equal(launch.executablePath, installedChrome);
  assert.match(launch.executablePath, /Google[\\/]Chrome[\\/]Application[\\/]chrome\.exe$/i);
  assert.equal(launch.userDataDir, clone);
  assert.deepEqual(launch.args, [
    '--profile-directory=Default',
    '--no-first-run',
    '--no-default-browser-check',
  ]);
});

test('refuses to substitute Playwright Chromium when installed Google Chrome is unavailable', () => {
  assert.throws(
    () => resolveGoogleChromeExecutable({
      localAppData: 'C:\\Users\\test\\AppData\\Local',
      programFiles: 'C:\\Program Files',
      programFilesX86: 'C:\\Program Files (x86)',
      exists: () => false,
    }),
    /Installed Google Chrome.*does not fall back to Playwright Chromium/i,
  );
});

test('navigates to the requested app URL before inspecting or clicking its sign-in button', async () => {
  const events = [];
  const button = {
    waitFor: async () => { events.push('wait-for-button'); },
    click: async () => { events.push('click-button'); },
  };
  const page = {
    goto: async (url, options) => { events.push(`goto:${url}:${options.waitUntil}`); },
    evaluate: async () => { events.push('inspect-session'); return false; },
    getByRole: (role, options) => {
      events.push(`find-button:${role}:${options.name}`);
      return button;
    },
  };

  const result = await navigateAndStartAgentweaverSignIn(page, 'https://app.example.test', {
    write: () => {},
  });

  assert.deepEqual(result, { hasSession: false, signInStarted: true });
  assert.deepEqual(events, [
    'goto:https://app.example.test:domcontentloaded',
    'inspect-session',
    'find-button:button:Sign in with Microsoft Entra ID',
    'wait-for-button',
    'click-button',
  ]);
});

test('reports an actionable error before a locked Default profile can be cloned', () => {
  assert.throws(
    () => assertChromeProfileIsUnlocked(['1234']),
    /Close every Chrome window.*will not open Chrome with the live Default profile/i,
  );
});

test('accepts only a real Agentweaver session returned to the configured origin', () => {
  assert.doesNotThrow(() => assertAuthenticatedAgentweaverSession(
    'https://app.example.test',
    { 'agentweaver.sessionToken': 'test-only-memory-value' },
    'https://app.example.test/projects',
  ));
  assert.throws(
    () => assertAuthenticatedAgentweaverSession(
      'https://login.microsoftonline.com',
      { 'agentweaver.sessionToken': 'test-only-memory-value' },
      'https://app.example.test',
    ),
    /did not return to the configured Agentweaver origin/,
  );
  assert.throws(
    () => assertAuthenticatedAgentweaverSession('https://app.example.test', {}, 'https://app.example.test'),
    /authentication was not completed/i,
  );
});

test('rejects CDP and DevTools fallback for Chrome Default authentication', () => {
  assert.throws(
    () => parseLoginOptions(['--base-url', 'https://app.example.test', '--cdp']),
    /CDP\/DevTools attach is not supported.*safe disposable-profile flow/i,
  );
  assert.throws(
    () => parseLoginOptions(['--base-url', 'https://app.example.test', '--cdp-url', 'http://127.0.0.1:9222']),
    /CDP\/DevTools attach is not supported/i,
  );
});

test('retires the generic Chrome capture fallback with the supported recovery command', () => {
  const result = spawnSync(process.execPath, ['login-capture-chrome.mjs'], {
    cwd: path.join(HERE, '..'),
    encoding: 'utf8',
  });

  test('retires the generic tools login fallback with the supported recovery command', () => {
    assert.throws(
      () => assertSupportedLoginCommand({ 'base-url': 'https://app.example.test' }),
      /tools\.mjs login is retired.*login-chrome-default\.mjs/i,
    );
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /login-chrome-default\.mjs.*Do not use generic Playwright, CDP\/DevTools/i);
});
