import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {
  assertAuthenticatedAgentweaverSession,
  assertChromeProfileIsUnlocked,
  buildChromeLaunchOptions,
  navigateAndStartAgentweaverSignIn,
} from '../lib/chrome-default-profile.mjs';

test('Chrome launches from the disposable clone with the Default profile selected', () => {
  const clone = path.resolve('scripts/ui-harness/.auth/chrome-default-automation');
  const launch = buildChromeLaunchOptions(clone);

  assert.equal(launch.channel, 'chrome');
  assert.equal(launch.userDataDir, clone);
  assert.deepEqual(launch.args, [
    '--profile-directory=Default',
    '--no-first-run',
    '--no-default-browser-check',
  ]);
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
