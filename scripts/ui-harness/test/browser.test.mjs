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

test('browser target boundary permits only GitHub OAuth navigation in login mode', () => {
  const baseUrl = 'https://agentweaver.foo.staging.example.com';

  assert.equal(
    guardedUrl(baseUrl, 'https://github.com/login/oauth/authorize?client_id=test', {
      allowGitHubOAuthNavigation: true,
    }).origin,
    'https://github.com',
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://github.com/login/oauth/authorize?client_id=test', {}),
    /refusing non-staging target/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://two.staging.example.com', { allowGitHubOAuthNavigation: true }),
    /cross-origin/,
  );
  assert.throws(
    () => guardedUrl(baseUrl, 'https://github.com/settings/profile', { allowGitHubOAuthNavigation: true }),
    /refusing non-staging target/,
  );
});
