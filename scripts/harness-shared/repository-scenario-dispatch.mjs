#!/usr/bin/env node
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { buildSetupFailureVerdict } from '../harness-judge/core.mjs';
import { writeLifecycleJson } from './persona-lifecycle.mjs';
import {
  markRepositoryScenarioRunning,
  preflightRepositoryScenario,
} from './repository-provenance.mjs';
import { redact } from './redaction.mjs';

function setupFailure(metadata, result) {
  return {
    action: 'stop',
    status: 'setup-failed',
    verdict: buildSetupFailureVerdict(metadata, result.failure),
  };
}

function boundaryFailure(code, message, recovery) {
  return { ok: false, failure: { code, message, recovery } };
}

export async function prepareRepositoryScenarioDispatch({
  metadata,
  repository,
  verdictPath,
} = {}, dependencies = {}) {
  const persist = dependencies.writeJson ?? writeLifecycleJson;
  let preflight;
  try {
    preflight = preflightRepositoryScenario(repository);
  } catch {
    preflight = boundaryFailure(
      'repository_preflight_error',
      'Repository provenance could not be validated.',
      'Verify the target base URL and repository setup inputs, then rerun setup.',
    );
  }
  if (!preflight.ok) {
    const stopped = setupFailure(metadata, preflight);
    if (!verdictPath) {
      throw new Error('verdictPath is required when repository scenario setup fails');
    }
    await persist(verdictPath, stopped.verdict);
    return redact({ ...stopped, verdictPath });
  }

  return redact({
    action: 'create-orchestration',
    status: 'ready',
    repositoryPreflight: preflight,
  });
}

export async function markRepositoryScenarioDispatchRunning({
  metadata,
  repository,
  orchestrationRunId,
  verdictPath,
} = {}, dependencies = {}) {
  const persist = dependencies.writeJson ?? writeLifecycleJson;
  let running;
  try {
    const preflight = preflightRepositoryScenario(repository);
    running = markRepositoryScenarioRunning(preflight, {
      baseUrl: repository?.baseUrl,
      orchestrationRunId,
    });
  } catch {
    running = boundaryFailure(
      'repository_running_evidence_error',
      'Repository running evidence could not be created.',
      'Verify the target base URL and orchestration result, then rerun setup.',
    );
  }
  if (running?.status !== 'running') {
    const failure = running?.ok === false
      ? running
      : boundaryFailure(
        'repository_preflight_missing',
        'The canonical repository inputs could not be validated.',
        'Refresh the project, workspace refs, and workflow inputs before retrying dispatch.',
      );
    const stopped = setupFailure(metadata, failure);
    if (!verdictPath) {
      throw new Error('verdictPath is required when repository scenario setup fails');
    }
    await persist(verdictPath, stopped.verdict);
    return redact({ ...stopped, verdictPath });
  }
  return redact({
    action: 'dispatch-persona',
    status: 'running',
    repositoryProvenance: running,
  });
}

function parseArgs(argv) {
  const inputIndex = argv.indexOf('--input');
  if (inputIndex < 0 || !argv[inputIndex + 1]) {
    throw new Error('usage: node repository-scenario-dispatch.mjs --input <request.json>');
  }
  return { input: argv[inputIndex + 1] };
}

export async function main(argv = process.argv.slice(2)) {
  const { input } = parseArgs(argv);
  const request = JSON.parse(await readFile(path.resolve(input), 'utf8'));
  const result = request.phase === 'running'
    ? await markRepositoryScenarioDispatchRunning(request)
    : await prepareRepositoryScenarioDispatch(request);
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  return result.action === 'stop' ? 1 : 0;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().then(
    (code) => { process.exitCode = code; },
    (error) => {
      console.error(redact(String(error?.stack ?? error?.message ?? error)));
      process.exitCode = 2;
    },
  );
}
