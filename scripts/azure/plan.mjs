#!/usr/bin/env node
/**
 * Read-only Azure planning entry point for the deployment configuration.
 * It resolves the clean source and target before requesting Azure's what-if result.
 */
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { guardAzureTarget } from './lib/guardrails.mjs';
import { buildDeployArgs, cliConfig, cliOptions } from './deploy.mjs';

/** Converts deployment arguments into the non-mutating Azure what-if command form. */
export function buildWhatIfArgs(options) {
  const args = buildDeployArgs(options);
  args[2] = 'what-if';
  args.splice(args.indexOf('--mode'), 2);
  args.splice(args.indexOf('--name'), 2);
  args.push('--no-pretty-print');
  return args;
}

/**
 * Runs an Azure what-if plan after checking source ancestry, ownership, and the dedicated target.
 * Returns the CLI result; target or source validation failures throw before the what-if call.
 */
export function plan(config, { execAz = runAz, sourceResolver = resolveSource } = {}) {
  const source = sourceResolver(config);
  const { group } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  return execAz(buildWhatIfArgs({ resourceGroup: config.resourceGroup, ...source,
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
