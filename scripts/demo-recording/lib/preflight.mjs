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
