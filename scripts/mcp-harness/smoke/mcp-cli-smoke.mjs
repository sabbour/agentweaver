#!/usr/bin/env node
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pathToFileURL } from 'node:url';
import { randomUUID } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { McpHarnessClient } from '../mcp-client/client.mjs';
import { assertCapabilitiesCompatible, checkCapabilities, loadCapabilitiesContract } from '../lib/capabilities-contract.mjs';
import { classifySmokeStatus } from '../lib/smoke-confirm-gate.mjs';
import { networkTargetEvidence } from '../../harness-shared/target-guard.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const arg = (name) => {
  const index = process.argv.indexOf(name);
  return index === -1 ? undefined : process.argv[index + 1];
};

function resolvedTool(report, capability) {
  const item = report.results.find((entry) => entry.capability === capability);
  if (!item?.tool) throw new Error(`CONTRACT FAIL: no resolved tool for ${capability}`);
  return item.tool;
}

function resultError(result) {
  return redact(result.rawContent || result.error?.message || `protocol error ${result.protocolErrorCode ?? 'unknown'}`);
}

async function callStep(client, report, capability, step, arguments_ = {}) {
  const tool = resolvedTool(report, capability);
  let result;
  try {
    result = await client.callTool(tool, arguments_);
  } catch (error) {
    throw new Error(`${step} failed (${tool}): ${redact(error.message)}`, { cause: error });
  }
  if (result.isError) throw new Error(`${step} failed (${tool}): ${resultError(result)}`);
  return result.structuredContent;
}

function projectIdFrom(value) {
  return value?.id ?? value?.project_id ?? value?.projectId;
}

async function resolveProject(client, report, options) {
  if (options.projectId) {
    if (!options.projectIsDisposable) {
      throw new Error('configuration failed: --project-id requires --project-is-disposable; supplied projects are never deleted');
    }
    return { id: options.projectId, source: 'provided', owned: false };
  }

  if (options.remote && !options.workingDirectory) {
    throw new Error(
      'configuration failed: remote project creation requires --working-directory or ' +
      'AGENTWEAVER_SMOKE_WORKING_DIRECTORY; use a path valid inside the deployed Agentweaver workspace',
    );
  }
  if (options.remote && (!options.workingDirectory.startsWith('/') || options.workingDirectory.includes('\\'))) {
    throw new Error(
      'configuration failed: remote working directory must be an absolute provider path (for example /workspace/smoke); ' +
      'local Windows paths are never sent to deployed targets',
    );
  }
  const name = `${options.projectName ?? 'agentweaver-mcp-smoke'}-${options.uniqueId()}`;
  const created = await callStep(client, report, 'create-project', 'project creation', {
    name,
    working_directory: options.workingDirectory ?? '.',
    origin: 'blank',
    blueprint_id: options.blueprintId ?? 'blueprint-software-development',
  });
  const id = projectIdFrom(created);
  if (!id) throw new Error('project creation failed (project_create): response did not contain a project id');
  return { id, source: 'created', name, owned: true };
}

