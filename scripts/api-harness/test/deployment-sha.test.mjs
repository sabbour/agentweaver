import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  deploymentShaMatches,
  requireDeploymentShaMatch,
  requireExpectedDeploymentSha,
} from '../lib/deployment-sha.mjs';

const fullSha = '0def5f6c0adf49117c3af6d37696ea14e9604f6c';
const longerSha = `${fullSha}0123456789abcdef01234567`;

test('deployment SHA accepts exact full and documented short-prefix matches', () => {
  assert.equal(deploymentShaMatches(fullSha, fullSha), true);
  assert.equal(deploymentShaMatches(fullSha, '0def5f6'), true);
});

test('deployment SHA rejects a reported SHA that extends the full expected SHA', () => {
  assert.equal(deploymentShaMatches(fullSha, longerSha), false);
});

test('deployment SHA rejects mismatched and missing reported values', () => {
  assert.equal(deploymentShaMatches(fullSha, 'f9b7e77'), false);
  assert.throws(
    () => deploymentShaMatches('0def5f6', fullSha),
    /full 40- or 64-character hexadecimal/,
  );
  assert.equal(deploymentShaMatches(fullSha, undefined), false);
  assert.equal(deploymentShaMatches(fullSha, ''), false);
});

test('deployment SHA comparison is case-insensitive for valid hexadecimal input', () => {
  assert.equal(deploymentShaMatches(fullSha.toUpperCase(), '0DEF5F6'), true);
});

test('deployment SHA rejects malformed and too-short reported values', () => {
  assert.equal(deploymentShaMatches(fullSha, '0def5g6'), false);
  assert.equal(deploymentShaMatches(fullSha, '0def5f'), false);
});

test('expected deployment SHA is required and must be an unambiguous Git hex identifier', () => {
  assert.throws(
    () => requireExpectedDeploymentSha(undefined),
    /--expected-deployment-sha <git-sha>/,
  );
  assert.throws(
    () => requireExpectedDeploymentSha(''),
    /--expected-deployment-sha <git-sha>/,
  );
  assert.throws(
    () => requireExpectedDeploymentSha('0def5f'),
    /full 40- or 64-character hexadecimal/,
  );
  assert.throws(
    () => requireExpectedDeploymentSha('0def5f6'),
    /full 40- or 64-character hexadecimal/,
  );
  assert.throws(
    () => requireExpectedDeploymentSha('not-a-sha'),
    /full 40- or 64-character hexadecimal/,
  );
});

test('deployment SHA setup guard rejects invalid reported provenance clearly', () => {
  assert.equal(requireDeploymentShaMatch(fullSha, '0def5f6'), fullSha);
  assert.throws(
    () => requireDeploymentShaMatch(fullSha, 'f9b7e77'),
    /Deployment SHA mismatch.*f9b7e77/,
  );
  assert.throws(
    () => requireDeploymentShaMatch(fullSha, undefined),
    /Deployment SHA mismatch.*<missing>/,
  );
  assert.throws(
    () => requireDeploymentShaMatch(fullSha, '0def5f'),
    /Deployment SHA mismatch/,
  );
  assert.throws(
    () => requireDeploymentShaMatch(fullSha, '0def5g6'),
    /Deployment SHA mismatch/,
  );
});
