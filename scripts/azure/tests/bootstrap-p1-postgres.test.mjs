import assert from 'node:assert/strict';
import test from 'node:test';
import {
  bootstrapP1Postgres,
} from '../bootstrap-p1-postgres.mjs';
import {
  buildP1PostgresPlan,
  P1_POSTGRES_TARGET,
  p1PostgresPlanHash,
} from '../lib/p1-postgres-plan.mjs';

const coreMergeSha = 'b'.repeat(40);
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
    repoRoot: 'C:\\agentweaver',
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

/** Execution is admitted only after live Azure identity IDs match every planned P1 role. */
test('P1 PostgreSQL execution verifies target ownership and all five live principals', async () => {
  const calls = [];
  let initializerInput;
  const result = await bootstrapP1Postgres({
    repoRoot: 'C:\\agentweaver',
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
    readPostgresMetadata(config, execAz) {
      assert.equal(config.postgresEntraAdminObjectId, P1_POSTGRES_TARGET.operatorObjectId);
      assert.equal(typeof execAz, 'function');
      return metadata();
    },
    async initializePostgres(input) {
      initializerInput = input;
      return { database: 'agentweaver', schema: 'identity_broker' };
    },
  });

  assert.equal(calls.length, 5);
  assert.equal(result.executed, true);
  assert.equal(initializerInput.runtimePrincipalObjectId, metadata().identityBrokerRuntimeIdentity.principalObjectId);
  assert.equal(initializerInput.migrationPrincipalObjectId, metadata().identityBrokerMigrationIdentity.principalObjectId);
  const { receiptPath, ...p1Initializer } = initializerInput.p1Initializer;
  assert.deepEqual(p1Initializer, {
    sourceSha: source.sha,
    coreMergeSha,
    planSha256: p1PostgresPlanHash(),
  });
  assert.match(receiptPath, /^C:\\agentweaver\\Temp\\p1-postgres-/i);
  assert.match(receiptPath, /\.jsonl$/);
  assert.equal(result.receipt.database, 'agentweaver');
});

/** Principal drift stops before the temporary Kubernetes transport or database initialization begins. */
test('P1 PostgreSQL execution refuses a live identity that differs from the reviewed plan', async () => {
  let initializerCalled = false;
  await assert.rejects(bootstrapP1Postgres({
    repoRoot: 'C:\\agentweaver',
    coreMergeSha,
    execute: true,
  }, {
    sourceResolver,
    targetGuard(_config, _execAz) {
      return {
        group: { tags: { 'agentweaver:owner': source.owner, 'agentweaver:cost-center': source.costCenter } },
        execAz: args => {
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
              principalId: service.service === 'knowledge'
                ? 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee' : service.principalObjectId,
            }),
          };
        },
      };
    },
    readPostgresMetadata: () => metadata(),
    initializePostgres() {
      initializerCalled = true;
      assert.fail('Identity drift must stop before database bootstrap.');
    },
  }), /Live principal for P1 service knowledge differs from the reviewed PostgreSQL plan/);
  assert.equal(initializerCalled, false);
});
