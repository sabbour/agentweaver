import test from 'node:test';
import assert from 'node:assert/strict';
import { guardedUrl } from '../lib/browser.mjs';

test('browser target boundary permits staging and blocks production before launch', () => {
  assert.equal(guardedUrl('https://agentweaver.foo.staging.example.com', '/projects', {}).pathname, '/projects');
  assert.throws(() => guardedUrl('https://agentweaver.example.com', '/', {}), /refusing non-staging target/);
  assert.equal(
    guardedUrl('https://agentweaver.example.com', '/', { allowProd: true, confirmProduction: true }).hostname,
    'agentweaver.example.com',
  );
});

test('browser target boundary blocks cross-origin navigation', () => {
  assert.throws(
    () => guardedUrl('https://one.staging.example.com', 'https://two.staging.example.com', {}),
    /cross-origin/,
  );
});

test('browser target boundary permits the whole GitHub origin in login mode', () => {
  const baseUrl = 'https://agentweaver.foo.staging.example.com';

  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/login/oauth/authorize?client_id=test', {
      allowIdentityProviderNavigation: true,
    }).origin,
    'https://github.com',
  );
  // Real human logins can be routed through many github.com paths beyond the
  // initial OAuth authorize hop -- 2FA challenges, new-device verification,
  // device-flow codes, org SSO -- all of which must be permitted in login mode.
  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/sessions/two-factor', {
      allowIdentityProviderNavigation: true,
    }).pathname,
    '/sessions/two-factor',
  );
  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/sessions/verified-device', {
      allowIdentityProviderNavigation: true,
    }).pathname,
    '/sessions/verified-device',
  );
  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/login/device', {
      allowIdentityProviderNavigation: true,
    }).pathname,
    '/login/device',
  );
  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/orgs/some-org/sso', {
      allowIdentityProviderNavigation: true,
    }).pathname,
    '/orgs/some-org/sso',
  );
  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/settings/profile', {
      allowIdentityProviderNavigation: true,
    }).pathname,
    '/settings/profile',
  );

  // The automated/persona-driven action() codepath never sets
  // allowIdentityProviderNavigation, so github.com navigation must still be
  // blocked when the flag is absent -- this is the actual security boundary.
  assert.throws(
    () => guardedUrl(baseUrl, 'https://github.com/login/oauth/authorize?client_id=test', {}),
    /refusing non-staging target/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://github.com/sessions/two-factor', {}),
    /refusing non-staging target/,
  );

  // The flag only ever widens the allowlist to the real github.com origin --
  // any other cross-origin destination, even a lookalike, is still blocked.
  assert.throws(
    () => guardedUrl(baseUrl, 'https://two.staging.example.com', { allowIdentityProviderNavigation: true }),
    /cross-origin/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://github.com.evil.example.com/login', { allowIdentityProviderNavigation: true }),
    /refusing non-staging target/,
  );
});

test('browser target boundary permits configured Entra and Microsoft-account origins only in login mode', () => {
  const baseUrl = 'https://agentweaver.foo.staging.example.com';
  const identityProviderOptions = {
    allowIdentityProviderNavigation: true,
    identityProviderOrigins: ['https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0'],
  };

  assert.equal(
    guardedUrl(baseUrl, 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', identityProviderOptions).origin,
    'https://login.microsoftonline.com',
  );
  assert.equal(
    guardedUrl(baseUrl, 'https://login.live.com/login.srf', identityProviderOptions).origin,
    'https://login.live.com',
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://contoso.b2clogin.com/contoso.onmicrosoft.com/oauth2/v2.0/authorize', identityProviderOptions),
    /refusing non-staging target/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://login.microsoftonline.com/common/oauth2/v2.0/authorize', {}),
    /refusing non-staging target/,
  );
});

test('browser target boundary honors a custom configured Entra authority origin in login mode', () => {
  const baseUrl = 'https://agentweaver.foo.staging.example.com';
  const identityProviderOptions = {
    allowIdentityProviderNavigation: true,
    identityProviderOrigins: ['https://contoso.b2clogin.com/contoso.onmicrosoft.com/oauth2/v2.0/authorize'],
  };

  assert.equal(
    guardedUrl(baseUrl, 'https://contoso.b2clogin.com/contoso.onmicrosoft.com/oauth2/v2.0/authorize', identityProviderOptions).origin,
    'https://contoso.b2clogin.com',
  );
});

test('browser target boundary permits only generated previews in preview mode', () => {
  const baseUrl = 'https://agentweaver.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io';
  const previewUrl = 'https://swift-falcon-amber-abcdefghijklmnopqrstuvwxyz-preview.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io';

  assert.equal(
    guardedUrl(baseUrl, previewUrl, { allowAgentweaverPreviewNavigation: true }).hostname,
    new URL(previewUrl).hostname,
  );
  assert.throws(
    () => guardedUrl(baseUrl, previewUrl, {}),
    /cross-origin/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://evil-preview.6a63b4fb256d5a00017339af.westus2.staging.aksapp.io', {
      allowAgentweaverPreviewNavigation: true,
    }),
    /cross-origin/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://swift-falcon-amber-abcdefghijklmnopqrstuvwxyz-preview.other.staging.example.com', {
      allowAgentweaverPreviewNavigation: true,
    }),
    /cross-origin/,
  );
});
