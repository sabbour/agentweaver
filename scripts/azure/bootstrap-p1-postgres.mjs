#!/usr/bin/env node
/**
 * Runs the approved Identity and P1 PostgreSQL initializers without redeploying Azure resources.
 * The exact target, current source, approved plan, and execution switch are checked first.
 */
import { randomUUID } from 'node:crypto';
import { parseArgs } from 'node:util';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { runAz } from './lib/exec.mjs';
import { resolveSource } from './lib/git.mjs';
import {
  guardAzureTarget,
  readIdentityPostgresBootstrapValues,
} from './lib/guardrails.mjs';
import {
  buildP1PostgresPlan,
  P1_POSTGRES_TARGET,
  p1PostgresPlanHash,
} from './lib/p1-postgres-plan.mjs';
import { bootstrapIdentityPostgres } from './lib/identity-postgres-bootstrap.mjs';

const CORE_MERGE_SHA_PATTERN = /^[0-9a-f]{40}$/;

/**
 * Uses approved plan principals and bootstrap values to run both database stages on one temporary tunnel.
 * Without execute, this returns the source-bound plan and does not call Azure or create temporary resources.
 */
export async function bootstrapP1Postgres({
  repoRoot = process.cwd(),
  coreMergeSha,
  execute = false,
}, {
  execAz = runAz,
  sourceResolver = resolveSource,
  targetGuard = guardAzureTarget,
  readPostgresValues = readIdentityPostgresBootstrapValues,
  initializePostgres = bootstrapIdentityPostgres,
} = {}) {
  if (typeof execute !== 'boolean' || !CORE_MERGE_SHA_PATTERN.test(coreMergeSha ?? '')) {
    throw new Error('P1 PostgreSQL initialization requires an exact Core merge SHA and explicit execution state.');
  }

  const target = P1_POSTGRES_TARGET;
  const source = sourceResolver({
    repoRoot,
    resourceGroup: target.resourceGroup,
    subscriptionId: target.subscriptionId,
    tenantId: target.tenantId,
    template: join(repoRoot, 'infra', 'bicep', 'aks-redeploy.bicep'),
    parametersFile: join(repoRoot, 'infra', 'bicep', 'parameters', 'p0-aks-redeploy.approved.json'),
  });
  const plan = buildP1PostgresPlan();
  const planSha256 = p1PostgresPlanHash(plan);
  const summary = {
    resourceGroup: target.resourceGroup,
    subscriptionId: target.subscriptionId,
    tenantId: target.tenantId,
    postgresHost: target.postgresHost,
    database: target.database,
    sourceSha: source.sha,
    coreMergeSha,
    planSha256,
    roles: plan.services.map(service => ({ service: service.service, role: service.role })),
    executed: false,
  };
  if (!execute) return summary;

  const config = {
    ...target,
    ...source,
    allowedSubscriptionId: target.subscriptionId,
    allowedTenantId: target.tenantId,
  };
  const { execAz: boundAz, group } = targetGuard(config, execAz);
  if (group.tags['agentweaver:owner'] !== source.owner ||
      group.tags['agentweaver:cost-center'] !== source.costCenter) {
    throw new Error('Source parameter ownership differs from the actual target.');
  }
  if (source.postgresEntraAdminObjectId?.toLowerCase() !== target.operatorObjectId.toLowerCase() ||
      source.postgresEntraAdminPrincipalName !== target.operatorName) {
    throw new Error('The approved PostgreSQL administrator differs from the P1 initializer operator.');
  }

  const postgresValues = readPostgresValues(config, boundAz);

  const receiptPath = join(repoRoot, 'Temp', `p1-postgres-${randomUUID().replaceAll('-', '')}.jsonl`);
  const receipt = await initializePostgres({
    repoRoot,
    resourceGroup: target.resourceGroup,
    subscriptionId: target.subscriptionId,
    tenantId: target.tenantId,
    clusterName: target.clusterName,
    postgresHost: postgresValues.resources.postgresHost,
    adminUsername: postgresValues.postgresEntraAdminPrincipalName,
    runtimePrincipalObjectId: postgresValues.identityBrokerRuntimeIdentity.principalObjectId,
    migrationPrincipalObjectId: postgresValues.identityBrokerMigrationIdentity.principalObjectId,
    p1Initializer: {
      sourceSha: source.sha,
      coreMergeSha,
      planSha256,
      receiptPath,
    },
  });
  return { ...summary, executed: true, receipt };
}

const cliOptions = {
  'core-merge-sha': { type: 'string' },
  execute: { type: 'boolean', default: false },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const { values } = parseArgs({ options: cliOptions });
    const result = await bootstrapP1Postgres({
      coreMergeSha: values['core-merge-sha'],
      execute: values.execute,
    });
    console.log(JSON.stringify(result, null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