export async function runSmoke({
  client,
  contract,
  projectId,
  projectIsDisposable = false,
  projectName,
  workingDirectory,
  blueprintId,
  goal = 'Create a minimal smoke-test task that produces one small reviewable artifact.',
  timeoutMs = 300_000,
  pollMs = 2_000,
  sleepFn = sleep,
  now = () => Date.now(),
  logger = console,
  uniqueId = () => randomUUID().slice(0, 12),
  isCancelled = () => false,
  preflight = networkTargetEvidence('stdio', { surface: 'mcp', authSource: 'none' }),
}) {
  if (timeoutMs > 300_000) throw new Error('configuration failed: --timeout-ms cannot exceed 300000 (5 minutes)');

  const report = checkCapabilities(await client.discoverTools(), contract);
  assertCapabilitiesCompatible(report);
  let runId = null;
  let project = null;
  let primaryError = null;
  const cleanupErrors = [];
  preflight.cleanupIntent = projectId
    ? 'archive-run; retain caller-owned disposable project'
    : 'archive-run; delete harness-created project';
  try {
    project = await resolveProject(client, report, {
      projectId, projectIsDisposable, projectName, workingDirectory, blueprintId, uniqueId,
      remote: preflight.transport === 'http',
    });
    preflight.projectId = project.id;
    const submit = await callStep(client, report, 'submit-run', 'run submission', {
      project_id: project.id,
      task: goal,
    });
    runId = submit?.run_id;
    if (!runId) throw new Error('run submission failed (run_submit): response did not contain run_id');

    const terminal = new Set(['succeeded', 'completed', 'failed', 'cancelled', 'archived']);
    const deadline = now() + timeoutMs;
    let latest = null;
    let confirmed = false;
    while (now() < deadline) {
      if (isCancelled()) throw new Error('smoke cancelled; cleanup will still run');
      latest = await callStep(client, report, 'poll-run', 'run polling', { run_id: runId });
      const action = classifySmokeStatus(latest, { terminal, alreadyConfirmed: confirmed });
      if (action === 'break') break;
      if (action === 'confirm') {
        confirmed = true;
        await callStep(client, report, 'confirm-outcome-spec', 'outcome confirmation', { run_id: runId });
      }
      await sleepFn(pollMs);
    }

    const status = latest?.status?.toLowerCase();
    if (!latest || !terminal.has(status)) throw new Error(`run polling failed (run_status): timed out after ${timeoutMs}ms`);
    if (!['succeeded', 'completed'].includes(status)) {
      throw new Error(`run completion assertion failed (run_status): expected succeeded/completed, got ${status}`);
    }

    const artifacts = await callStep(client, report, 'list-artifacts', 'artifact retrieval', { run_id: runId });
    const files = artifacts?.artifacts;
    if (!Array.isArray(files) || files.length === 0) {
      throw new Error('artifact assertion failed (run_show_artifacts): expected at least one artifact');
    }

    return {
      banner: 'CLI→MCP SMOKE OK',
      runId,
      status,
      artifactCount: files.length,
      project,
      contract: report,
      preflight,
    };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    if (runId) {
      try {
        await callStep(client, report, 'cleanup-run', 'run cleanup', { run_id: runId });
      } catch (cleanupError) {
        cleanupErrors.push(cleanupError);
      }
    }
    if (project?.owned) {
      try {
        await callStep(client, report, 'delete-project', 'project cleanup', { project_id: project.id });
      } catch (cleanupError) {
        cleanupErrors.push(cleanupError);
      }
    }
    preflight.runId = runId;
    preflight.cleanupResult = cleanupErrors.length
      ? `failed: ${cleanupErrors.map((error) => error.message).join('; ')}`
      : 'completed';
    if (cleanupErrors.length) {
      if (primaryError) {
        primaryError.cleanupErrors = cleanupErrors.map((error) => error.message);
        logger.error(`cleanup failure(s): ${primaryError.cleanupErrors.join('; ')}; original failure: ${primaryError.message}`);
      } else {
        const cleanupError = cleanupErrors[0];
        cleanupError.cleanupErrors = cleanupErrors.map((error) => error.message);
        throw cleanupError;
      }
    }
  }
}

