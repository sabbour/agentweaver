import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import test from 'node:test';
import { buildP1PostgresPlan, p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';

test('Image compatibility permits rolling the reviewed 032-to-033 migrations', () => {
  const compatibility = JSON.parse(readFileSync(new URL(
    '../../../services/orchestrator/Agentweaver.Orchestrator/schema-compatibility.json', import.meta.url)));
  const plan = buildP1PostgresPlan();
  const core = plan.coreVersions;
  // The runner can read this source-owned contract before choosing deployment and migration order.
  assert.deepEqual(compatibility.applyByDefault, core);
  assert.deepEqual(compatibility.currentImage.requiredCoreVersions, core.filter(version => version !== 32 && version !== 33));
  assert.deepEqual(compatibility.currentImage.optionalCoreVersions, [32, 33]);
  assert.equal(compatibility.currentImage.repositoryIdentityWriteVersion, 33);
  assert.equal(compatibility.currentImage.beforeRepositoryIdentityUpdateError, 'database_update_pending');
  assert.deepEqual(compatibility.rolloutOrder, ['deployCurrentImages', 'applyCore032And033']);
  assert.deepEqual(compatibility.separateApprovalRequired, []);
  assert.equal(compatibility.preRepositoryIdentityImage.acceptsCore033, false);
  assert.equal(core.includes(32), true);
});

test('P1 plan binds five service roles to the provisioned principal IDs', () => {
  const plan = buildP1PostgresPlan();
  assert.equal(plan.target.database, 'agentweaver');
  assert.equal(plan.target.operatorName, 'asabbour@microsoft.com');
  assert.equal(plan.target.connectionHost, '127.0.0.1');
  assert.equal(plan.target.connectionPort, 15432);
  assert.equal(plan.target.sslMode, 'VerifyFull');
  assert.equal(plan.services.length, 5);
  assert.equal(new Set(plan.services.map(item => item.principalObjectId)).size, 5);
  assert.deepEqual(plan.services.map(item => [item.service, item.principalObjectId]), [
    ['projects-config', 'df4f3934-9585-47c7-b923-d9abca981dfe'],
    ['environment', '3f94ddf2-a376-4d8e-8e8f-ad24e96f7417'],
    ['knowledge', '9bc761f0-f192-41c6-a2d7-bbee1b83a1f2'],
    ['events-and-sessions', '7045d349-8e2d-40d1-a492-039540465780'],
    ['orchestrator', 'bb6f5b78-1f0e-4e56-a93c-31d96d9e1aa7'],
  ]);
  assert.equal(new Set(plan.services.map(item => item.role)).size, 5);
  assert.deepEqual(plan.services.map(item => item.schema),
    ['projects_config', 'environment', 'knowledge', 'events_sessions', 'orchestrator']);
  assert.deepEqual(plan.services.map(item => [item.ownerOnlyTables, item.ownerOnlySequences]), [
    [['project_setup_operations'], ['platform_runtime_revisions_revision_seq']],
    [[], []],
    [[], []],
    [[], []],
    [[], []],
  ]);
  assert.ok(plan.services.every(item => !item.role.includes('migration')));
  assert.ok(plan.executionRequirements.some(item =>
    item.includes('current target identity deployment')));
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

test('The only DELETE grants are the source-owned rollback, edge, and Priority link paths', () => {
  const deletes = buildP1PostgresPlan().services.flatMap(service =>
    service.tables.filter(table => table.privileges.includes('DELETE'))
      .map(table => `${service.schema}.${table.table}`));
  assert.deepEqual(deletes, ['events_sessions.addressed_message_threads', 'orchestrator.backlog_dependencies',
    'orchestrator.chief_of_staff_priority_links']);
});

test('Row locks use one column, never blanket UPDATE on append-only tables', () => {
  const plan = buildP1PostgresPlan();
  const locks = plan.services.flatMap(service =>
    service.tables.filter(table => table.lockColumn).map(table => ({ service, table })));
  assert.equal(locks.length, 8);
  for (const { service, table } of locks) {
    assert.deepEqual(table.privileges, ['SELECT', 'INSERT']);
    assert.equal(table.mutationDenialRequired, true);
    assert.match(table.lockColumn, /^[a-z][a-z0-9_]*$/);
    assert.deepEqual(Object.keys(table.mutationDenialGuard).sort(),
      ['function', 'message', 'sqlState', 'trigger', 'triggerType']);
    assert.match(table.mutationDenialGuard.trigger, /^[a-z][a-z0-9_]*$/);
    assert.match(table.mutationDenialGuard.function, /^[a-z][a-z0-9_]*\(\)$/);
    assert.ok([19, 27].includes(table.mutationDenialGuard.triggerType));
    assert.ok(service.functions.some(item =>
      item.signature === table.mutationDenialGuard.function && !item.execute));
  }
  assert.deepEqual(locks.map(({ service, table }) =>
    `${service.schema}.${table.table}:${table.mutationDenialGuard.trigger}:` +
    `${table.mutationDenialGuard.function}:${table.mutationDenialGuard.triggerType}`).sort(), [
    'events_sessions.session_fork_lineage:session_fork_lineage_no_update:reject_row_lock_record_update():19',
    'events_sessions.session_provider_bindings:session_provider_bindings_no_update:reject_row_lock_record_update():19',
    'events_sessions.sessions:sessions_no_update:reject_row_lock_record_update():19',
    'events_sessions.usage_ledger:usage_ledger_append_only:reject_usage_history_mutation():27',
    'events_sessions.usage_rate_cards:usage_rate_cards_immutable:reject_usage_history_mutation():27',
    'orchestrator.coordinator_decisions:coordinator_decisions_no_update:reject_row_lock_record_update():19',
    'orchestrator.maf_workflow_checkpoints:maf_workflow_checkpoints_no_update:reject_row_lock_record_update():19',
    'orchestrator.session_consistency_manifests:session_consistency_manifests_immutable:reject_session_consistency_manifest_mutation():27',
  ].sort());
});

test('Only the existing casting authority function receives direct runtime EXECUTE', () => {
  const functions = buildP1PostgresPlan().services.flatMap(service =>
    service.functions.filter(item => item.execute).map(item => `${service.schema}.${item.signature}`));
  assert.deepEqual(functions,
    ['projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)']);
});

// Migration 032 and its table grants are part of the same reviewed plan.
test('Core plan includes 032 and 033, skips 017, and grants only the PR-record operations', () => {
  // The plan must include every shipped resource without granting mutation of caller receipts.
  const plan = buildP1PostgresPlan();
  assert.deepEqual(plan.coreVersions,
    [...Array.from({ length: 16 }, (_, index) => index + 1), 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33]);
  assert.deepEqual(plan.services.find(service => service.schema === 'orchestrator').tables
    .find(table => table.table === 'source_control_pull_requests').privileges, ['SELECT', 'INSERT', 'UPDATE']);
  assert.deepEqual(plan.services.find(service => service.schema === 'orchestrator').tables
    .find(table => table.table === 'user_notification_dismissals').privileges, ['SELECT', 'INSERT']);
  assert.deepEqual(plan.services.find(service => service.schema === 'orchestrator').tables
    .find(table => table.table === 'guardrails_evaluations').privileges, ['SELECT', 'INSERT']);
  for (const name of ['backlog_plan_promotions', 'backlog_task_origins', 'user_notification_promotion_dismissals'])
    assert.deepEqual(plan.services.find(service => service.schema === 'orchestrator').tables
      .find(table => table.table === name).privileges, ['SELECT', 'INSERT']);
  assert.equal(plan.executionRequirements.length, 6);
  assert.ok(plan.executionRequirements.some(item => item.includes('separate approval')));
  const replacementGrant = plan.executionRequirements.find(item =>
    item.includes('SELECT,INSERT on environment.runtime_bootstrap_profile_replacements'));
  assert.match(replacementGrant, /exactly SELECT,INSERT/);
  assert.match(replacementGrant, /no UPDATE, DELETE, or default privileges/);
  assert.deepEqual(plan.services.find(item => item.schema === 'environment').tables
    .find(item => item.table === 'runtime_bootstrap_profile_replacements').privileges,
  ['SELECT', 'INSERT']);
  assert.ok(plan.executionRequirements.some(item => item.includes('restricted-role PostgreSQL tests prove')));
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

test('Initializer validates planned and installed mutation guards before it commits grants', () => {
  // Read source only: these assertions must not mint credentials or initialize a database.
  const source = readFileSync(new URL('../../../tools/P1PostgresInitializer/Program.cs', import.meta.url), 'utf8')
    .replace(/\r\n/g, '\n');
  const planGuard = source.indexOf('InitializerChecks.AssertMutationDenialPlan(plan.Services)');
  assert.ok(planGuard > 0 && planGuard < source.indexOf('new AzureCliCredential('));
  assert.ok(source.includes('InitializerChecks.AssertMutationDenialGuards(service, mutationGuards, ownerOid, runtimeOid)'));
  assert.ok(source.includes("has_column_privilege(@role, @table, @column, 'UPDATE')"));
  assert.doesNotMatch(source, /plan\.Services\.Any\(service => service\.Tables\.Any\(table => table\.MutationDenialRequired\)\)/);
  assert.doesNotMatch(source, /One-column row-lock grants still require reviewed mutation-denial source/);
  // The plan catalog is compared with the compiled migrators, not regex-pinned source text.
  assert.doesNotMatch(source, /Enumerable\\\\\.Range\\\\\(1/);
  assert.ok(source.includes('InitializerChecks.AssertMigrationCatalog(plan.CoreVersions, CoordinationOwnerMigrator.SupportedVersions,'));
});

// Both owner catalogs must agree with the separately reviewed initializer plan.
test('Current source catalog preserves exact Core and Events versions and embedded resources', () => {
  const plan = buildP1PostgresPlan();
  const owners = [
    ['orchestrator/Agentweaver.Orchestrator', 'CoordinationOwnerMigrator.cs', plan.coreVersions],
    ['events-and-sessions/Agentweaver.EventsAndSessions', 'EventsAndSessionsMigrator.cs',
      plan.eventsVersions],
  ];
  for (const [owner, migrator, versions] of owners) {
    // Version registration, embedded resources, and the reviewed plan must describe the same catalog.
    const directory = new URL(`../../../services/${owner}/`, import.meta.url);
    const source = readFileSync(new URL(migrator, directory), 'utf8').replace(/\r\n/g, '\n');
    const project = readFileSync(new URL(`${owner.split('/')[1]}.csproj`, directory), 'utf8');
    const embedded = [...project.matchAll(/<EmbeddedResource Include="Migrations\\([^"]+)"\s*\/>/g)]
      .map(match => match[1]);
    const mappings = owner.startsWith('orchestrator/')
      ? [...source.matchAll(/^\s*(\d+) => "([a-z_]+)",$/gm)]
        .map(match => [Number(match[1]), Number(match[1]) === 10
          ? `${match[2]}.sql` : `${match[1].padStart(3, '0')}_${match[2]}.sql`])
      : [...source.matchAll(/^\s*(\d+) => "Agentweaver\.EventsAndSessions\.Migrations\.([^"]+)",$/gm)]
        .map(match => [Number(match[1]), match[2]]);
    assert.deepEqual(mappings.map(([version]) => version), versions);
    assert.deepEqual(embedded, mappings.map(([, filename]) => filename));
    for (const [, filename] of mappings)
      assert.ok(readFileSync(new URL(`Migrations/${filename}`, directory)).length > 0);
    if (owner.startsWith('orchestrator/')) {
      assert.ok(source.includes('Enumerable.Range(1, 16).Concat([18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33])'));
      assert.ok(source.includes('(applied.Contains(21) && !applied.Contains(20))'));
      assert.ok(source.includes('(applied.Contains(22) && !applied.Contains(21))'));
      assert.ok(source.includes('(applied.Contains(23) && !applied.Contains(22))'));
      assert.ok(source.includes('(applied.Contains(24) && !applied.Contains(23))'));
      assert.ok(source.includes('(applied.Contains(29) && !applied.Contains(28))'));
      assert.ok(!source.includes('if (!applied.Contains(21))'));
      const allowed = source.match(/WHERE version NOT IN \(([\d, ]+)\)/);
      assert.deepEqual(allowed[1].split(',').map(Number), versions);
      assert.ok(source.includes("tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal"));
      assert.equal((source.match(/tgtype = 19/g) ?? []).length, 2);
      assert.ok(source.includes('(applied.Contains(30) && !applied.Contains(26))'));
      assert.ok(source.includes('(applied.Contains(31) && (!applied.Contains(30) || !applied.Contains(25)))'));
      assert.ok(source.includes('Enumerable.Range(57, 45)'));
      assert.ok(source.includes('(applied.Contains(33) && !applied.Contains(24))'));
      assert.ok(plan.coreVersions.includes(32));
    } else {
      assert.deepEqual(plan.eventsVersions, Array.from({ length: 9 }, (_, index) => index + 1));
      assert.ok(source.includes('private const int CurrentSchemaVersion = 9;'));
      assert.ok(source.includes('(applied.Contains(8) && !applied.Contains(7))'));
      assert.ok(source.includes('(applied.Contains(9) && !applied.Contains(8))'));
      assert.ok(source.includes('3 when legacyProjectFactsV2 => "Agentweaver.EventsAndSessions.Migrations.002_addressed_messages.sql"'));
      assert.equal((source.match(/tgtype = 19/g) ?? []).length, 3);
    }
  }
});

test('Environment EF migrations map to the reviewed ordered schema versions', () => {
  const plan = buildP1PostgresPlan();
  const source = readFileSync(
    new URL('../../../services/environment/Agentweaver.Environment/EnvironmentMigrator.cs', import.meta.url),
    'utf8').replace(/\r\n/g, '\n');
  const catalog = [...source.matchAll(/^\s*"(\d{14}_[A-Za-z0-9_]+)",$/gm)].map(match => match[1]);
  const migrationsDirectory = new URL(
    '../../../services/environment/Agentweaver.Environment/Migrations/', import.meta.url);
  const migrationIds = readdirSync(migrationsDirectory)
    .filter(name => name.endsWith('.cs') && name !== 'EnvironmentDbContextModelSnapshot.cs')
    .flatMap(name => [...readFileSync(new URL(name, migrationsDirectory), 'utf8')
      .matchAll(/\[Migration\("([^"]+)"\)\]/g)].map(match => match[1]))
    .sort();

  assert.deepEqual(catalog, migrationIds);
  assert.deepEqual(plan.environmentVersions, Array.from({ length: catalog.length }, (_, index) => index + 1));
  assert.match(source, /private const int CurrentSchemaVersion = 11;/);
  assert.match(source, /context\.Database\.GetMigrations\(\)/);
});

// The initializer derives Core and Events versions from the plan and the compiled migrator catalogs.
test('Initializer binds exact plan versions to compiled migrator catalogs and approved maxima', () => {
  const source = readFileSync(new URL('../../../tools/P1PostgresInitializer/Program.cs', import.meta.url), 'utf8');
  assert.ok(!source.includes('private const int CurrentSchemaVersion = 9;'));
  assert.ok(source.includes('InitializerChecks.AssertMigrationCatalog(plan.EventsVersions, EventsAndSessionsMigrator.SupportedVersions,'));
  assert.ok(source.includes('plan.EnvironmentVersions'));
  assert.ok(source.includes('maxEventsVersion, "Events")'));
  assert.ok(source.includes('maxCoreVersion, "Core/Orchestrator")'));
});

test('Catalog inventory includes exact source functions, tables, and lock grants without setup-table grants', () => {
  // Check exact grants as well as counts; a new table must not gain broader default privileges.
  const plan = buildP1PostgresPlan();
  assert.equal(plan.services.flatMap(service => service.tables).length, 112);
  const projectsConfig = plan.services.find(service => service.schema === 'projects_config');
  assert.deepEqual(projectsConfig.tables.find(table => table.table === 'casting_generation_requests').privileges,
    ['SELECT', 'INSERT', 'UPDATE']);
  const orchestrator = plan.services.find(service => service.schema === 'orchestrator');
  assert.deepEqual(orchestrator.tables.find(table => table.table === 'project_run_requests').privileges,
    ['SELECT', 'INSERT', 'UPDATE']);
  assert.deepEqual(orchestrator.tables.find(table => table.table === 'project_run_cancellations').privileges,
    ['SELECT', 'INSERT']);
  for (const [table, privileges] of [
    ['chief_of_staff_conversations', ['SELECT', 'INSERT']],
    ['chief_of_staff_priority_lists', ['SELECT', 'INSERT', 'UPDATE']],
    ['chief_of_staff_priorities', ['SELECT', 'INSERT', 'UPDATE']],
    ['chief_of_staff_priority_links', ['SELECT', 'INSERT', 'DELETE']],
  ])
    assert.deepEqual(orchestrator.tables.find(item => item.table === table).privileges, privileges);
  assert.deepEqual(plan.services.map(service => service.functions.length), [4, 0, 4, 4, 21]);
  assert.equal(plan.services.flatMap(service => service.functions).length, 33);
  const projectSetupGuard = plan.services
    .find(service => service.schema === 'projects_config')
    ?.functions.find(item => item.signature === 'reject_project_setup_operation_mutation()');
  assert.deepEqual(projectSetupGuard, {
    signature: 'reject_project_setup_operation_mutation()',
    execute: false,
  });
  assert.ok(plan.services.every(service =>
    service.tables.every(table => table.table !== 'project_setup_operations')));
  const projects = plan.services.find(service => service.schema === 'projects_config');
  assert.deepEqual(projects.ownerOnlyTables, ['project_setup_operations']);
  assert.deepEqual(projects.ownerOnlySequences, ['platform_runtime_revisions_revision_seq']);
  assert.ok(!projects.tables.some(table => projects.ownerOnlyTables.includes(table.table)));
  assert.deepEqual(plan.services.find(service => service.schema === 'environment').tables
    .find(table => table.table === 'runtime_bootstrap_profile_replacements').privileges, ['SELECT', 'INSERT']);
  assert.deepEqual(plan.services.find(service => service.schema === 'environment').tables
    .find(table => table.table === 'sandbox_provider_attempts').privileges, ['SELECT', 'INSERT', 'UPDATE']);
  assert.deepEqual(plan.services.flatMap(service =>
    service.tables.filter(table => table.lockColumn)
      .map(table => `${service.schema}.${table.table}.${table.lockColumn}`)), [
    'events_sessions.sessions.session_id',
    'events_sessions.session_provider_bindings.run_id',
    'events_sessions.session_fork_lineage.target_session_id',
    'events_sessions.usage_rate_cards.card_id',
    'events_sessions.usage_ledger.event_id',
    'orchestrator.coordinator_decisions.decision_id',
    'orchestrator.maf_workflow_checkpoints.checkpoint_id',
    'orchestrator.session_consistency_manifests.manifest_id',
  ]);
  const directories = [
    'projects-config/Agentweaver.Projects.Config',
    'environment/Agentweaver.Environment',
    'knowledge/Agentweaver.Knowledge',
    'events-and-sessions/Agentweaver.EventsAndSessions',
    'orchestrator/Agentweaver.Orchestrator',
  ];
  for (const [index, owner] of directories.entries()) {
    const directory = new URL(`../../../services/${owner}/Migrations/`, import.meta.url);
    const functions = readdirSync(directory)
      .filter(file => file.endsWith('.sql') || file.endsWith('.cs'))
      .flatMap(file => [...readFileSync(new URL(file, directory), 'utf8').matchAll(
        /CREATE(?: OR REPLACE)? FUNCTION (?:"?\{schema\}"?|projects_config)\.([a-z_]+)\(([\s\S]*?)\)/g)]
        .map(match => `${match[1]}(${match[2].trim() ? match[2].split(',')
          .map(parameter => parameter.trim().split(/\s+/).at(-1)).join(', ') : ''})`));
    assert.deepEqual(functions.sort(), plan.services[index].functions.map(item => item.signature).sort());
  }
  assert.equal(plan.services.flatMap(service =>
    service.functions.filter(item => item.execute)).length, 1);
});

test('Old and new guards retain distinct SQL states and row and truncate protections', () => {
  for (const [owner, filename, tables] of [
    ['orchestrator/Agentweaver.Orchestrator', '021_row_lock_record_updates.sql',
      ['coordinator_decisions', 'maf_workflow_checkpoints']],
    ['events-and-sessions/Agentweaver.EventsAndSessions', '008_row_lock_record_updates.sql',
      ['sessions', 'session_provider_bindings', 'session_fork_lineage']],
  ]) {
    const sql = readFileSync(new URL(`../../../services/${owner}/Migrations/${filename}`, import.meta.url), 'utf8');
    assert.deepEqual([...sql.matchAll(/BEFORE UPDATE ON \{schema\}\.([a-z_]+)/g)]
      .map(match => match[1]), tables);
    assert.ok(sql.includes("USING ERRCODE = '55000'"));
    assert.ok(sql.includes('REVOKE ALL ON FUNCTION {schema}.reject_row_lock_record_update() FROM PUBLIC'));
    assert.doesNotMatch(sql, /\b(?:DELETE|TRUNCATE|RETURN)\b/);
  }
  for (const [owner, filename, functionName, tables] of [
    ['orchestrator/Agentweaver.Orchestrator', '019_session_suspend_resume.sql',
      'reject_session_consistency_manifest_mutation', ['session_consistency_manifests']],
    ['events-and-sessions/Agentweaver.EventsAndSessions', '005_copilot_usage.sql',
      'reject_usage_history_mutation', ['usage_rate_cards', 'usage_ledger']],
  ]) {
    const sql = readFileSync(new URL(`../../../services/${owner}/Migrations/${filename}`, import.meta.url), 'utf8');
    const body = sql.split(`CREATE OR REPLACE FUNCTION {schema}.${functionName}()`)[1].split('$$;')[0];
    assert.ok(body.includes('RAISE EXCEPTION'));
    assert.doesNotMatch(body, /ERRCODE/);
    for (const table of tables) {
      assert.match(sql, new RegExp(`BEFORE UPDATE OR DELETE ON \\{schema\\}\\.${table}\\s+FOR EACH ROW EXECUTE FUNCTION \\{schema\\}\\.${functionName}\\(\\)`));
      assert.match(sql, new RegExp(`BEFORE TRUNCATE ON \\{schema\\}\\.${table}\\s+FOR EACH STATEMENT EXECUTE FUNCTION \\{schema\\}\\.${functionName}\\(\\)`));
    }
  }
});
