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

// Keep these declarations aligned with the AKS redeploy federation module's default services.
const AKS_ONLY_FEDERATED_CREDENTIALS = [
  {
    operation: 'createOrUpdate',
    managedIdentityName: 'agentweaver-id-foundation-probe',
    federatedCredentialName: 'foundation-probe-workload-identity',
    subject: 'system:serviceaccount:agentweaver:foundation-probe',
    audiences: ['api://AzureADTokenExchange'],
  },
  {
    operation: 'createOrUpdate',
    managedIdentityName: 'agentweaver-id-identity-broker',
    federatedCredentialName: 'identity-broker-workload-identity',
    subject: 'system:serviceaccount:agentweaver:identity-broker',
    audiences: ['api://AzureADTokenExchange'],
  },
  {
    operation: 'createOrUpdate',
    managedIdentityName: 'agentweaver-id-identity-broker-migration',
    federatedCredentialName: 'identity-broker-migration-workload-identity',
    subject: 'system:serviceaccount:agentweaver:identity-broker-migration',
    audiences: ['api://AzureADTokenExchange'],
  },
];

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
 * Adds nested AKS federation declarations to a successful what-if result.
 * This reports Bicep create-or-update operations without querying existing FIC state.
 */
function addNestedDeploymentChanges(result, source) {
  if (source.scope !== 'aks-only' || result.status !== 0) return result;

  let whatIf;
  try {
    whatIf = JSON.parse(result.stdout);
  } catch (error) {
    throw new Error('AKS-only what-if returned malformed JSON.', { cause: error });
  }
  if (!whatIf || typeof whatIf !== 'object' || Array.isArray(whatIf)) {
    throw new Error('AKS-only what-if returned malformed JSON.');
  }
  return {
    ...result,
    stdout: JSON.stringify({
      ...whatIf,
      nestedDeploymentChanges: {
        note: 'The top-level what-if omits resources in the nested workload-identity federation ' +
          'deployment. These declarations are create-or-update operations; existing FIC state was not queried.',
        federatedIdentityCredentials: AKS_ONLY_FEDERATED_CREDENTIALS,
      },
    }, null, 2),
  };
}

/**
 * Runs an Azure what-if plan after checking source ancestry, ownership, and the dedicated target.
 * Returns the CLI result, with nested AKS federation declarations added for AKS-only plans.
 * Target or source validation failures throw before the what-if call.
 */
export function plan(config, { execAz = runAz, sourceResolver = resolveSource } = {}) {
  if (config.identityRoutingOnly) {
    throw new Error('--identity-routing-only is only supported by deploy.mjs.');
  }
  const source = sourceResolver(config);
  const { group } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  const result = execAz(buildWhatIfArgs({ resourceGroup: config.resourceGroup, ...source,
    sourceSha: source.sha, subscription: config.subscriptionId }), { check: false });
  return addNestedDeploymentChanges(result, source);
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
