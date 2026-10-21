import assert from 'node:assert/strict';
import test from 'node:test';
import { basename, dirname, resolve } from 'node:path';
import {
  bootstrapP1Postgres,
  p1PostgresMode,
} from '../bootstrap-p1-postgres.mjs';
import {
  buildP1PostgresPlan,
  P1_POSTGRES_TARGET,
  p1PostgresPlanHash,
} from '../lib/p1-postgres-plan.mjs';

const coreMergeSha = 'b'.repeat(40);
const repoRoot = resolve('agentweaver-test-root');
const source = {
  sha: 'a'.repeat(40),
  location: 'eastus2euap',
  owner: 'reviewed-owner',
  costCenter: 'reviewed-cost-center',
  keyVaultName: 'agentweaver-kv-v1',
  postgresEntraAdminObjectId: P1_POSTGRES_TARGET.operatorObjectId,
  postgresEntraAdminPrincipalName: P1_POSTGRES_TARGET.operatorName,
};

function metadata() {
  return {
    resources: { postgresHost: P1_POSTGRES_TARGET.postgresHost },
    postgresEntraAdminPrincipalName: P1_POSTGRES_TARGET.operatorName,
    identityBrokerRuntimeIdentity: { principalObjectId: 'c'.repeat(8) + '-cccc-4ccc-8ccc-' + 'c'.repeat(12) },
    identityBrokerMigrationIdentity: { principalObjectId: 'd'.repeat(8) + '-dddd-4ddd-8ddd-' + 'd'.repeat(12) },
  };
}

function sourceResolver(config) {
  assert.equal(config.resourceGroup, P1_POSTGRES_TARGET.resourceGroup);
  assert.equal(config.subscriptionId, P1_POSTGRES_TARGET.subscriptionId);
  assert.equal(config.tenantId, P1_POSTGRES_TARGET.tenantId);
  assert.match(config.template, /aks-redeploy\.bicep$/);
  assert.match(config.parametersFile, /p0-aks-redeploy\.approved\.json$/);
  return source;
}

/** The offline mode describes the exact plan and must not query Azure or create a tunnel. */
test('P1 PostgreSQL summary mode is source-only and makes no Azure calls', async () => {
  const result = await bootstrapP1Postgres({
    repoRoot,
    coreMergeSha,
  }, {
    sourceResolver,
    execAz() { assert.fail('Offline summary must not call Azure.'); },
    targetGuard() { assert.fail('Offline summary must not inspect Azure.'); },
    initializePostgres() { assert.fail('Offline summary must not create a tunnel.'); },
  });

  assert.equal(result.executed, false);
  assert.equal(result.resourceGroup, 'agentweaver');
  assert.equal(result.postgresHost, 'agentweaver-pg.postgres.database.azure.com');
  assert.equal(result.planSha256, p1PostgresPlanHash());
  assert.deepEqual(result.roles, buildP1PostgresPlan().services.map(({ service, role }) => ({ service, role })));
});

/** Execution uses the reviewed target, source, plan, and values needed by PostgreSQL bootstrap. */
test('P1 PostgreSQL execution verifies the target and uses only required bootstrap values', async () => {
  const calls = [];
  let initializerInput;
  const result = await bootstrapP1Postgres({
    repoRoot,
    coreMergeSha,
    execute: true,
  }, {
    sourceResolver,
    execAz() { assert.fail('Target guard must provide the subscription-bound Azure client.'); },
    targetGuard(config, execAz) {
      assert.equal(config.resourceGroup, 'agentweaver');
      assert.equal(config.allowedSubscriptionId, P1_POSTGRES_TARGET.subscriptionId);
      assert.equal(config.allowedTenantId, P1_POSTGRES_TARGET.tenantId);
      assert.equal(config.keyVaultName, 'agentweaver-kv-v1');
      assert.equal(typeof execAz, 'function');
      return {
        group: { tags: { 'agentweaver:owner': source.owner, 'agentweaver:cost-center': source.costCenter } },
        execAz: args => {
          calls.push(args);
          const name = args[args.indexOf('--name') + 1];
          const service = buildP1PostgresPlan().services.find(item =>
            name === `agentweaver-id-${item.service}`);
          return {
            status: 0,
            stderr: '',
            stdout: JSON.stringify({
              id: `/subscriptions/${P1_POSTGRES_TARGET.subscriptionId}/resourceGroups/agentweaver/providers/Microsoft.ManagedIdentity/userAssignedIdentities/${name}`,
              name,
              location: source.location,
              principalId: service.principalObjectId,
            }),
          };
        },
      };
    },
    readPostgresValues(config, execAz) {
      assert.equal(config.postgresEntraAdminObjectId, P1_POSTGRES_TARGET.operatorObjectId);
      assert.equal(typeof execAz, 'function');
      return metadata();
    },
    async initializePostgres(input) {
      initializerInput = input;
      return { database: 'agentweaver', schema: 'identity_broker' };
    },
  });

  assert.equal(calls.length, 0);
  assert.equal(result.executed, true);
  assert.equal(initializerInput.runtimePrincipalObjectId, metadata().identityBrokerRuntimeIdentity.principalObjectId);
  assert.equal(initializerInput.migrationPrincipalObjectId, metadata().identityBrokerMigrationIdentity.principalObjectId);
  assert.equal(initializerInput.verifyOnly, false);
  const { receiptPath, ...p1Initializer } = initializerInput.p1Initializer;
  assert.deepEqual(p1Initializer, {
    sourceSha: source.sha,
    coreMergeSha,
    planSha256: p1PostgresPlanHash(),
  });
  assert.equal(dirname(receiptPath), resolve(repoRoot, 'Temp'));
  assert.match(basename(receiptPath), /^p1-postgres-[0-9a-f]{32}\.jsonl$/);
  assert.equal(result.receipt.database, 'agentweaver');
});

