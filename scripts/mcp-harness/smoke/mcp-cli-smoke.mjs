#!/usr/bin/env node
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { pathToFileURL } from 'node:url';
import { McpHarnessClient } from '../mcp-client/client.mjs';
import { assertCapabilitiesCompatible, checkCapabilities, loadCapabilitiesContract } from '../lib/capabilities-contract.mjs';
import { classifySmokeStatus } from '../lib/smoke-confirm-gate.mjs';

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
  return result.rawContent || result.error?.message || `protocol error ${result.protocolErrorCode ?? 'unknown'}`;
}

async function callStep(client, report, capability, step, arguments_ = {}) {
  const tool = resolvedTool(report, capability);
  let result;
  try {
    result = await client.callTool(tool, arguments_);
  } catch (error) {
    throw new Error(`${step} failed (${tool}): ${error.message}`, { cause: error });
  }
  if (result.isError) throw new Error(`${step} failed (${tool}): ${resultError(result)}`);
  return result.structuredContent;
}

function projectsFrom(value) {
  if (Array.isArray(value)) return value;
  if (Array.isArray(value?.projects)) return value.projects;
  if (Array.isArray(value?.items)) return value.items;
  return [];
}

function projectIdFrom(value) {
  return value?.id ?? value?.project_id ?? value?.projectId;
}

async function resolveProject(client, report, options) {
  if (options.projectId) return { id: options.projectId, source: 'provided' };

  const listed = projectsFrom(await callStep(client, report, 'list-projects', 'project discovery'));
  const project = options.projectName
    ? listed.find((item) => item?.name === options.projectName)
    : listed[0];
  if (project) {
    const id = projectIdFrom(project);
    if (!id) throw new Error('project discovery failed (project_list): selected project did not contain an id');
    return { id, source: 'reused', name: project.name };
  }

  const name = options.projectName ?? 'agentweaver-mcp-smoke';
  const created = await callStep(client, report, 'create-project', 'project creation', {
    name,
    working_directory: options.workingDirectory ?? '.',
    ...(options.blueprintId ? { blueprint_id: options.blueprintId } : {}),
  });
  const id = projectIdFrom(created);
  if (!id) throw new Error('project creation failed (project_create): response did not contain a project id');
  return { id, source: 'created', name };
}

export async function runSmoke({
  client,
  contract,
  projectId,
  projectName,
  workingDirectory,
  blueprintId,
  goal = 'Create a minimal smoke-test task that produces one small reviewable artifact.',
  timeoutMs = 300_000,
  pollMs = 2_000,
  sleepFn = sleep,
  now = () => Date.now(),
  logger = console,
}) {
  if (timeoutMs > 300_000) throw new Error('configuration failed: --timeout-ms cannot exceed 300000 (5 minutes)');

  const report = checkCapabilities(await client.discoverTools(), contract);
  assertCapabilitiesCompatible(report);
  let runId = null;
  let primaryError = null;
  try {
    const auth = await callStep(client, report, 'auth-status', 'GitHub sign-in check');
    if (auth?.status !== 'signed_in') {
      await callStep(client, report, 'auth-signin', 'GitHub sign-in');
    }

    const project = await resolveProject(client, report, { projectId, projectName, workingDirectory, blueprintId });
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
    };
  } catch (error) {
    primaryError = error;
    throw error;
  } finally {
    if (runId) {
      try {
        await callStep(client, report, 'cleanup-run', 'run cleanup', { run_id: runId });
      } catch (cleanupError) {
        if (!primaryError) throw cleanupError;
        logger.error(`${cleanupError.message}; original failure: ${primaryError.message}`);
      }
    }
  }
}

async function main() {
  if (process.argv.includes('--list')) {
    const { listPersonas } = await import('../../persona-briefs/index.mjs');
    const scenarios = await listPersonas({ surface: 'mcp' });
    process.stdout.write(`${JSON.stringify({ surface: 'mcp', mode: 'persona-adapter', scenarios }, null, 2)}\n`);
    return;
  }

  const baseUrl = process.env.AGENTWEAVER_BASE_URL?.replace(/\/+$/, '');
  const target = arg('--target') ?? (baseUrl ? `${baseUrl}/mcp` : 'stdio');
  const client = await McpHarnessClient.connect({
    target,
    command: arg('--server-command'),
    args: arg('--server-args') ? JSON.parse(arg('--server-args')) : ['--stdio'],
    token: arg('--token') ?? process.env.AGENTWEAVER_TOKEN ?? process.env.GITHUB_TOKEN,
    allowProd: process.argv.includes('--allow-prod'),
    iUnderstandProd: process.argv.includes('--i-understand-prod'),
  });
  try {
    const contract = await loadCapabilitiesContract(path.join(ROOT, 'required-capabilities.json'));
    const result = await runSmoke({
      client,
      contract,
      projectId: arg('--project-id') ?? process.env.AGENTWEAVER_SMOKE_PROJECT_ID,
      projectName: arg('--project-name') ?? process.env.AGENTWEAVER_SMOKE_PROJECT_NAME,
      workingDirectory: arg('--working-directory') ?? process.env.AGENTWEAVER_SMOKE_WORKING_DIRECTORY,
      blueprintId: arg('--blueprint-id') ?? process.env.AGENTWEAVER_SMOKE_BLUEPRINT_ID,
      goal: arg('--goal'),
      timeoutMs: Number(arg('--timeout-ms') ?? 300_000),
      pollMs: Number(arg('--poll-ms') ?? 2_000),
    });
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  } finally {
    await client.close();
  }
}

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    console.error(`CLI→MCP SMOKE FAIL: ${error.message}`);
    process.exitCode = 1;
  });
}
