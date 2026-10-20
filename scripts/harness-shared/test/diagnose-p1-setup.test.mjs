import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createP1SetupDiagnosisQuery, runP1SetupDiagnosisCli } from '../diagnose-p1-setup.mjs';

const ID = 'c65a2f67-9054-4781-9cbb-89286bd7c44f';

test('diagnosis generates only an invocation-bound read-only snapshot, never a replay or cleanup', async () => {
  const query = createP1SetupDiagnosisQuery(ID.toUpperCase());
  assert.match(query, /^BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;/);
  assert.match(query, /SET LOCAL statement_timeout = '15s';/);
  assert.match(query, /ROLLBACK;\n$/);
  assert.ok(query.includes(`invocation_id = '${ID}'::uuid`));
  assert.ok(query.includes(`p.name = 'p1-${ID}'`));
  assert.match(query, /'retryAuthorized', false/);
  assert.match(query, /THEN 'not-observed'/);
  assert.doesNotMatch(query, /\b(INSERT|UPDATE|DELETE|TRUNCATE|CREATE|ALTER|DROP|COMMIT|COPY)\b/i);
  assert.doesNotMatch(query, /accessToken|connectionString|password|configuration_json|\.result\b/i);
  const model = await readFile(new URL(
    '../../../services/projects-config/Agentweaver.Projects.Config/ProjectsConfigDbContext.cs', import.meta.url), 'utf8');
  for (const table of ['project_setup_operations', 'projects', 'project_role_assignments', 'project_run_selections']) {
    assert.ok(query.includes(`projects_config.${table}`));
    assert.ok(model.includes(`ToTable("${table}"`), `${table} must match the owner schema`);
  }
  for (const field of [
    'membership_id', 'membership_revision', 'owner_assignment_id', 'owner_assignment_revision',
    'project_configuration_revision', 'authority_audit_event_id', 'owner_authority_audit_event_id',
    'owner_revocation_audit_event_id',
  ]) {
    assert.ok(query.includes(field));
    assert.ok(model.includes(`HasColumnName("${field}")`));
  }
});

test('diagnosis rejects injection, missing or extra arguments, and never echoes invalid input', () => {
  for (const value of [undefined, '', `${ID}'; DELETE FROM projects;--`, 'controlled-secret-token']) {
    assert.throws(() => createP1SetupDiagnosisQuery(value), /valid P1 invocation UUID/);
  }
  for (const argv of [[], ['--invocation-id', 'controlled-secret-token'], ['--invocation-id', ID, '--cleanup']]) {
    const output = [];
    assert.equal(runP1SetupDiagnosisCli(argv, { log: text => output.push(text), error: text => output.push(text) }), 1);
    assert.doesNotMatch(output.join('\n'), /controlled-secret-token|DELETE/);
  }
  const output = [];
  assert.equal(runP1SetupDiagnosisCli(['--invocation-id', ID], { log: text => output.push(text) }), 0);
  assert.equal(output[0], createP1SetupDiagnosisQuery(ID));
});
