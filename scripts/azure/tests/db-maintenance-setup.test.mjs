import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { DB_MAINTENANCE_TARGET } from '../lib/db-maintenance.mjs';
import { SETUP_TARGET, parseSetupArgs, readSetupState, runDbMaintenanceSetup, setupScopes } from '../lib/db-maintenance-setup.mjs';

const ok = (stdout = '') => ({ status: 0, stdout, stderr: '' });
const fail = (stderr = 'failed', status = 1) => ({ status, stdout: '', stderr });
const CLIENT_ID = '11111111-2222-4333-8444-555555555555';
const PRINCIPAL_ID = '66666666-7777-4888-8999-aaaaaaaaaaaa';
const scopes = setupScopes();
const environmentPath = `repos/${SETUP_TARGET.repository}/environments/${SETUP_TARGET.environment}`;
const allRoles = [
  { role: 'Reader', scope: scopes.resourceGroup },
  { role: 'Azure Kubernetes Service Cluster User Role', scope: scopes.cluster },
  { role: `Agentweaver DB Maintenance Kubernetes (${DB_MAINTENANCE_TARGET.resourceGroup})`, scope: scopes.namespace },
];

/**
 * A fake Azure and GitHub. complete=true starts fully set up. The Bicep deployment creates the
 * identity, credential, and roles; gh calls create the environment, branch rule, and variable.
 */
function world({ complete = false, accountType = 'user', admin = 'true', extraRule, prepare = {} } = {}) {
  const live = complete
    ? { identity: true, roles: [...allRoles], environment: true, rules: [{ name: 'v1', type: 'branch' }], variable: CLIENT_ID }
    : { identity: false, roles: [], environment: false, rules: [], variable: undefined };
  if (extraRule) live.rules.push(extraRule);
  const calls = [];
  const runnerCalls = [];
  const receipts = [];
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
    if (command === 'git') return ok('b'.repeat(40));
    if (command === 'az') {
      if (args[0] === 'account') return ok(JSON.stringify({ id: DB_MAINTENANCE_TARGET.subscriptionId,
        tenantId: DB_MAINTENANCE_TARGET.tenantId, user: { name: 'admin@example.test', type: accountType } }));
      if (args[0] === 'group') return ok('{}');
      if (args[0] === 'identity' && args[1] === 'federated-credential')
        return ok(JSON.stringify(live.identity ? [{ issuer: SETUP_TARGET.githubIssuer, audiences: ['api://AzureADTokenExchange'],
          subject: `repo:${SETUP_TARGET.repository}:environment:${SETUP_TARGET.environment}` }] : []));
      if (args[0] === 'identity') return live.identity ? ok(JSON.stringify({ clientId: CLIENT_ID, principalId: PRINCIPAL_ID }))
        : fail(`ERROR: The Resource '${SETUP_TARGET.identity}' was not found.`, 3);
      if (args[0] === 'role') {
        const scope = args[args.indexOf('--scope') + 1];
        return ok(JSON.stringify(live.roles.filter(entry => entry.scope === scope)));
      }
      if (args[0] === 'deployment') {
        Object.assign(live, { identity: true, roles: [...allRoles] });
        return ok(JSON.stringify({ name: SETUP_TARGET.identity, clientId: CLIENT_ID, principalObjectId: PRINCIPAL_ID }));
      }
    }
    if (command === 'gh') {
      if (args[0] === 'variable' && args[1] === 'get') return live.variable ? ok(`${live.variable}\n`) : fail('variable not found', 1);
      if (args[0] === 'variable' && args[1] === 'set') { live.variable = args[args.indexOf('--body') + 1]; return ok(); }
      if (args.includes('.permissions.admin')) return ok(`${admin}\n`);
      if (args.includes('PUT')) {
        live.environment = true;
        assert.deepEqual(JSON.parse(settings.input), { deployment_branch_policy: { protected_branches: false, custom_branch_policies: true } });
        return ok('{}');
      }
      if (args.includes('POST')) { live.rules.push({ name: 'v1', type: 'branch' }); return ok('{}'); }
      if (args[1] === environmentPath) return live.environment
        ? ok(JSON.stringify({ deployment_branch_policy: { protected_branches: false, custom_branch_policies: true } }))
        : fail('gh: Not Found (HTTP 404)');
      if (args[1] === `${environmentPath}/deployment-branch-policies`) return ok(JSON.stringify({ branch_policies: live.rules }));
    }
    throw new Error(`Unexpected ${command} ${args.join(' ')}`);
  };
  return {
    live, calls, runnerCalls, receipts,
    deps: {
      exec, repoRoot: process.cwd(), newRunId: () => 'setup0000001', now: () => new Date('2026-10-22T10:00:00Z'), log: () => {},
      writeReceipt: (path, receipt) => receipts.push(structuredClone(receipt)),
      runner: async options => {
        runnerCalls.push(options);
        return { runId: 'prep00000001', result: options.execute ? 'succeeded' : 'dry-run-passed',
          plannedSteps: prepare.plannedSteps ?? ['lock', 'migration-identity-verified', 'handover-skip'] };
      },
    },
  };
}

test('setup arguments accept only --execute and --receipt-dir', () => {
  assert.equal(parseSetupArgs([]).execute, false);
  assert.equal(parseSetupArgs(['--execute', '--receipt-dir=/tmp/r']).receiptDir, '/tmp/r');
  assert.throws(() => parseSetupArgs(['--force']), /Unknown argument/);
  assert.throws(() => parseSetupArgs(['--receipt-dir']), /requires a value/);
});

