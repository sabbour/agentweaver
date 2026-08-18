import { createApiFromSession } from './api.mjs';
import { DEFAULT_SESSION_STORAGE_PATH } from './auth.mjs';

const TIMESTAMPED_FIXTURE_SUFFIX = '(?: - [0-9]{8}T[0-9]{6}Z)?';

function escapeRegularExpression(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}

function compileSafeProjectPatterns(patterns, projectName) {
  if (!Array.isArray(patterns) || patterns.length === 0) {
    throw new Error('Scenario fixture cleanup requires at least one explicit safeProjectNamePatterns entry.');
  }
  const escapedProjectName = escapeRegularExpression(projectName);
  const exactPattern = `^${escapedProjectName}$`;
  const timestampedPattern = `^${escapedProjectName}${TIMESTAMPED_FIXTURE_SUFFIX}$`;
  return patterns.map((pattern) => {
    if (pattern !== exactPattern && pattern !== timestampedPattern) {
      throw new Error(
        'Every fixture project-name pattern must match only the declared fixture name, optionally with the deterministic UTC timestamp suffix.',
      );
    }
    return new RegExp(pattern, 'u');
  });
}

export function validateScenarioFixture(fixture) {
  if (!fixture || typeof fixture !== 'object' || Array.isArray(fixture)) {
    throw new Error('The capture plan must declare a fixture object before cleanup can run.');
  }
  if (typeof fixture.projectName !== 'string' || !fixture.projectName.trim()) {
    throw new Error('The capture plan fixture requires projectName.');
  }
  if (!fixture.projectName.trim().startsWith('Agentweaver Demo')) {
    throw new Error('The fixture projectName must use the unmistakable "Agentweaver Demo" ownership marker.');
  }
  const patterns = compileSafeProjectPatterns(fixture.safeProjectNamePatterns, fixture.projectName.trim());
  if (!patterns.some((pattern) => pattern.test(fixture.projectName))) {
    throw new Error('The fixture projectName must match an explicit safe project-name pattern.');
  }
  if (fixture.cleanAllProjects !== undefined) {
    throw new Error('fixture.cleanAllProjects is not permitted; cleanup is limited to explicit demo fixtures.');
  }
  return { ...fixture, projectName: fixture.projectName.trim(), patterns };
}

export function isScenarioFixtureProject(project, fixture) {
  const validated = fixture.patterns ? fixture : validateScenarioFixture(fixture);
  return typeof project?.name === 'string'
    && validated.patterns.some((pattern) => pattern.test(project.name));
}

function preflightError(message) {
  throw new Error(`Demo capture preflight: ${message}`);
}

function resolvePlaceholders(value, environment) {
  if (typeof value === 'string') {
    return value.replace(/\{\{([A-Z][A-Z0-9_]+)\}\}/gu, (_match, name) => {
      const resolved = environment[name];
      if (typeof resolved !== 'string' || !resolved.trim()) {
        preflightError(`set ${name} to the real, pre-created GitHub artifact URL before preparing this beat.`);
      }
      return resolved.trim();
    });
  }
  if (Array.isArray(value)) return value.map((item) => resolvePlaceholders(item, environment));
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, resolvePlaceholders(item, environment)]));
  }
  return value;
}

function validateExternalUrl(name, value, requirement) {
  let url;
  try { url = new URL(value); } catch { preflightError(`${name} must be an absolute HTTPS URL.`); }
  if (url.protocol !== 'https:') preflightError(`${name} must be an HTTPS URL.`);
  if (requirement.host && url.hostname !== requirement.host) preflightError(`${name} must point to ${requirement.host}, not ${url.hostname}.`);
  if (requirement.origin && url.origin !== requirement.origin) preflightError(`${name} must point to ${requirement.origin}, not ${url.origin}.`);
}

function isSelected(requirement, selected) {
  return requirement?.beats?.some((beatId) => selected.has(beatId));
}

function currentFixtureProject(projects, fixture) {
  const matches = projects.filter((project) => project?.name === fixture?.projectName);
  if (matches.length !== 1 || typeof matches[0]?.project_id !== 'string') {
    preflightError(`expected exactly one active fixture named "${fixture?.projectName}" before resolving the Bug Fix pull request; found ${matches.length}.`);
  }
  return matches[0];
}

function isCurrentBugFixRun(run) {
  return run?.status === 'awaiting_review'
    && typeof run?.workflow_selection_reason === 'string'
    && /\bbug(?:\s|-)+fix\b/iu.test(run.workflow_selection_reason);
}

function pullRequestUrlsFromTopology(events) {
  const urls = new Set();
  for (const event of events) {
    if (event?.type !== 'workflow.step' || event?.payload?.status !== 'completed') continue;
    const message = event.payload.message;
    if (typeof message !== 'string') continue;
    for (const match of message.matchAll(/https:\/\/github\.com\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/[1-9][0-9]*/gu)) {
      urls.add(match[0]);
    }
  }
  return [...urls];
}

