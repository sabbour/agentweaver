import test from 'node:test';
import assert from 'node:assert/strict';
import { createBrokerAuthProvider } from '../broker-auth.mjs';

const claims = { iss: 'https://broker.test/', aud: 'gateway', sub: 'controlled-actor', exp: 2000 };
const tokenFor = value => ['e30', Buffer.from(JSON.stringify(value)).toString('base64url'), 'fixture-signature'].join('.');
const options = {
  target: 'https://gateway.test/', issuer: claims.iss, audience: claims.aud, actorId: claims.sub,
  token: tokenFor(claims), now: () => 1000000,
};

test('supplied broker identity is origin-bound and expires without acquiring or refreshing credentials', async () => {
  let now = 1000000;
  const provider = createBrokerAuthProvider({ ...options, now: () => now });
  assert.equal(await provider.getAuthorization('https://gateway.test/api/v1/projects'),
    ['Bearer', options.token].join(' '));
  await assert.rejects(provider.getAuthorization('https://other.test/'), /outside/);
  now = 2000000;
  await assert.rejects(provider.getAuthorization(options.target), /AUTH_EXPIRED/);
});

test('wrong issuer, actor, audience or missing expiry fails before a network request', () => {
  for (const changed of [
    { ...claims, iss: 'https://other.test/' },
    { ...claims, sub: 'other-actor' },
    { ...claims, aud: 'mcp' },
    { ...claims, exp: undefined },
  ]) {
    assert.throws(() => createBrokerAuthProvider({ ...options, token: tokenFor(changed) }), /does not match/);
  }
  assert.throws(() => createBrokerAuthProvider({ ...options, token: 'not-a-jwt' }), /invalid JWT/);
  assert.throws(() => createBrokerAuthProvider({ ...options, audience: undefined }), /explicit/);
});

test('an explicitly selected audience can appear in the broker audience list', async () => {
  const provider = createBrokerAuthProvider({ ...options, token: tokenFor({ ...claims, aud: ['mcp', 'gateway'] }) });
  assert.ok(await provider.getAuthorization(options.target));
});
