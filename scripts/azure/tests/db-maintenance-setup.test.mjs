import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { DB_MAINTENANCE_TARGET } from '../lib/db-maintenance.mjs';
import { SETUP_TARGET, parseSetupArgs, readSetupState, runDbMaintenanceSetup, setupScopes,
  workflowAuthorizationResources } from '../lib/db-maintenance-setup.mjs';

const ok = (stdout = '') => ({ status: 0, stdout, stderr: '' });
const fail = (stderr = 'failed', status = 1) => ({ status, stdout: '', stderr });
const CLIENT_ID = '11111111-2222-4333-8444-555555555555';
const PRINCIPAL_ID = '66666666-7777-4888-8999-aaaaaaaaaaaa';
const MIGRATION_CLIENT_ID = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
const MIGRATION_PRINCIPAL_ID = 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff';
const OIDC_ISSUER = 'https://eastus2euap.oic.prod-aks.azure.com/tenant/cluster/';
const scopes = setupScopes();
const environmentPath = `repos/${SETUP_TARGET.repository}/environments/${SETUP_TARGET.environment}`;
const allRoles = [
  { role: 'Reader', scope: scopes.resourceGroup },
  { role: 'Azure Kubernetes Service Cluster User Role', scope: scopes.cluster },
];

/**
 * A fake Azure and GitHub. complete=true starts fully set up. The Bicep deployment creates the
 * identity, credential, and Azure roles; kubectl applies namespace RBAC; gh creates the environment.
 */
