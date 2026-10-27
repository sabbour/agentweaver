import assert from 'node:assert/strict';
import test from 'node:test';
import { buildP1ProjectSetupAuthorityPlan } from '../lib/p1-project-setup-authority-plan.mjs';
import { buildP1PostgresPlan } from '../lib/p1-postgres-plan.mjs';

test('setup capability inventory is explicit and grants no cleanup or membership mutation', () => {
  const plan = buildP1ProjectSetupAuthorityPlan();
  assert.equal(plan.capability, 'projects-config-p1-setup');
  assert.deepEqual(plan.schemaPrivileges, ['USAGE']);
  assert.deepEqual(plan.tables, [
    { table: '__ef_migrations_history', privileges: ['SELECT'] },
    { table: 'tenant_memberships', privileges: ['SELECT'] },
    { table: 'projects', privileges: ['SELECT', 'INSERT'] },
    { table: 'project_configuration_revisions', privileges: ['SELECT', 'INSERT'] },
    { table: 'project_role_assignments', privileges: ['SELECT', 'INSERT'] },
    { table: 'authority_audit', privileges: ['SELECT', 'INSERT'] },
    { table: 'project_setup_operations', privileges: ['SELECT', 'INSERT'] },
  ]);
  assert.deepEqual(plan.functions, [{
    signature: 'lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
    privileges: ['EXECUTE'],
  }]);
  assert.ok(plan.deploymentPrerequisites.length > 0);
  for (const key of ['role', 'principalObjectId', 'clientId', 'environmentVersions', 'sequences']) {
    assert.equal(Object.hasOwn(plan, key), false, `inventory must not invent ${key}`);
  }
});

test('authority permissions never leak into the normal runtime initialization plan', () => {
  const runtime = buildP1PostgresPlan().services.find(service => service.service === 'projects-config');
  assert.ok(runtime.ownerOnlyTables.includes('project_setup_operations'));
  assert.equal(runtime.tables.some(table => table.table === 'project_setup_operations'), false);
  assert.deepEqual(buildP1ProjectSetupAuthorityPlan(), buildP1ProjectSetupAuthorityPlan());
  const altered = buildP1ProjectSetupAuthorityPlan();
  altered.tables[0].privileges.push('DELETE');
  assert.deepEqual(buildP1ProjectSetupAuthorityPlan().tables[0].privileges, ['SELECT']);
});
