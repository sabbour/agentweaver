#!/usr/bin/env node
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpHarnessClient } from '../mcp-client/client.mjs';
import { assertCapabilitiesCompatible, checkCapabilities, loadCapabilitiesContract } from '../lib/capabilities-contract.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const arg = (name) => {
  const index = process.argv.indexOf(name);
  return index === -1 ? undefined : process.argv[index + 1];
};
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function resolvedTool(report, capability) {
  const item = report.results.find((entry) => entry.capability === capability);
  if (!item?.tool) throw new Error(`CONTRACT FAIL: no resolved tool for ${capability}`);
  return item.tool;
}

// `--list` is a no-connect catalog print (the reviewed persona IDs that have MCP
// adapters). Short-circuit before touching any transport so it works offline, matching
// the documented behavior and run-persona.mjs --list.
if (process.argv.includes('--list')) {
  const { listPersonas } = await import('../../persona-briefs/index.mjs');
  const scenarios = await listPersonas({ surface: 'mcp' });
  process.stdout.write(`${JSON.stringify({ surface: 'mcp', mode: 'persona-adapter', scenarios }, null, 2)}\n`);
  process.exit(0);
}

const target = arg('--target') ?? 'stdio';
const client = await McpHarnessClient.connect({
  target,
  command: arg('--server-command'),
  args: arg('--server-args') ? JSON.parse(arg('--server-args')) : ['--stdio'],
  token: arg('--token') ?? process.env.AGENTWEAVER_TOKEN,
  allowProd: process.argv.includes('--allow-prod'),
  iUnderstandProd: process.argv.includes('--i-understand-prod'),
});
let runId = null;
try {
  const contract = await loadCapabilitiesContract(path.join(ROOT, 'required-capabilities.json'));
  const report = checkCapabilities(await client.discoverTools(), contract);
  assertCapabilitiesCompatible(report);
  const projectId = arg('--project-id');
  if (!projectId) throw new Error('Smoke requires --project-id (project creation is scenario-owned)');
  const submit = await client.callTool(resolvedTool(report, 'submit-run'), {
    project_id: projectId, task: arg('--goal') ?? 'Create a minimal smoke-test task and stop at the reviewable result.',
  });
  if (submit.isError) throw new Error(`submit-run failed: ${submit.rawContent}`);
  runId = submit.structuredContent?.run_id;
  if (!runId) throw new Error('submit-run result did not contain run_id');
  const timeoutMs = Number(arg('--timeout-ms') ?? 300_000);
  const pollMs = Number(arg('--poll-ms') ?? 2_000);
  const terminal = new Set(['completed', 'failed', 'cancelled', 'archived']);
  let latest = null;
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    latest = await client.callTool(resolvedTool(report, 'poll-run'), { run_id: runId });
    if (latest.isError) throw new Error(`poll-run failed: ${latest.rawContent}`);
    if (terminal.has(String(latest.structuredContent?.status ?? '').toLowerCase())) break;
    await sleep(pollMs);
  }
  if (!latest || !terminal.has(String(latest.structuredContent?.status ?? '').toLowerCase())) {
    throw new Error(`poll-run timed out after ${timeoutMs}ms`);
  }
  const artifacts = await client.callTool(resolvedTool(report, 'list-artifacts'), { run_id: runId });
  if (artifacts.isError) throw new Error(`list-artifacts failed: ${artifacts.rawContent}`);
  const files = artifacts.structuredContent?.artifacts;
  if (!Array.isArray(files) || files.length === 0) throw new Error('list-artifacts result did not contain at least one artifact');
  process.stdout.write(`${JSON.stringify({ banner: 'DRIVE+CAPTURE OK', runId, status: latest.structuredContent.status, artifactCount: files.length, contract: report }, null, 2)}\n`);
} finally {
  if (runId) {
    try {
      const contract = await loadCapabilitiesContract(path.join(ROOT, 'required-capabilities.json'));
      const report = checkCapabilities(await client.discoverTools(), contract);
      if (report.ok) await client.callTool(resolvedTool(report, 'cleanup-run'), { run_id: runId });
    } catch (error) {
      console.error(`cleanup-run could not be completed for ${runId}: ${error.message}`);
    }
  }
  await client.close();
}
