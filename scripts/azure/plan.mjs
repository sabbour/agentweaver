#!/usr/bin/env node
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { guardAzureTarget, resolveClusterAdminRoleAssignmentName } from './lib/guardrails.mjs';
import { buildDeployArgs, cliConfig, cliOptions } from './deploy.mjs';

export function buildWhatIfArgs(options) {
  const args = buildDeployArgs(options);
  args[2] = 'what-if';
  args.splice(args.indexOf('--mode'), 2);
  args.splice(args.indexOf('--name'), 2);
  args.push('--no-pretty-print');
  return args;
}

export function plan(config, { execAz = runAz, sourceResolver = resolveSource } = {}) {
  const source = sourceResolver(config);
  const { group, execAz: boundAz } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  const operatorRoleAssignmentName =
    resolveClusterAdminRoleAssignmentName({ ...config, ...source }, boundAz);
  return execAz(buildWhatIfArgs({ resourceGroup: config.resourceGroup, ...source,
    operatorRoleAssignmentName,
    sourceSha: source.sha, subscription: config.subscriptionId }), { check: false });
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const { values } = parseArgs({ options: cliOptions });
    const result = plan(cliConfig(values));
    if (result.stdout) console.log(result.stdout);
    if (result.stderr) console.error(result.stderr);
    process.exitCode = result.status ?? 1;
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
