import assert from 'node:assert/strict';
import test from 'node:test';
import { readRegistry } from '../registry-read.mjs';

const url = 'https://ghcr.io/v2/sabbour/image/manifests/sha256:abc';
const read = response => response.text();
const networkError = () => new TypeError('fetch failed', {
  cause: Object.assign(new Error('socket reset by peer'), { code: 'ECONNRESET' }),
});

test('registry reads retry network TypeError and 429/5xx with bounded exponential backoff', async () => {
  for (const failure of [networkError(), 429, 500, 502, 503, 504, 599]) {
    let attempts = 0;
    let cancelled = 0;
    const delays = [];
    const result = await readRegistry(url, undefined, read, {
      delay: async milliseconds => delays.push(milliseconds),
      fetchImpl: async () => {
        if (++attempts === 4) return new Response('manifest');
        if (failure instanceof Error) throw failure;
        return { ok: false, status: failure, body: { cancel: async () => { cancelled++; } } };
      },
    });
    assert.equal(result.value, 'manifest');
    assert.equal(attempts, 4);
    assert.equal(cancelled, failure instanceof Error ? 0 : 3);
    assert.deepEqual(delays, [1000, 2000, 4000]);
  }
});

test('registry reads exhaust exactly four attempts and retain URL and underlying cause diagnostics', async () => {
  for (const failure of [networkError(), 429, 503]) {
    let attempts = 0;
    const delays = [];
    await assert.rejects(readRegistry(url, undefined, read, {
      delay: async milliseconds => delays.push(milliseconds),
      fetchImpl: async () => {
        attempts++;
        if (failure instanceof Error) throw failure;
        return { ok: false, status: failure };
      },
    }), error => {
      assert.ok(error.message.includes(url));
      assert.match(error.message, /after 4 attempt/);
      if (failure instanceof Error) {
        assert.match(error.message, /fetch failed; cause: ECONNRESET socket reset by peer/);
        assert.equal(error.cause, failure);
      } else {
        assert.ok(error.message.includes(`HTTP ${failure}`));
      }
      return true;
    });
    assert.equal(attempts, 4);
    assert.deepEqual(delays, [1000, 2000, 4000]);
  }
});

test('registry reads do not retry other statuses or non-network exceptions', async () => {
  for (const failure of [
    400, 401, 403, 404, 408, 422, 302,
    new Error('failure'),
    new TypeError('invalid manifest metadata'),
    new SyntaxError('bad JSON'),
  ]) {
    let attempts = 0;
    await assert.rejects(readRegistry(url, undefined, read, {
      delay: async () => assert.fail('must not delay'),
      fetchImpl: async () => {
        attempts++;
        if (failure instanceof Error) throw failure;
        return { ok: false, status: failure };
      },
    }), /after 1 attempt/);
    assert.equal(attempts, 1);
  }
});

test('registry read diagnostics omit credentials, query tokens, and fragments, including in causes', async () => {
  const secretUrl = 'https://alice:password@ghcr.io/token?access_token=secret#private';
  const failure = new TypeError(`fetch failed for ${secretUrl}`, {
    cause: Object.assign(new Error(`timeout reading ${secretUrl}`), { code: 'UND_ERR_CONNECT_TIMEOUT' }),
  });
  await assert.rejects(readRegistry(secretUrl, { headers: { authorization: 'Bearer header-secret' } }, read, {
    delay: async () => {},
    fetchImpl: async () => { throw failure; },
  }), error => {
    assert.match(error.message, /https:\/\/ghcr.io\/token/);
    assert.match(error.message, /UND_ERR_CONNECT_TIMEOUT timeout reading/);
    assert.doesNotMatch(error.message, /alice|password|access_token|secret|private/);
    return true;
  });
});

test('registry reads retry interrupted bodies, not malformed JSON', async () => {
  let attempts = 0;
  const delays = [];
  const result = await readRegistry(url, undefined, read, {
    delay: async milliseconds => delays.push(milliseconds),
    fetchImpl: async () => ({
      ok: true,
      text: async () => {
        if (++attempts === 1) throw networkError();
        return 'complete';
      },
    }),
  });
  assert.equal(result.value, 'complete');
  assert.equal(attempts, 2);
  assert.deepEqual(delays, [1000]);
  attempts = 0;
  await assert.rejects(readRegistry(url, undefined, response => response.json(), {
    delay: async () => assert.fail('invalid JSON must not retry'),
    fetchImpl: async () => { attempts++; return new Response('not JSON'); },
  }), /after 1 attempt/);
  assert.equal(attempts, 1);
});
