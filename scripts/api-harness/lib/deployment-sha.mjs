export function requireExpectedDeploymentSha(value) {
  if (typeof value !== 'string' || !/^[0-9a-f]{40}$/.test(value)) {
    throw new Error('Expected deployment SHA must be a full lowercase 40-character commit identifier.');
  }
  return value;
}

export function deploymentShaMatches(expectedSha, reportedSha) {
  return requireExpectedDeploymentSha(expectedSha) === reportedSha;
}

export function requireDeploymentShaMatch(expectedSha, reportedSha) {
  if (!deploymentShaMatches(expectedSha, reportedSha)) {
    throw new Error('The observed deployment source SHA is missing or differs from the exact candidate.');
  }
  return expectedSha;
}