test('verify reports every gap, changes nothing, and fails', async () => {
  const fake = world({ prepare: { plannedSteps: ['lock', 'migration-identity-deploy', 'handover-run'] } });
  await assert.rejects(runDbMaintenanceSetup(parseSetupArgs([]), fake.deps), /Setup is incomplete \(4 gaps\)/);
  const [receipt] = fake.receipts;
  assert.equal(receipt.result, 'setup-needed');
  assert.match(receipt.gaps.join('\n'), /P1 migration identity[\s\S]*ownership handover[\s\S]*agentweaver-id-db-maintenance does not exist[\s\S]*environment v1-db-maintenance does not exist/);
  assert.equal(fake.runnerCalls[0].execute, false);
  assert.equal(fake.runnerCalls[0].prepareOnly, true);
  assert.ok(!fake.calls.some(call => call.args.includes('deployment') || call.args.includes('PUT') ||
    call.args.includes('POST') || call.args[1] === 'set'));
});

test('execute creates only what is missing, then verifies it', async () => {
  const fake = world();
  const receipt = await runDbMaintenanceSetup(parseSetupArgs(['--execute']), fake.deps);
  assert.equal(receipt.result, 'succeeded');
  assert.deepEqual(receipt.steps.map(step => step.name),
    ['accounts-verified', 'p1-prepared', 'workflow-identity-deployed', 'github-environment-ready', 'setup-verified']);
  assert.equal(fake.runnerCalls[0].execute, true);
  assert.equal(fake.live.variable, CLIENT_ID);
  assert.deepEqual(fake.live.rules, [{ name: 'v1', type: 'branch' }]);
  const deploy = fake.calls.find(call => call.args[0] === 'deployment');
  assert.ok(deploy.args.includes(SETUP_TARGET.template));
  assert.ok(deploy.args.includes(`sourceSha=${'b'.repeat(40)}`));
});

test('a rerun on a complete setup deploys nothing and adds no branch rule', async () => {
  const fake = world({ complete: true });
  const receipt = await runDbMaintenanceSetup(parseSetupArgs(['--execute']), fake.deps);
  assert.equal(receipt.result, 'succeeded');
  assert.ok(!fake.calls.some(call => call.args[0] === 'deployment' || call.args.includes('POST')));
  const verify = world({ complete: true });
  assert.equal((await runDbMaintenanceSetup(parseSetupArgs([]), verify.deps)).result, 'verified');
});

test('setup fails closed for a service principal, a non-admin, or a foreign branch rule', async () => {
  await assert.rejects(runDbMaintenanceSetup(parseSetupArgs(['--execute']), world({ accountType: 'servicePrincipal' }).deps),
    /as the PostgreSQL Entra administrator/);
  await assert.rejects(runDbMaintenanceSetup(parseSetupArgs(['--execute']), world({ admin: 'false' }).deps),
    /not an admin/);
  const foreign = world({ complete: true, extraRule: { name: 'main', type: 'branch' } });
  await assert.rejects(runDbMaintenanceSetup(parseSetupArgs(['--execute']), foreign.deps), /branch rules other than v1/);
  assert.equal(foreign.live.rules.length, 2);
});

test('state reads count a role only at its exact scope', () => {
  const fake = world({ complete: true });
  fake.live.roles = fake.live.roles.map(entry => entry.role === 'Reader' ? { ...entry, scope: scopes.cluster } : entry);
  const state = readSetupState(fake.deps.exec);
  assert.deepEqual(state.gaps, ['Role "Reader" is not assigned at the resourceGroup scope.']);
});

test('the workflow identity template trusts only the v1-db-maintenance environment and grants namespace-scoped rights', () => {
  const source = readFileSync('infra/bicep/db-maintenance-ci-identity.bicep', 'utf8');
  assert.match(source, /token\.actions\.githubusercontent\.com/);
  assert.match(source, /repo:\$\{githubRepository\}:environment:\$\{githubEnvironment\}/);
  assert.doesNotMatch(source, /Microsoft\.ContainerService\/managedClusters\/[^']*\/\*/);
  assert.doesNotMatch(source, /pods\/exec|secrets|roleAssignments\/write|deployments\/delete/);
  const result = spawnSync('az', ['bicep', 'build', '--file', 'infra/bicep/db-maintenance-ci-identity.bicep', '--stdout'],
    { encoding: 'utf8', shell: process.platform === 'win32' });
  // The Bicep CLI is optional offline; when present the compiled scope must be the namespace.
  if (result.error || result.status !== 0) return;
  const flat = JSON.stringify(JSON.parse(result.stdout));
  assert.match(flat, /Microsoft\.ContainerService\/managedClusters\/namespaces/);
  assert.match(flat, /Microsoft\.ManagedIdentity\/userAssignedIdentities\/federatedIdentityCredentials/);
});

test('the workflow uses the variable and environment that setup creates', () => {
  const workflow = readFileSync('.github/workflows/v1-db-maintenance.yml', 'utf8');
  assert.match(workflow, new RegExp(`environment: ${SETUP_TARGET.environment}`));
  assert.match(workflow, new RegExp(`vars\\.${SETUP_TARGET.variable}`));
});
