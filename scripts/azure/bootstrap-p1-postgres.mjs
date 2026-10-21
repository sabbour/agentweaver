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
const MODES = new Set(['initialize', 'inspect', 'apply-pending', 'apply-pending-dry-run']);
const isVersion = value => Number.isSafeInteger(value) && value > 0 && value < 10_000;

/** Maps CLI switches to one mode, rejecting ambiguous or partial combinations. */
export function p1PostgresMode(values) {
  if (values.inspect && (values['apply-pending-migrations'] || values['dry-run'])) {
    throw new Error('Use either --inspect or --apply-pending-migrations.');
  }
  if (values.inspect) return 'inspect';
  if (values['apply-pending-migrations']) return values['dry-run'] ? 'apply-pending-dry-run' : 'apply-pending';
  if (values['dry-run']) throw new Error('--dry-run applies only to --apply-pending-migrations.');
  return 'initialize';
}

function parseVersion(value) {
  if (value === undefined) return undefined;
  if (!/^[1-9][0-9]{0,3}$/.test(value)) throw new Error('Maximum migration versions must be positive integers.');
  return Number(value);
}

/**
 * Uses approved plan principals and bootstrap values to run both database stages on one temporary tunnel.
 * Without execute, this returns the source-bound plan and does not call Azure or create temporary resources.
 * Mode initialize bootstraps Identity Broker and fresh-initializes an empty database. Mode inspect and the
 * apply-pending modes target an already initialized database: Identity Broker is verified read-only, and
 * apply-pending applies only planned Core/Events versions up to the explicit approved maxima.
 */
export async function bootstrapP1Postgres({
  repoRoot = process.cwd(),
  coreMergeSha,
  execute = false,
  mode = 'initialize',
  maxCoreVersion,
  maxEventsVersion,
}, {
  execAz = runAz,
  sourceResolver = resolveSource,
  targetGuard = guardAzureTarget,
  readPostgresValues = readIdentityPostgresBootstrapValues,
  initializePostgres = bootstrapIdentityPostgres,
} = {}) {
  if (typeof execute !== 'boolean' || !MODES.has(mode)) {
    throw new Error('P1 PostgreSQL work requires a supported mode and explicit execution state.');
  }
  const pending = mode.startsWith('apply-pending');
  if (mode === 'inspect' ? coreMergeSha !== undefined : !CORE_MERGE_SHA_PATTERN.test(coreMergeSha ?? '')) {
    throw new Error('P1 PostgreSQL initialization and continuation require an exact Core merge SHA; inspection takes none.');
  }
  if (pending ? !isVersion(maxCoreVersion) || !isVersion(maxEventsVersion)
    : maxCoreVersion !== undefined || maxEventsVersion !== undefined) {
    throw new Error('Only pending-migration continuation takes, and requires, explicit maximum Core and Events versions.');
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
    mode,
    ...(pending ? { maxCoreVersion, maxEventsVersion } : {}),
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

  const writesReceipt = mode === 'initialize' || mode === 'apply-pending';
  const receiptPath = writesReceipt
    ? join(repoRoot, 'Temp', `p1-postgres-${randomUUID().replaceAll('-', '')}.jsonl`) : undefined;
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
    // Never re-bootstrap Identity Broker when the database is already initialized.
    verifyOnly: mode !== 'initialize',
    p1Initializer: {
      ...(mode === 'initialize' ? {} : { mode }),
      sourceSha: source.sha,
      ...(coreMergeSha === undefined ? {} : { coreMergeSha }),
      planSha256,
      ...(receiptPath ? { receiptPath } : {}),
      ...(pending ? { maxCoreVersion, maxEventsVersion } : {}),
    },
  });
  return { ...summary, executed: true, receipt };
}

const cliOptions = {
  'core-merge-sha': { type: 'string' },
  execute: { type: 'boolean', default: false },
  inspect: { type: 'boolean', default: false },
  'apply-pending-migrations': { type: 'boolean', default: false },
  'dry-run': { type: 'boolean', default: false },
  'max-core-version': { type: 'string' },
  'max-events-version': { type: 'string' },
};

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    const { values } = parseArgs({ options: cliOptions });
    const result = await bootstrapP1Postgres({
      coreMergeSha: values['core-merge-sha'],
      execute: values.execute,
      mode: p1PostgresMode(values),
      maxCoreVersion: parseVersion(values['max-core-version']),
      maxEventsVersion: parseVersion(values['max-events-version']),
    });
    console.log(JSON.stringify(result, null, 2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
