import test from 'node:test';
import assert from 'node:assert/strict';
import {
  deploymentShaMatches, requireDeploymentShaMatch, requireExpectedDeploymentSha,
} from '../lib/deployment-sha.mjs';

const sha = 'a'.repeat(40);

test('P1 source matching accepts only the complete exact observed commit', () => {
  assert.equal(requireDeploymentShaMatch(sha, sha), sha);
  for (const reported of [undefined, sha.slice(0, 7), 'b'.repeat(40), sha.toUpperCase(), ` ${sha}`]) {
    assert.equal(deploymentShaMatches(sha, reported), false);
    assert.throws(() => requireDeploymentShaMatch(sha, reported), /missing or differs/);
  }
});

test('missing, abbreviated, nonhex or noncanonical expected source is rejected', () => {
  for (const expected of [undefined, '', 'abc1234', 'a'.repeat(64), 'z'.repeat(40), sha.toUpperCase()]) {
    assert.throws(() => requireExpectedDeploymentSha(expected), /full lowercase 40/);
  }
});
