import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { buildP1PostgresPlan, p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';

test('P1 plan binds the five existing runtime objects and operator-only target', () => {
  const plan = buildP1PostgresPlan();
  assert.equal(plan.target.database, 'agentweaver');
  assert.equal(plan.target.operatorName, 'asabbour@microsoft.com');
  assert.equal(plan.target.connectionHost, '127.0.0.1');
  assert.equal(plan.target.connectionPort, 15432);
  assert.equal(plan.target.sslMode, 'VerifyFull');
  assert.equal(plan.services.length, 5);
  assert.equal(new Set(plan.services.map(item => item.principalObjectId)).size, 5);
  assert.equal(new Set(plan.services.map(item => item.role)).size, 5);
  assert.deepEqual(plan.services.map(item => item.schema),
    ['projects_config', 'environment', 'knowledge', 'events_sessions', 'orchestrator']);
  assert.ok(plan.services.every(item => !item.role.includes('migration')));
  assert.ok(!JSON.stringify(plan).includes('password'));
});

test('Only exact table DML is planned; history and authority remain SELECT-only', () => {
  for (const service of buildP1PostgresPlan().services) {
    assert.equal(new Set(service.tables.map(item => item.table)).size, service.tables.length);
    for (const table of service.tables) {
      assert.match(table.table, /^[a-z_][a-z0-9_]*$/);
      assert.ok(table.privileges.every(item => ['SELECT', 'INSERT', 'UPDATE', 'DELETE'].includes(item)));
      if (table.table.endsWith('schema_migrations') || table.table === '__ef_migrations_history' ||
          ['tenant_memberships', 'project_role_assignments', 'authority_audit'].includes(table.table))
        assert.deepEqual(table.privileges, ['SELECT']);
    }
  }
});

test('The only DELETE grants are the two source-owned rollback and edge paths', () => {
  const deletes = buildP1PostgresPlan().services.flatMap(service =>
    service.tables.filter(table => table.privileges.includes('DELETE'))
      .map(table => `${service.schema}.${table.table}`));
  assert.deepEqual(deletes, ['events_sessions.addressed_message_threads', 'orchestrator.backlog_dependencies']);
});

test('Row locks use one column, never blanket UPDATE on append-only tables', () => {
  const locks = buildP1PostgresPlan().services.flatMap(service =>
    service.tables.filter(table => table.lockColumn));
  assert.equal(locks.length, 8);
  for (const table of locks) {
    assert.deepEqual(table.privileges, ['SELECT', 'INSERT']);
    assert.equal(table.mutationDenialRequired, true);
    assert.match(table.lockColumn, /^[a-z][a-z0-9_]*$/);
  }
});

test('Only the existing casting authority function receives direct runtime EXECUTE', () => {
  const functions = buildP1PostgresPlan().services.flatMap(service =>
    service.functions.filter(item => item.execute).map(item => `${service.schema}.${item.signature}`));
  assert.deepEqual(functions,
    ['projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)']);
});

test('Core plan includes 016/018 but never 017; source approval is not execution permission', () => {
  const plan = buildP1PostgresPlan();
  assert.ok(plan.coreVersions.includes(16));
  assert.ok(plan.coreVersions.includes(18));
  assert.ok(!plan.coreVersions.includes(17));
  assert.equal(plan.executionRequirements.length, 4);
  assert.ok(plan.executionRequirements.some(item => item.includes('separate approval')));
  assert.ok(plan.executionRequirements.some(item => item.includes('Restricted-role PostgreSQL tests')));
});

test('Plans and hashes are deterministic and independent copies', () => {
  const original = buildP1PostgresPlan();
  const hash = p1PostgresPlanHash(original);
  const other = buildP1PostgresPlan();
  other.services[0].tables[0].privileges.push('INSERT');
  other.target.database = 'foreign';
  assert.equal(p1PostgresPlanHash(), hash);
  assert.notEqual(p1PostgresPlanHash(other), hash);
  assert.match(hash, /^[0-9a-f]{64}$/);
});

test('Initializer source corrections preserve both execution blocks and the separate catalog review', () => {
  const source = readFileSync(new URL('../../../tools/P1PostgresInitializer/Program.cs', import.meta.url), 'utf8')
    .replace(/\r\n/g, '\n');
  const credentialBlock = source.indexOf('if (plan.Services.Any(service => service.Tables.Any(table => table.MutationDenialRequired)))');
  assert.ok(credentialBlock > 0);
  assert.ok(source.indexOf('No credential or database connection was used.', credentialBlock) >
    credentialBlock);
  assert.ok(credentialBlock < source.indexOf('new AzureCliCredential('));
  assert.ok(source.includes('if (table.LockColumn is not null)\n            throw new InvalidOperationException("One-column row-lock grants still require reviewed mutation-denial source and restricted-role proof. Initialization is not yet permitted.");'));
  assert.ok(source.includes('Enumerable\\.Range\\(1,\\s*16\\)\\.Concat\\(\\[18,\\s*19,\\s*20\\]\\)'));
  assert.ok(!source.includes('021') && !source.includes('008'));
});