export async function finishSmokeLifecycle({
  primaryError,
  client,
  preflight,
  preflightOut,
  mkdirImpl = mkdir,
  writeFileImpl = writeFile,
}) {
  const finalizationErrors = [];
  try {
    await mkdirImpl(path.dirname(preflightOut), { recursive: true });
    await writeFileImpl(preflightOut, `${JSON.stringify(redact(preflight), null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
  } catch (error) {
    finalizationErrors.push(new Error(`preflight evidence write failed: ${error.message}`, { cause: error }));
  } finally {
    try {
      await client?.close();
    } catch (error) {
      finalizationErrors.push(new Error(`MCP client close failed: ${error.message}`, { cause: error }));
    }
  }

  if (primaryError) {
    if (finalizationErrors.length) {
      primaryError.finalizationErrors = finalizationErrors.map((error) => error.message);
    }
    throw primaryError;
  }
  if (finalizationErrors.length === 1) throw finalizationErrors[0];
  if (finalizationErrors.length > 1) {
    throw new AggregateError(finalizationErrors, finalizationErrors.map((error) => error.message).join('; '));
  }
}

async function main() {
  if (process.argv.some((value) => /^--(?:authorization|api[-_]?key|secret|password|to[k]en)(?:=|$)/i.test(value))) {
    throw new Error('configuration failed: authentication material is not accepted in argv; set AGENTWEAVER_TOKEN in the transient process environment');
  }
  if (process.argv.includes('--list')) {
    const { listPersonas } = await import('../../persona-briefs/index.mjs');
    const scenarios = await listPersonas({ surface: 'mcp' });
    process.stdout.write(`${JSON.stringify({ surface: 'mcp', mode: 'persona-adapter', scenarios }, null, 2)}\n`);
    return;
  }

  const baseUrl = process.env.AGENTWEAVER_BASE_URL?.replace(/\/+$/, '');
  const target = arg('--target') ?? (baseUrl ? `${baseUrl}/mcp` : 'stdio');
  const token = process.env.AGENTWEAVER_TOKEN;
  if (target !== 'stdio' && !token) {
    throw new Error('configuration failed: remote MCP smoke requires AGENTWEAVER_TOKEN');
  }
  const tokenSource = process.env.AGENTWEAVER_TOKEN ? 'environment' : 'none';
  const preflight = networkTargetEvidence(target, {
    surface: 'mcp',
    authSource: tokenSource,
    exactPath: target === 'stdio' ? undefined : '/mcp',
  });
  const preflightOut = path.resolve(arg('--preflight-out') ?? path.join(
    ROOT, '..', '..', 'artifacts', 'mcp-harness', `smoke-preflight-${randomUUID()}.json`,
  ));
  let client = null;
  let cancelledSignal = null;
  const onCancel = (signal) => { cancelledSignal = signal; };
  process.once('SIGINT', onCancel);
  process.once('SIGTERM', onCancel);
  let primaryError = null;
  try {
    client = await McpHarnessClient.connect({
      target,
      command: arg('--server-command'),
      args: arg('--server-args') ? JSON.parse(arg('--server-args')) : ['--stdio'],
      token,
    });
    const contract = await loadCapabilitiesContract(path.join(ROOT, 'required-capabilities.json'));
    const result = await runSmoke({
      client,
      contract,
      projectId: arg('--project-id') ?? process.env.AGENTWEAVER_SMOKE_PROJECT_ID,
      projectIsDisposable: process.argv.includes('--project-is-disposable'),
      projectName: arg('--project-name') ?? process.env.AGENTWEAVER_SMOKE_PROJECT_NAME,
      workingDirectory: arg('--working-directory') ?? process.env.AGENTWEAVER_SMOKE_WORKING_DIRECTORY,
      blueprintId: arg('--blueprint-id') ?? process.env.AGENTWEAVER_SMOKE_BLUEPRINT_ID,
      goal: arg('--goal'),
      timeoutMs: Number(arg('--timeout-ms') ?? 300_000),
      pollMs: Number(arg('--poll-ms') ?? 2_000),
      isCancelled: () => cancelledSignal !== null,
      preflight,
    });
    process.stdout.write(`${JSON.stringify(redact(result), null, 2)}\n`);
  } catch (error) {
    primaryError = error;
  } finally {
    process.removeListener('SIGINT', onCancel);
    process.removeListener('SIGTERM', onCancel);
    await finishSmokeLifecycle({ primaryError, client, preflight, preflightOut });
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(`CLI→MCP SMOKE FAIL: ${redact(error.message)}`);
    process.exitCode = 1;
  });
}
