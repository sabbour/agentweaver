function fail(message) {
  throw new Error(`Bug Fix pull-request resolution failed: ${message}`);
}

export function parseGitHubPullRequestUrl(value) {
  const url = new URL(value);
  const parts = url.pathname.split('/').filter(Boolean);
  if (url.protocol !== 'https:' || url.hostname !== 'github.com'
    || parts.length !== 4 || parts[2] !== 'pull' || !/^[1-9][0-9]*$/u.test(parts[3])) {
    fail('the pull-request URL must be a canonical https://github.com/<owner>/<repo>/pull/<number> URL.');
  }
  return {
    url: `https://github.com/${parts[0]}/${parts[1]}/pull/${parts[3]}`,
    repository: `${parts[0]}/${parts[1]}`,
    number: parts[3],
  };
}

function parseRunUrl(value) {
  const url = new URL(value);
  const parts = url.pathname.split('/').filter(Boolean);
  const projectIndex = parts.indexOf('projects');
  const runIndex = parts.indexOf('runs');
  if (projectIndex < 0 || runIndex < 0 || !parts[projectIndex + 1] || !parts[runIndex + 1]) {
    fail('the Bug Fix run URL must identify both a project and a run.');
  }
  return { origin: url.origin, projectId: parts[projectIndex + 1], runId: parts[runIndex + 1] };
}

function parseProjectUrl(value, expectedOrigin) {
  const url = new URL(value);
  const parts = url.pathname.split('/').filter(Boolean);
  const projectIndex = parts.indexOf('projects');
  if (url.origin !== expectedOrigin || projectIndex < 0 || !parts[projectIndex + 1]) {
    fail('the Bug Fix project URL must be on the run origin and identify a project.');
  }
  return parts[projectIndex + 1];
}

function values(value, result = []) {
  if (typeof value === 'string') result.push(value);
  else if (Array.isArray(value)) value.forEach((item) => values(item, result));
  else if (value && typeof value === 'object') Object.values(value).forEach((item) => values(item, result));
  return result;
}

export function resolveBugFixPullRequestEvidence({
  runUrl,
  projectUrl,
  expectedPullRequestUrl,
  topology,
  events,
  project,
}) {
  const run = parseRunUrl(runUrl);
  if (parseProjectUrl(projectUrl, run.origin) !== run.projectId) {
    fail('the Bug Fix project URL does not identify the project that owns the current run.');
  }
  if (!Array.isArray(topology?.nodes) || !topology.nodes.some((node) => (
    node?.id === 'push-pr'
    && node.role === 'action'
    && node.kind === 'live'
    && node.node_type === 'action'
  ))) {
    fail('the current run topology does not contain the live push-pr action.');
  }

  const candidates = new Map();
  for (const value of values(events)) {
    for (const candidate of value.matchAll(/https:\/\/github\.com\/[^\s/]+\/[^\s/]+\/pull\/[1-9][0-9]*\/?/gu)) {
      try {
        const pr = parseGitHubPullRequestUrl(candidate[0]);
        candidates.set(pr.url, pr);
      } catch {
        // Event payloads contain unrelated strings; only canonical PR URLs are evidence.
      }
    }
  }
  if (candidates.size === 0) fail('the current run has not reported a pull request yet.');
  if (candidates.size > 1) fail('the current run reported multiple pull requests; refusing to choose one.');

  const resolved = [...candidates.values()][0];
  const repository = String(project?.source_repository ?? '').replace(/^https:\/\/github\.com\//iu, '').replace(/\.git$/iu, '');
  if (repository !== resolved.repository) {
    fail(`run pull request ${resolved.repository}#${resolved.number} does not belong to project repository ${repository || '(missing)'}.`);
  }
  const supplied = parseGitHubPullRequestUrl(expectedPullRequestUrl);
  if (supplied.url !== resolved.url) {
    fail(`configured pull request ${supplied.repository}#${supplied.number} is stale or does not match this run's ${resolved.repository}#${resolved.number}.`);
  }
  return { ...resolved, runId: run.runId, projectId: run.projectId };
}

export function browserBugFixPullRequestResolverSource() {
  return [
    fail.toString(),
    parseGitHubPullRequestUrl.toString(),
    parseRunUrl.toString(),
    parseProjectUrl.toString(),
    values.toString(),
    resolveBugFixPullRequestEvidence.toString(),
    'resolveBugFixPullRequestEvidence',
  ].join('\n');
}
