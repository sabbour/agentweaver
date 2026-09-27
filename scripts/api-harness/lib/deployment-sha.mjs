export const MIN_DEPLOYMENT_SHA_LENGTH = 7;
export const FULL_DEPLOYMENT_SHA_LENGTHS = new Set([40, 64]);

function normalizeGitSha(value) {
  return typeof value === 'string' ? value.trim().toLowerCase() : '';
}

function isValidReportedGitSha(value) {
  return value.length >= MIN_DEPLOYMENT_SHA_LENGTH
    && value.length <= Math.max(...FULL_DEPLOYMENT_SHA_LENGTHS)
    && /^[0-9a-f]+$/.test(value);
}

export function requireExpectedDeploymentSha(value) {
  const normalized = normalizeGitSha(value);
  if (!normalized) {
    throw new Error(
      'Expected deployment SHA is required. Pass --expected-deployment-sha <git-sha>.',
    );
  }
  if (!FULL_DEPLOYMENT_SHA_LENGTHS.has(normalized.length) || !/^[0-9a-f]+$/.test(normalized)) {
    throw new Error(
      'Expected deployment SHA must be a full 40- or 64-character hexadecimal commit identifier.',
    );
  }
  return normalized;
}

export function deploymentShaMatches(expectedSha, reportedSha) {
  const expected = requireExpectedDeploymentSha(expectedSha);
  const reported = normalizeGitSha(reportedSha);
  return isValidReportedGitSha(reported)
    && reported.length <= expected.length
    && expected.startsWith(reported);
}

export function requireDeploymentShaMatch(expectedSha, reportedSha) {
  const expected = requireExpectedDeploymentSha(expectedSha);
  if (!deploymentShaMatches(expected, reportedSha)) {
    const reported = normalizeGitSha(reportedSha) || '<missing>';
    throw new Error(
      `Deployment SHA mismatch: /api/version reported "${reported}", expected a valid prefix of "${expected}".`,
    );
  }
  return expected;
}
