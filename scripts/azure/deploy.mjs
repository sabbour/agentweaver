#!/usr/bin/env node
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import { assertDedicatedTarget, assertSubscription, assertTenant, guardAzureTarget, readFoundationOutputs } from './lib/guardrails.mjs';

export function buildDeployArgs({ resourceGroup, template, parametersFile, deploymentName, subscription, sourceSha, sourceTree, sourceHash }) {
  assertDedicatedTarget(resourceGroup);
  if (!subscription || !/^[0-9a-f]{40}$/.test(sourceSha ?? '') || !/^[0-9a-f]{40}$/.test(sourceTree ?? '') ||
      !/^[0-9a-f]{64}$/.test(sourceHash ?? '')) {
    throw new Error('Exact source receipt and explicit subscription are required.');
  }
  return ['deployment', 'group', 'create', '--resource-group', resourceGroup, '--template-file', template,
    '--parameters', `@${parametersFile}`, `sourceSha=${sourceSha}`, `sourceTree=${sourceTree}`, `sourceHash=${sourceHash}`,
    '--mode', 'Incremental', '--name', deploymentName, '--subscription', subscription, '-o', 'json'];
}

export function deploy(config, { execAz = runAz, sourceResolver = resolveSource } = {}) {
  assertDedicatedTarget(config.resourceGroup);
  assertSubscription(config.subscriptionId, config.allowedSubscriptionId);
  assertTenant(config.tenantId, config.allowedTenantId);
  const source = sourceResolver(config);
  const deploymentName = `${config.resourceGroup}-${source.sha.slice(0, 12)}`;
  const args = buildDeployArgs({ resourceGroup: config.resourceGroup, ...source,
    subscription: config.subscriptionId, sourceSha: source.sha, deploymentName });
  const summary = { ...source, resourceGroup: config.resourceGroup, deploymentName, args, executed: false };
  if (!config.execute) return summary;
  const { execAz: boundAz, group } = guardAzureTarget({ ...config, ...source }, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner || group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  // A guarded what-if precedes create. Failure never admits a mutation.
  const whatIf = boundAz(['deployment', 'group', 'what-if', '--resource-group', config.resourceGroup,
    '--template-file', source.template, '--parameters', `@${source.parametersFile}`,
    `sourceSha=${source.sha}`, `sourceTree=${source.sourceTree}`, `sourceHash=${source.sourceHash}`, '--no-pretty-print'], { check: false });
  if (whatIf.status !== 0) throw new Error(`What-if failed: ${whatIf.stderr}`);
  const confirmedSource = sourceResolver(config);
  if (confirmedSource.sha !== source.sha || confirmedSource.sourceTree !== source.sourceTree || confirmedSource.sourceHash !== source.sourceHash ||
      confirmedSource.template !== source.template || confirmedSource.parametersFile !== source.parametersFile) {
    throw new Error('Source inputs changed after what-if; refusing deployment.');
  }
  const result = execAz(args, { check: false });
  if (result.status !== 0) throw new Error(`Infrastructure deployment failed: ${result.stderr}`);
  const deployment = JSON.parse(result.stdout);
  const outputs = deployment.properties?.outputs;
  const deploymentId = `/subscriptions/${config.subscriptionId}/resourceGroups/${config.resourceGroup}/providers/Microsoft.Resources/deployments/${deploymentName}`;
  if (deployment.properties?.provisioningState !== 'Succeeded' ||
      deployment.id?.toLowerCase() !== deploymentId.toLowerCase() ||
      outputs?.sourceSha?.value !== source.sha || outputs?.sourceTree?.value !== source.sourceTree ||
      outputs?.sourceHash?.value !== source.sourceHash) {
    throw new Error('Deployment did not return a successful source-bound infrastructure receipt.');
  }
  return { ...summary, executed: true, receipt: { scope: source.scope, sourceSha: source.sha,
    sourceTree: source.sourceTree, sourceHash: source.sourceHash, subscriptionId: config.subscriptionId, tenantId: config.tenantId,
    resourceGroup: config.resourceGroup, deploymentName, deploymentId: deployment.id,
    ...readFoundationOutputs(outputs, config) } };
}

export function cliConfig(values) {
  return { resourceGroup: values['resource-group'], template: values.template, parametersFile: values.parameters,
    repoRoot: process.cwd(), execute: values.execute, subscriptionId: values.subscription,
    allowedSubscriptionId: values['allowed-subscription'], tenantId: values.tenant, allowedTenantId: values['allowed-tenant'] };
}

export const cliOptions = {
  'resource-group': { type: 'string' }, template: { type: 'string', default: 'infra/bicep/main.bicep' },
  parameters: { type: 'string' }, subscription: { type: 'string' }, 'allowed-subscription': { type: 'string' },
  tenant: { type: 'string' }, 'allowed-tenant': { type: 'string' }, execute: { type: 'boolean', default: false },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const { values } = parseArgs({ options: cliOptions });
    console.log(JSON.stringify(deploy(cliConfig(values)), null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