async function resolveCurrentBugFixPullRequest(config, api, fetchImpl) {
  if (!api) preflightError('cannot resolve the current Bug Fix pull request without the authenticated Agentweaver run API.');
  const project = currentFixtureProject(await api.listAllProjects(), config.fixture);
  const runSummaries = await api.listAllProjectRuns(project.project_id);
  const inspectedRuns = await Promise.all(runSummaries.map(async (summary) => api.getRun(summary.execution_id)));
  const currentRuns = inspectedRuns.filter(isCurrentBugFixRun);
  if (currentRuns.length !== 1) {
    preflightError(`expected exactly one current Bug Fix run awaiting review; found ${currentRuns.length}. Refusing to use a stale or unrelated pull request.`);
  }

  const run = currentRuns[0];
  if (typeof run.worktree_branch !== 'string' || !run.worktree_branch) {
    preflightError(`current Bug Fix run ${run.run_id} has no worktree branch to attest its pull request.`);
  }
  const urls = pullRequestUrlsFromTopology(await api.getRunEvents(run.run_id));
  if (urls.length !== 1) {
    preflightError(`current Bug Fix run ${run.run_id} has ${urls.length} pull-request identities in its topology artifact; expected exactly one.`);
  }

  const artifactUrl = new URL(urls[0]);
  const [, owner, repository, number] = artifactUrl.pathname.match(/^\/([^/]+)\/([^/]+)\/pull\/([1-9][0-9]*)$/u) ?? [];
  if (!owner || !repository || !number) {
    preflightError(`current Bug Fix run ${run.run_id} emitted an invalid pull-request identity.`);
  }
  let response;
  try {
    response = await fetchImpl(`https://api.github.com/repos/${owner}/${repository}/pulls/${number}`, {
      headers: { Accept: 'application/vnd.github+json' },
    });
  } catch {
    preflightError(`could not verify the pull request emitted by current Bug Fix run ${run.run_id} on GitHub.`);
  }
  if (!response?.ok) {
    preflightError(`the pull request emitted by current Bug Fix run ${run.run_id} does not exist or is not readable on GitHub.`);
  }
  const pullRequest = await response.json().catch(() => null);
  if (
    pullRequest?.number !== Number(number)
    || pullRequest?.html_url !== urls[0]
    || pullRequest?.state !== 'open'
    || pullRequest?.head?.ref !== run.worktree_branch
  ) {
    preflightError(`the pull request emitted by current Bug Fix run ${run.run_id} is stale, closed, or belongs to another run.`);
  }
  return urls[0];
}

export async function resolveCapturePreflight(
  config,
  selectedBeatIds,
  environment = process.env,
  { api, fetchImpl = fetch } = {},
) {
  const selected = new Set(selectedBeatIds);
  const values = { ...environment };
  for (const requirement of (config.preflight?.externalArtifacts ?? [])) {
    if (!isSelected(requirement, selected)) continue;
    const value = environment[requirement.environment];
    if (typeof value !== 'string' || !value.trim()) {
      preflightError(`${requirement.environment} is required for beat ${requirement.beats.join(', ')}. ${requirement.instruction}`);
    }
    validateExternalUrl(requirement.environment, value.trim(), requirement);
  }
  const pullRequestRequirement = config.preflight?.pullRequest;
  if (isSelected(pullRequestRequirement, selected)) {
    values.AGENTWEAVER_DEMO_GITHUB_BUGFIX_PR_URL = await resolveCurrentBugFixPullRequest(config, api, fetchImpl);
  }
  return resolvePlaceholders(config, values);
}

export async function verifyFixtureWorkflowRequirements(options, dependencies = {}) {
  const workflowIds = options.workflowIds ?? [];
  if (workflowIds.length === 0) return;
  const fixture = validateScenarioFixture(options.fixture);
  const api = dependencies.api ?? await createApiFromSession({ baseUrl: options.baseUrl, sessionStoragePath: options.sessionStoragePath ?? DEFAULT_SESSION_STORAGE_PATH, fetchImpl: dependencies.fetchImpl });
  const projects = await api.listAllProjects();
  const matches = projects.filter((project) => project?.name === fixture.projectName);
  if (matches.length !== 1 || typeof matches[0]?.project_id !== 'string') {
    preflightError(`expected exactly one active fixture named "${fixture.projectName}" after beat 2.x; found ${matches.length}.`);
  }
  const response = await api.listProjectWorkflows(matches[0].project_id);
  const foundIds = new Set((response?.workflows ?? []).map((workflow) => workflow?.id).filter((id) => typeof id === 'string'));
  const missing = workflowIds.filter((id) => !foundIds.has(id));
  if (missing.length) {
    preflightError(`fixture "${fixture.projectName}" is missing required project workflows: ${missing.join(', ')}. Create these project-owned workflows during the 2.x setup; built-in workflows are read-only and cannot be scheduled or event-configured.`);
  }
}

export async function cleanScenarioFixtures(options, dependencies = {}) {
  const fixture = validateScenarioFixture(options.fixture);
  const api = dependencies.api ?? await createApiFromSession({
    baseUrl: options.baseUrl,
    sessionStoragePath: options.sessionStoragePath ?? DEFAULT_SESSION_STORAGE_PATH,
    fetchImpl: dependencies.fetchImpl,
  });

  const beforeProjects = await api.listAllProjects();
  const matchedProjects = beforeProjects.filter((project) => isScenarioFixtureProject(project, fixture));
  if (matchedProjects.some((project) => typeof project.project_id !== 'string' || !project.project_id)) {
    throw new Error('Scenario fixture cleanup refused a matched project without a valid project_id.');
  }
  const removed = [];
  for (const project of matchedProjects) {
    const sessions = await api.listAllProjectSessions(project.project_id);
    await api.deleteProject(project.project_id);
    removed.push({
      projectId: project.project_id,
      name: project.name,
      sessionCount: sessions.length,
    });
  }

  const afterProjects = await api.listAllProjects();
  const remaining = afterProjects.filter((project) => isScenarioFixtureProject(project, fixture));
  if (remaining.length > 0) {
    throw new Error(`Scenario fixture preflight could not verify a clean state; ${remaining.length} project(s) remain.`);
  }

  return {
    projectName: fixture.projectName,
    safeProjectNamePatterns: [...options.fixture.safeProjectNamePatterns],
    discoveredProjectCount: matchedProjects.length,
    discoveredSessionCount: removed.reduce((sum, project) => sum + project.sessionCount, 0),
    removed,
    remainingProjectCount: 0,
    clean: true,
  };
}
