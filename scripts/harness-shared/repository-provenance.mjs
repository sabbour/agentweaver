import { redact } from './redaction.mjs';
import { validateNetworkTarget } from './target-guard.mjs';

const REVISION = /^[0-9a-f]{40,64}$/i;

function failure(code, message, recovery) {
  return redact({ ok: false, failure: { code, message, recovery } });
}

function repositoryIdentity(value) {
  const identity = String(value ?? '').trim().replace(/^https:\/\/github\.com\//i, '').replace(/\.git$/i, '');
  return /^[^/\s]+\/[^/\s]+$/.test(identity) ? identity.toLowerCase() : null;
}

function appUrl(baseUrl, path) {
  const target = validateNetworkTarget(baseUrl);
  return new URL(path, `${target.origin}/`).toString();
}

export function preflightRepositoryScenario({
  baseUrl,
  requestedRepository,
  project,
  workspaceRefs,
  workflowId,
  blueprintId,
  disposableProject = false,
} = {}) {
  const requestedIdentity = repositoryIdentity(requestedRepository);
  if (!requestedIdentity) {
    return failure(
      'requested_repository_missing',
      'A completion-required repository scenario needs an explicit owner/repository target.',
      'Provide the requested repository as owner/repository and rerun setup.',
    );
  }
  if (!project?.project_id) {
    return failure(
      'project_missing',
      'No disposable project was created or selected for the requested repository.',
      'Create a disposable GitHub-origin project, or explicitly select a disposable project connected to the requested repository.',
    );
  }
  if (!disposableProject) {
    return failure(
      'project_not_disposable',
      'The selected project is not confirmed as disposable for this Harness run.',
      'Create a Harness-owned disposable project or explicitly mark the selected test project as disposable.',
    );
  }
  if (project.origin !== 'github') {
    return failure(
      'project_not_repository_backed',
      'The selected project is not GitHub-origin and cannot prove repository execution provenance.',
      'Use the repository-selection flow to create or select a project whose origin is github.',
    );
  }
  const actualIdentity = repositoryIdentity(project.source_repository);
  if (!actualIdentity) {
    return failure(
      'repository_identity_missing',
      'The selected project does not expose source_repository.',
      'Reconnect the requested repository and verify GET /api/projects/{id} returns source_repository.',
    );
  }
  if (actualIdentity !== requestedIdentity) {
    return failure(
      'repository_identity_mismatch',
      `The selected project is connected to ${actualIdentity}, not ${requestedIdentity}.`,
      'Create or select a disposable project connected to the exact requested repository.',
    );
  }
  const baseRef = workspaceRefs?.refs?.find((entry) =>
    entry?.kind === 'base' && entry.branch === (workspaceRefs.current_branch ?? project.default_branch));
  if (!REVISION.test(String(baseRef?.revision ?? ''))) {
    return failure(
      'resolved_revision_missing',
      'The project base ref does not expose an immutable resolved revision.',
      'Refresh the project workspace refs and require the base ref revision before starting orchestration.',
    );
  }
  const workflowOrBlueprintId = String(workflowId ?? blueprintId ?? '').trim();
  if (!workflowOrBlueprintId) {
    return failure(
      'workflow_or_blueprint_missing',
      'No workflow or Blueprint ID was selected for execution.',
      'Select an allowed workflow or the Blueprint applied to the disposable project before starting orchestration.',
    );
  }

  return redact({
    ok: true,
    provenance: {
      projectId: project.project_id,
      projectUrl: appUrl(baseUrl, `/projects/${encodeURIComponent(project.project_id)}`),
      repositoryIdentity: actualIdentity,
      resolvedRevision: baseRef.revision.toLowerCase(),
      workflowOrBlueprintId,
    },
  });
}

export function markRepositoryScenarioRunning(preflight, { baseUrl, orchestrationRunId } = {}) {
  if (!preflight?.ok) return preflight;
  if (!String(orchestrationRunId ?? '').trim()) {
    return failure(
      'orchestration_missing',
      'The scenario has not created an orchestration and cannot be reported as running.',
      'Start the orchestration, capture its run ID, and only then report the scenario as running.',
    );
  }
  const projectId = preflight.provenance.projectId;
  return redact({
    status: 'running',
    ...preflight.provenance,
    orchestrationRunId,
    orchestrationUrl: appUrl(
      baseUrl,
      `/projects/${encodeURIComponent(projectId)}/orchestrations/${encodeURIComponent(orchestrationRunId)}`,
    ),
  });
}
