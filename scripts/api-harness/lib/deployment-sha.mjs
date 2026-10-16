/** Validate the exact full commit identifier required to pin a candidate deployment. */
export function requireExpectedDeploymentSha(value) {
  if (typeof value !== 'string' || !/^[0-9a-f]{40}$/.test(value)) {
    throw new Error('Expected deployment SHA must be a full lowercase 40-character commit identifier.');
  }
  return value;
}

/** Return whether the reported deployment source is exactly the validated candidate commit. */
export function deploymentShaMatches(expectedSha, reportedSha) {
  return requireExpectedDeploymentSha(expectedSha) === reportedSha;
}

/** Reject missing or different deployment source evidence and return the accepted candidate SHA. */
export function requireDeploymentShaMatch(expectedSha, reportedSha) {
  if (!deploymentShaMatches(expectedSha, reportedSha)) {
    throw new Error('The observed deployment source SHA is missing or differs from the exact candidate.');
  }
  return expectedSha;
}