function executionDependencies(onInitialize) {
  return {
    sourceResolver,
    execAz() { assert.fail('Target guard must provide the subscription-bound Azure client.'); },
    targetGuard() {
      return {
        group: { tags: { 'agentweaver:owner': source.owner, 'agentweaver:cost-center': source.costCenter } },
        execAz() { assert.fail('Bootstrap values are supplied by the fake reader.'); },
      };
    },
    readPostgresValues: () => metadata(),
    async initializePostgres(input) {
      onInitialize(input);
      return { database: 'agentweaver' };
    },
  };
}

/** Inspection of an initialized database verifies Identity read-only and passes no merge SHA or receipt. */
test('P1 PostgreSQL inspection is read-only and never re-bootstraps Identity', async () => {
  let input;
  const result = await bootstrapP1Postgres({ repoRoot, execute: true, mode: 'inspect' },
    executionDependencies(value => { input = value; }));
  assert.equal(result.mode, 'inspect');
  assert.equal(input.verifyOnly, true);
  assert.deepEqual(input.p1Initializer, {
    mode: 'inspect', sourceSha: source.sha, planSha256: p1PostgresPlanHash(),
  });
});

/** Pending continuation carries explicit maxima; only the applying mode writes a receipt. */
test('P1 PostgreSQL pending continuation requires explicit maxima and receipts only when applying', async () => {
  for (const mode of ['apply-pending-dry-run', 'apply-pending']) {
    let input;
    const result = await bootstrapP1Postgres({
      repoRoot, coreMergeSha, execute: true, mode, maxCoreVersion: 23, maxEventsVersion: 9,
    }, executionDependencies(value => { input = value; }));
    assert.equal(result.maxCoreVersion, 23);
    assert.equal(result.maxEventsVersion, 9);
    assert.equal(input.verifyOnly, true);
    const { receiptPath, ...p1Initializer } = input.p1Initializer;
    assert.deepEqual(p1Initializer, {
      mode, sourceSha: source.sha, coreMergeSha, planSha256: p1PostgresPlanHash(),
      maxCoreVersion: 23, maxEventsVersion: 9,
    });
    assert.equal(receiptPath === undefined, mode === 'apply-pending-dry-run');
  }
});

/** Partial or contradictory mode inputs fail before any source or Azure lookup. */
test('P1 PostgreSQL modes reject missing maxima, stray maxima, and inspection merge SHAs', async () => {
  const fail = { sourceResolver() { assert.fail('Invalid input must fail first.'); } };
  await assert.rejects(bootstrapP1Postgres({ repoRoot, coreMergeSha, mode: 'apply-pending', maxCoreVersion: 23 }, fail),
    /explicit maximum Core and Events versions/);
  await assert.rejects(bootstrapP1Postgres({ repoRoot, coreMergeSha, maxCoreVersion: 23, maxEventsVersion: 9 }, fail),
    /explicit maximum Core and Events versions/);
  await assert.rejects(bootstrapP1Postgres({ repoRoot, coreMergeSha, mode: 'inspect' }, fail),
    /inspection takes none/);
  await assert.rejects(bootstrapP1Postgres({ repoRoot, coreMergeSha, mode: 'reset' }, fail),
    /supported mode/);
  assert.equal(p1PostgresMode({ inspect: true }), 'inspect');
  assert.equal(p1PostgresMode({ 'apply-pending-migrations': true, 'dry-run': true }), 'apply-pending-dry-run');
  assert.equal(p1PostgresMode({ 'apply-pending-migrations': true }), 'apply-pending');
  assert.equal(p1PostgresMode({}), 'initialize');
  assert.throws(() => p1PostgresMode({ 'dry-run': true }), /--dry-run applies only/);
  assert.throws(() => p1PostgresMode({ inspect: true, 'apply-pending-migrations': true }), /either/);
});