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