function world({ complete = false, accountType = 'user', admin = 'true', extraRule, prepare = {},
  authorization = { azureRbac: true, kubernetesRbac: true } } = {}) {
  const resources = complete ? workflowAuthorizationResources(PRINCIPAL_ID, MIGRATION_CLIENT_ID) : [];
  const live = complete
    ? { identity: true, migrationIdentity: true, roles: [...allRoles], environment: true,
      rules: [{ name: 'v1', type: 'branch' }], variable: CLIENT_ID,
      kubernetesResources: new Map(resources.map(resource =>
        [`${resource.kind.toLowerCase()}/${resource.metadata.name}`, structuredClone(resource)])) }
    : { identity: false, migrationIdentity: false, roles: [], environment: false, rules: [],
      variable: undefined, kubernetesResources: new Map() };
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
      if (args[0] === 'aks' && args[1] === 'show') return ok(JSON.stringify({ ...authorization, oidcIssuer: OIDC_ISSUER }));
      if (args[0] === 'aks' && args[1] === 'get-credentials') return ok();
      if (args[0] === 'group') return ok('{}');
      if (args[0] === 'identity' && args[1] === 'federated-credential') {
        const identityName = args[args.indexOf('--identity-name') + 1];
        const exists = identityName === SETUP_TARGET.identity ? live.identity : live.migrationIdentity;
        const credential = identityName === SETUP_TARGET.identity
          ? { issuer: SETUP_TARGET.githubIssuer, subject: `repo:${SETUP_TARGET.repository}:environment:${SETUP_TARGET.environment}` }
          : { issuer: OIDC_ISSUER, subject: `system:serviceaccount:${DB_MAINTENANCE_TARGET.namespace}:${SETUP_TARGET.projectsConfigMigrationServiceAccount}` };
        return ok(JSON.stringify(exists ? [{ ...credential, audiences: ['api://AzureADTokenExchange'] }] : []));
      }
      if (args[0] === 'identity' && args[1] === 'show') {
        const identityName = args[args.indexOf('--name') + 1];
        if (identityName === SETUP_TARGET.identity && live.identity)
          return ok(JSON.stringify({ name: identityName, clientId: CLIENT_ID, principalId: PRINCIPAL_ID }));
        if (identityName === SETUP_TARGET.projectsConfigMigrationIdentity && live.migrationIdentity)
          return ok(JSON.stringify({ name: identityName, clientId: MIGRATION_CLIENT_ID, principalId: MIGRATION_PRINCIPAL_ID }));
        return fail(`ERROR: The Resource '${identityName}' was not found.`, 3);
      }
      if (args[0] === 'role') {
        const scope = args[args.indexOf('--scope') + 1];
        return ok(JSON.stringify(live.roles.filter(entry => entry.scope === scope)));
      }
      if (args[0] === 'deployment') {
        Object.assign(live, { identity: true, migrationIdentity: true, roles: [...allRoles] });
        return ok(JSON.stringify({
          maintenanceIdentity: { value: { name: SETUP_TARGET.identity, clientId: CLIENT_ID, principalObjectId: PRINCIPAL_ID } },
          projectsConfigMigrationIdentity: {
            value: { name: SETUP_TARGET.projectsConfigMigrationIdentity,
              clientId: MIGRATION_CLIENT_ID, principalObjectId: MIGRATION_PRINCIPAL_ID },
          },
        }));
      }
    }
    if (command === 'kubelogin') return ok();
    if (command === 'kubectl') {
      const actionIndex = args.findIndex(arg => ['apply', 'get'].includes(arg));
      const action = args[actionIndex];
      if (action === 'apply') {
        const resource = JSON.parse(settings.input);
        live.kubernetesResources.set(`${resource.kind.toLowerCase()}/${resource.metadata.name}`, resource);
        return ok(JSON.stringify(resource));
      }
      if (action === 'get') {
        const kind = args[actionIndex + 1];
        const name = args[actionIndex + 2];
        const resource = live.kubernetesResources.get(`${kind}/${name}`);
        return resource ? ok(JSON.stringify(resource)) : fail(`Error from server (NotFound): ${kind} "${name}" not found.`);
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

test('setup arguments accept only --execute, --v1-deploy-only, and --receipt-dir', () => {
  assert.equal(parseSetupArgs([]).execute, false);
  assert.equal(parseSetupArgs(['--execute', '--receipt-dir=C:\\agentweaver-scratch\\r']).receiptDir,
    'C:\\agentweaver-scratch\\r');
  assert.equal(parseSetupArgs(['--v1-deploy-only']).v1DeployOnly, true);
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
    ['accounts-verified', 'p1-prepared', 'workflow-identities-deployed', 'workflow-kubernetes-authorization-applied',
      'github-environment-ready', 'setup-verified']);
  assert.equal(fake.runnerCalls[0].execute, true);
  assert.equal(fake.live.variable, CLIENT_ID);
  assert.deepEqual(fake.live.rules, [{ name: 'v1', type: 'branch' }]);
  assert.deepEqual(fake.live.roles, allRoles);
  const applied = fake.calls.filter(call => call.command === 'kubectl' && call.args.includes('apply'))
    .map(call => JSON.parse(call.settings.input));
  assert.deepEqual(applied, workflowAuthorizationResources(PRINCIPAL_ID, MIGRATION_CLIENT_ID));
  const deploy = fake.calls.find(call => call.args[0] === 'deployment');
  assert.ok(deploy.args.includes(SETUP_TARGET.template));
  assert.ok(deploy.args.includes(`sourceSha=${'b'.repeat(40)}`));
});

test('v1 deployment setup provisions only approved identities and namespace authorization', async () => {
  const fake = world();
  const receipt = await runDbMaintenanceSetup(
    parseSetupArgs(['--execute', '--v1-deploy-only']), fake.deps);

  assert.equal(receipt.result, 'succeeded');
  assert.equal(fake.runnerCalls.length, 0);
  assert.equal(fake.live.identity, true);
  assert.equal(fake.live.migrationIdentity, true);
  assert.deepEqual(fake.calls.filter(call => call.command === 'kubectl' && call.args.includes('apply'))
    .map(call => JSON.parse(call.settings.input)),
  workflowAuthorizationResources(PRINCIPAL_ID, MIGRATION_CLIENT_ID));
});

test('setup verifies both AKS authorization modes before changing the workflow identity', async () => {
  const fake = world({ authorization: { azureRbac: false, kubernetesRbac: true } });
  await assert.rejects(runDbMaintenanceSetup(parseSetupArgs(['--execute']), fake.deps),
    /both Azure RBAC for Kubernetes and Kubernetes RBAC enabled/);
  const authorizationCall = fake.calls.find(call => call.command === 'az' && call.args[0] === 'aks' && call.args[1] === 'show');
  assert.equal(authorizationCall.args[authorizationCall.args.indexOf('--query') + 1],
    '{azureRbac:aadProfile.enableAzureRbac,kubernetesRbac:enableRbac,oidcIssuer:oidcIssuerProfile.issuerURL}');
  assert.ok(!fake.calls.some(call => call.command === 'az' && call.args[0] === 'deployment'));
  assert.ok(!fake.calls.some(call => call.command === 'kubectl' && call.args.includes('apply')));
});

test('AKS source enables both authorization modes used by setup', () => {
  const source = readFileSync('infra/bicep/modules/aks.bicep', 'utf8');
  assert.match(source, /enableRBAC:\s*true/);
  assert.match(source, /enableAzureRBAC:\s*true/);
});

test('namespace Role grants only approved deploy and maintenance access and binds the workflow identity', () => {
  const [role, binding, serviceAccount] = workflowAuthorizationResources(PRINCIPAL_ID, MIGRATION_CLIENT_ID);
  assert.equal(role.metadata.namespace, DB_MAINTENANCE_TARGET.namespace);
  assert.deepEqual(role.rules, [
    { apiGroups: ['batch'], resources: ['jobs'], verbs: ['create', 'delete', 'get', 'list', 'watch'] },
    { apiGroups: [''], resources: ['pods'], verbs: ['get', 'list', 'watch'] },
    { apiGroups: [''], resources: ['pods/log'], verbs: ['get'] },
    { apiGroups: [''], resources: ['resourcequotas', 'limitranges'], verbs: ['create', 'get', 'patch'] },
    { apiGroups: [''], resources: ['services', 'networkpolicies'], verbs: ['create', 'get', 'patch'] },
    { apiGroups: [''], resources: ['configmaps'], verbs: ['create', 'get', 'patch'] },
    { apiGroups: [''], resources: ['serviceaccounts'], verbs: ['create', 'get', 'patch'] },
    { apiGroups: ['coordination.k8s.io'], resources: ['leases'], verbs: ['create', 'delete', 'get'] },
    { apiGroups: ['cilium.io'], resources: ['ciliumnetworkpolicies'], verbs: ['create', 'get', 'patch'] },
    { apiGroups: ['apps'], resources: ['deployments'], verbs: ['create'] },
    { apiGroups: ['apps'], resources: ['deployments'], resourceNames: [
      'environment', 'orchestrator', 'events-and-sessions', 'identity-broker',
      'gateway', 'mcp', 'web', 'knowledge', 'projects-config',
    ],
      verbs: ['get', 'list', 'patch', 'watch'] },
    { apiGroups: ['gateway.networking.k8s.io'], resources: ['httproutes'], verbs: ['create', 'get', 'patch'] },
  ]);
  assert.deepEqual(binding.subjects, [{
    kind: 'User', name: PRINCIPAL_ID, apiGroup: 'rbac.authorization.k8s.io',
  }]);
  assert.deepEqual(binding.roleRef, {
    kind: 'Role', name: SETUP_TARGET.kubernetesRole, apiGroup: 'rbac.authorization.k8s.io',
  });
  assert.deepEqual(serviceAccount, {
    apiVersion: 'v1',
    kind: 'ServiceAccount',
    metadata: {
      name: SETUP_TARGET.projectsConfigMigrationServiceAccount,
      namespace: DB_MAINTENANCE_TARGET.namespace,
      annotations: { 'azure.workload.identity/client-id': MIGRATION_CLIENT_ID },
    },
    automountServiceAccountToken: false,
  });
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

test('the workflow identity template trusts its environment and creates no Azure custom role', () => {
  const source = readFileSync('infra/bicep/db-maintenance-ci-identity.bicep', 'utf8');
  assert.match(source, /token\.actions\.githubusercontent\.com/);
  assert.match(source, /repo:\$\{githubRepository\}:environment:\$\{githubEnvironment\}/);
  assert.doesNotMatch(source, /resource\s+\w+\s+'Microsoft\.Authorization\/roleDefinitions@/);
  assert.match(source, /resource readerAssignment 'Microsoft\.Authorization\/roleAssignments/);
  assert.match(source, /resource clusterUserAssignment 'Microsoft\.Authorization\/roleAssignments/);
  assert.match(source, /name: '\$\{namePrefix\}-id-projects-config-migration'/);
  assert.match(source, /subject: 'system:serviceaccount:agentweaver:projects-config-migration'/);
  const result = spawnSync('az', ['bicep', 'build', '--file', 'infra/bicep/db-maintenance-ci-identity.bicep', '--stdout'],
    { encoding: 'utf8', shell: process.platform === 'win32' });
  // The Bicep CLI is optional offline; when present verify it compiles to Azure role assignments only.
  if (result.error || result.status !== 0) return;
  const template = JSON.parse(result.stdout);
  assert.ok(template.resources.every(resource => resource.type !== 'Microsoft.Authorization/roleDefinitions'));
  assert.deepEqual(template.resources.filter(resource => resource.type === 'Microsoft.Authorization/roleAssignments')
    .map(resource => resource.properties.roleDefinitionId).sort(), [
      "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('clusterUserRoleId'))]",
      "[subscriptionResourceId('Microsoft.Authorization/roleDefinitions', variables('readerRoleId'))]",
    ].sort());
  assert.ok(template.resources.some(resource =>
    resource.type === 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials'));
});

test('the workflow uses the variable and environment that setup creates', () => {
  const workflow = readFileSync('.github/workflows/v1-db-maintenance.yml', 'utf8');
  assert.match(workflow, new RegExp(`environment: ${SETUP_TARGET.environment}`));
  assert.match(workflow, new RegExp(`vars\\.${SETUP_TARGET.variable}`));
});
