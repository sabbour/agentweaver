/**
 * One-time, repeatable setup for v1 database maintenance. It makes everything that routine runs
 * need exist and checks it afterward:
 *   - the workflow's Azure identity, its GitHub federated credential, and its role assignments;
 *   - the P1 migration identity, its ServiceAccount and egress, and the schema ownership handover;
 *   - the GitHub environment, its v1 branch rule, and the client ID variable.
 * Without --execute it only reads and reports what is missing. With --execute it applies only what
 * is missing and verifies the result. A person who is the PostgreSQL Entra administrator, can assign
 * roles in the resource group, and administers the repository runs it once.
 */
import { randomUUID } from 'node:crypto';
import { isDeepStrictEqual } from 'node:util';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { run, redact } from './exec.mjs';
import { DB_MAINTENANCE_TARGET, runDbMaintenance } from './db-maintenance.mjs';

/** Names that the workflow, the Bicep template, and this setup must agree on. */
export const SETUP_TARGET = Object.freeze({
  repository: 'sabbour/agentweaver',
  environment: 'v1-db-maintenance',
  branch: 'v1',
  variable: 'AZURE_DB_MAINTENANCE_CLIENT_ID',
  identity: 'agentweaver-id-db-maintenance',
  projectsConfigMigrationIdentity: 'agentweaver-id-projects-config-migration',
  projectsConfigMigrationServiceAccount: 'projects-config-migration',
  deploymentName: 'db-maintenance-ci-identity',
  kubernetesRole: 'db-maintenance-workflow',
  kubernetesRoleBinding: 'db-maintenance-workflow',
  template: join('infra', 'bicep', 'db-maintenance-ci-identity.bicep'),
  githubIssuer: 'https://token.actions.githubusercontent.com',
});
// Azure role names, as Azure reports them, and the scope each must be assigned at.
const ROLES = [
  { role: 'Reader', scope: 'resourceGroup' },
  { role: 'Azure Kubernetes Service Cluster User Role', scope: 'cluster' },
];
const GUID = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

export const SETUP_USAGE = `Usage: npm run db:maintenance:setup -- [--execute] [--v1-deploy-only] [--receipt-dir <path>]

Without --execute the command only checks the setup and lists what is missing; it exits 1 if
anything is missing. With --execute it creates only the missing parts and checks them again.
Run it signed in to Azure as the PostgreSQL Entra administrator and to GitHub as a repository admin.
Use --v1-deploy-only to provision the approved v1 deployment identity and namespace permissions without running P1 maintenance setup.
`;

/** Parses flags. Unknown flags stop the command before anything runs. */
export function parseSetupArgs(argv) {
  const options = { execute: false, v1DeployOnly: false, help: false, receiptDir: join(homedir(), '.agentweaver', 'db-maintenance') };
  for (let index = 0; index < argv.length; index++) {
    const arg = argv[index];
    if (arg === '--execute') options.execute = true;
    else if (arg === '--v1-deploy-only') options.v1DeployOnly = true;
    else if (arg === '--help' || arg === '-h') options.help = true;
    else if (arg === '--receipt-dir' || arg.startsWith('--receipt-dir=')) {
      const value = arg.includes('=') ? arg.slice(arg.indexOf('=') + 1) : argv[++index];
      if (!value || value.startsWith('--')) throw new Error('--receipt-dir requires a value.');
      options.receiptDir = value;
    } else throw new Error(`Unknown argument "${arg}".`);
  }
  return options;
}

/** The Azure scopes the role assignments live at. */
export function setupScopes(target = DB_MAINTENANCE_TARGET) {
  const resourceGroup = `/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}`;
  const cluster = `${resourceGroup}/providers/Microsoft.ContainerService/managedClusters/${target.clusterName}`;
  return { resourceGroup, cluster };
}

export function workflowAuthorizationResources(principalObjectId, projectsConfigMigrationClientId) {
  if (!GUID.test(principalObjectId ?? '') || !GUID.test(projectsConfigMigrationClientId ?? ''))
    throw new Error('Workflow authorization requires the exact maintenance principal and Projects & Config migration client IDs.');
  // Job list/watch keeps cleanup reliable; Deployment access is limited to the v1 rollout names.
  const namespace = DB_MAINTENANCE_TARGET.namespace;
  const roleName = SETUP_TARGET.kubernetesRole;
  return [
    {
      apiVersion: 'rbac.authorization.k8s.io/v1',
      kind: 'Role',
      metadata: { name: roleName, namespace },
      rules: [
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
        ], verbs: ['get', 'list', 'patch', 'watch'] },
        { apiGroups: ['gateway.networking.k8s.io'], resources: ['httproutes'], verbs: ['create', 'get', 'patch'] },
      ],
    },
    {
      apiVersion: 'rbac.authorization.k8s.io/v1',
      kind: 'RoleBinding',
      metadata: { name: SETUP_TARGET.kubernetesRoleBinding, namespace },
      subjects: [{
        kind: 'User',
        name: principalObjectId,
        apiGroup: 'rbac.authorization.k8s.io',
      }],
      roleRef: {
        kind: 'Role',
        name: roleName,
        apiGroup: 'rbac.authorization.k8s.io',
      },
    },
    {
      apiVersion: 'v1',
      kind: 'ServiceAccount',
      metadata: {
        name: SETUP_TARGET.projectsConfigMigrationServiceAccount,
        namespace,
        annotations: {
          'azure.workload.identity/client-id': projectsConfigMigrationClientId,
        },
      },
      automountServiceAccountToken: false,
    },
  ];
}

function withSetupKubectl(exec, target, action) {
  const scratch = join(process.cwd(), '.agentweaver-scratch');
  mkdirSync(scratch, { recursive: true });
  const directory = join(scratch, `db-maintenance-setup-${process.pid}-${randomUUID()}`);
  mkdirSync(directory, { recursive: true });
  const kubeconfig = join(directory, 'config');
  const must = (result, operation) => {
    if (result.status !== 0)
      throw new Error(`${operation}: ${redact((result.stderr || result.stdout || `exit ${result.status}`).trim())}`);
    return result;
  };
  let failure;
  let value;
  try {
    // Use the setup operator's Azure CLI identity; the workflow identity is not an administrator.
    must(exec('az', ['aks', 'get-credentials', '--resource-group', target.resourceGroup, '--name', target.clusterName,
      '--subscription', target.subscriptionId, '--file', kubeconfig]), 'Could not get AKS credentials');
    must(exec('kubelogin', ['convert-kubeconfig', '--login', 'azurecli', '--kubeconfig', kubeconfig]),
      'Could not configure AKS kubeconfig');
    value = action((args, options = {}) =>
      exec('kubectl', ['--kubeconfig', kubeconfig, ...args], options));
  } catch (error) {
    failure = error;
  }
  try {
    rmSync(directory, { recursive: true, force: true });
  } catch (cleanupError) {
    const message = redact(cleanupError.message);
    if (failure) throw new Error(`${failure.message}; temporary kubeconfig cleanup failed: ${message}`);
    throw new Error(`Temporary kubeconfig cleanup failed: ${message}`);
  }
  if (failure) throw failure;
  return value;
}

function applyWorkflowAuthorization(exec, target, principalObjectId, projectsConfigMigrationClientId) {
  // Server-side apply makes namespace authorization and the migration identity annotation repeatable.
  const resources = workflowAuthorizationResources(principalObjectId, projectsConfigMigrationClientId);
  withSetupKubectl(exec, target, kubectl => {
    for (const resource of resources) {
      const applied = kubectl(['apply', '--server-side', '--field-manager', 'agentweaver-db-maintenance-setup', '-f', '-'],
        { input: JSON.stringify(resource) });
      if (applied.status !== 0)
        throw new Error(`Could not apply Kubernetes ${resource.kind} ${resource.metadata.name}: ` +
          `${redact((applied.stderr || applied.stdout || `exit ${applied.status}`).trim())}`);
    }
  });
}

/**
 * Reads the workflow identity and the GitHub settings, and returns every gap as a short sentence.
 * It never changes anything. A read error other than "not found" throws, so the command never
 * reports a guess as a fact.
 */
export function readSetupState(exec) {
  const target = DB_MAINTENANCE_TARGET;
  const gaps = [];
  const azureGaps = [];
  const kubernetesGaps = [];
  const githubGaps = [];
  const state = {};
  const addGap = (category, gap) => {
    category.push(gap);
    gaps.push(gap);
  };
  const json = (result, action) => {
    if (result.status !== 0) throw new Error(`${action}: ${redact((result.stderr || result.stdout || '').trim())}`);
    return JSON.parse(result.stdout || 'null');
  };
  const notFound = result => result.status !== 0 && /not ?found|could not be found|HTTP 404/i.test(`${result.stderr}${result.stdout}`);

  // The cluster template enables both authorizers: Azure RBAC handles Azure roles, and Kubernetes
  // RBAC evaluates the narrow Role/RoleBinding that this setup applies for the maintenance identity.
  const authorization = json(exec('az', ['aks', 'show', '--resource-group', target.resourceGroup,
    '--name', target.clusterName, '--subscription', target.subscriptionId,
    '--query', '{azureRbac:aadProfile.enableAzureRbac,kubernetesRbac:enableRbac,oidcIssuer:oidcIssuerProfile.issuerURL}', '-o', 'json']),
  'Could not read AKS authorization settings');
  state.kubernetesAuthorization = authorization;
  if (authorization?.azureRbac !== true || authorization?.kubernetesRbac !== true)
    addGap(kubernetesGaps, 'AKS must have both Azure RBAC for Kubernetes and Kubernetes RBAC enabled.');

  // Workflow identity and its GitHub federated credential.
  const shown = exec('az', ['identity', 'show', '--resource-group', target.resourceGroup, '--name', SETUP_TARGET.identity, '-o', 'json']);
  if (notFound(shown)) addGap(azureGaps, `Azure identity ${SETUP_TARGET.identity} does not exist.`);
  else {
    const identity = json(shown, `Could not read ${SETUP_TARGET.identity}`);
    state.clientId = identity.clientId;
    state.principalObjectId = identity.principalId;
    const credentials = json(exec('az', ['identity', 'federated-credential', 'list', '--resource-group', target.resourceGroup,
      '--identity-name', SETUP_TARGET.identity, '-o', 'json']), 'Could not read its federated credentials');
    const subject = `repo:${SETUP_TARGET.repository}:environment:${SETUP_TARGET.environment}`;
    if (!(credentials ?? []).some(credential => credential.issuer === SETUP_TARGET.githubIssuer &&
        credential.subject === subject && (credential.audiences ?? []).includes('api://AzureADTokenExchange')))
      addGap(azureGaps, `No federated credential lets GitHub environment ${SETUP_TARGET.environment} sign in.`);
    // Role assignments, each at its exact scope. A broader assignment elsewhere is not counted,
    // so the check matches what the template grants.
    const scopes = setupScopes(target);
    for (const { role, scope } of ROLES) {
      const assigned = json(exec('az', ['role', 'assignment', 'list', '--assignee', identity.principalId,
        '--scope', scopes[scope], '--query', '[].{role:roleDefinitionName,scope:scope}', '-o', 'json']),
      `Could not read role assignments at ${scopes[scope]}`);
      if (!(assigned ?? []).some(entry => entry.role === role && entry.scope.toLowerCase() === scopes[scope].toLowerCase()))
        addGap(azureGaps, `Role "${role}" is not assigned at the ${scope} scope.`);
    }
    if (authorization?.azureRbac === true && authorization?.kubernetesRbac === true) {
      if (!GUID.test(identity.principalId ?? '')) throw new Error('The workflow identity has no principal object ID.');
      const migrationShown = exec('az', [
        'identity', 'show', '--resource-group', target.resourceGroup,
        '--name', SETUP_TARGET.projectsConfigMigrationIdentity, '-o', 'json',
      ]);
      if (notFound(migrationShown)) {
        addGap(azureGaps, `Azure identity ${SETUP_TARGET.projectsConfigMigrationIdentity} does not exist.`);
      }
      const migrationIdentity = notFound(migrationShown)
        ? undefined : json(migrationShown, `Could not read ${SETUP_TARGET.projectsConfigMigrationIdentity}`);
      if (migrationIdentity) {
        state.projectsConfigMigrationClientId = migrationIdentity.clientId;
        state.projectsConfigMigrationPrincipalObjectId = migrationIdentity.principalId;
        const credentials = json(exec('az', [
          'identity', 'federated-credential', 'list', '--resource-group', target.resourceGroup,
          '--identity-name', SETUP_TARGET.projectsConfigMigrationIdentity, '-o', 'json',
        ]), `Could not read ${SETUP_TARGET.projectsConfigMigrationIdentity} federated credentials`);
        const subject = `system:serviceaccount:${target.namespace}:${SETUP_TARGET.projectsConfigMigrationServiceAccount}`;
        if (!(credentials ?? []).some(credential => credential.issuer === authorization?.oidcIssuer &&
            credential.subject === subject && (credential.audiences ?? []).includes('api://AzureADTokenExchange')))
          addGap(azureGaps, `${SETUP_TARGET.projectsConfigMigrationIdentity} is not federated to its exact AKS ServiceAccount.`);
        if (!GUID.test(migrationIdentity.clientId ?? '') || !GUID.test(migrationIdentity.principalId ?? ''))
          throw new Error(`${SETUP_TARGET.projectsConfigMigrationIdentity} has no valid client or principal object ID.`);
      }
      const [expectedRole, expectedBinding, expectedServiceAccount] = workflowAuthorizationResources(
        identity.principalId, migrationIdentity?.clientId ?? '00000000-0000-0000-0000-000000000000');
      const actual = withSetupKubectl(exec, target, kubectl => {
        const read = (kind, name) => {
          const result = kubectl(['get', kind, name, '--namespace', target.namespace, '-o', 'json']);
          if (notFound(result)) return undefined;
          return json(result, `Could not read Kubernetes ${kind} ${name}`);
        };
        return {
          role: read('role', expectedRole.metadata.name),
          binding: read('rolebinding', expectedBinding.metadata.name),
          serviceAccount: read('serviceaccount', expectedServiceAccount.metadata.name),
        };
      });
      const roleMatches = actual.role?.metadata?.name === expectedRole.metadata.name &&
        actual.role.metadata.namespace === expectedRole.metadata.namespace &&
        isDeepStrictEqual(actual.role.rules, expectedRole.rules);
      if (!roleMatches)
        addGap(kubernetesGaps, `Kubernetes Role "${expectedRole.metadata.name}" is missing or has different permissions.`);
      const bindingMatches = actual.binding?.metadata?.name === expectedBinding.metadata.name &&
        actual.binding.metadata.namespace === expectedBinding.metadata.namespace &&
        isDeepStrictEqual(actual.binding.subjects ?? [], expectedBinding.subjects) &&
        isDeepStrictEqual(actual.binding.roleRef, expectedBinding.roleRef);
      if (!bindingMatches)
        addGap(kubernetesGaps, `Kubernetes RoleBinding "${expectedBinding.metadata.name}" is missing or has a different subject.`);
      const serviceAccountMatches = actual.serviceAccount?.metadata?.name === expectedServiceAccount.metadata.name &&
        actual.serviceAccount.metadata.namespace === expectedServiceAccount.metadata.namespace &&
        actual.serviceAccount.automountServiceAccountToken === false &&
        actual.serviceAccount.metadata.annotations?.['azure.workload.identity/client-id'] ===
          expectedServiceAccount.metadata.annotations['azure.workload.identity/client-id'];
      if (!serviceAccountMatches)
        addGap(kubernetesGaps, `ServiceAccount "${expectedServiceAccount.metadata.name}" is missing the approved migration identity annotation.`);
    }
  }

  // GitHub environment: it must exist and admit only the v1 branch.
  const repo = `repos/${SETUP_TARGET.repository}/environments/${SETUP_TARGET.environment}`;
  const environment = exec('gh', ['api', repo]);
  if (notFound(environment)) addGap(githubGaps, `GitHub environment ${SETUP_TARGET.environment} does not exist.`);
  else {
    const policy = json(environment, 'Could not read the GitHub environment').deployment_branch_policy;
    const branches = json(exec('gh', ['api', `${repo}/deployment-branch-policies`]), 'Could not read its branch rules');
    const names = (branches?.branch_policies ?? []).map(entry => `${entry.type ?? 'branch'}:${entry.name}`);
    if (!policy?.custom_branch_policies || policy.protected_branches || names.length !== 1 || names[0] !== `branch:${SETUP_TARGET.branch}`)
      addGap(githubGaps, `GitHub environment ${SETUP_TARGET.environment} must admit only the ${SETUP_TARGET.branch} branch.`);
    const variable = exec('gh', ['variable', 'get', SETUP_TARGET.variable, '--env', SETUP_TARGET.environment,
      '--repo', SETUP_TARGET.repository]);
    if (variable.status !== 0 && !notFound(variable)) json(variable, 'Could not read the GitHub variable');
    state.variable = variable.status === 0 ? variable.stdout.trim() : undefined;
    if (!state.clientId || state.variable !== state.clientId)
      addGap(githubGaps, `GitHub variable ${SETUP_TARGET.variable} does not hold the workflow identity client ID.`);
  }
  return { ...state, gaps, azureGaps, kubernetesGaps, githubGaps };
}

function defaultDependencies() {
  return {
    exec: (command, args, options = {}) => run(command, args, { check: false, timeout: 120_000, ...options }),
    newRunId: () => randomUUID().replaceAll('-', '').slice(0, 12),
    now: () => new Date(),
    runner: runDbMaintenance,
    runnerDependencies: {},
    writeReceipt: (path, receipt) => writeFileSync(path, `${JSON.stringify(receipt, null, 2)}\n`),
    log: message => console.log(message),
  };
}

/**
 * Runs the setup. Returns the receipt; throws after writing a failed receipt. The P1 part reuses
 * the maintenance runner in prepare-only mode, so its preflight, lock, cleanup, and receipt apply.
 */
export async function runDbMaintenanceSetup(options, overrides = {}) {
  const deps = { ...defaultDependencies(), ...overrides };
  const repoRoot = deps.repoRoot ?? process.cwd();
  const { exec, log } = deps;
  const target = DB_MAINTENANCE_TARGET;
  const runId = deps.newRunId();
  const receipt = { runId, kind: 'setup', mode: options.execute ? 'execute' : 'verify', startedAt: deps.now().toISOString(),
    steps: [], result: 'running' };
  const step = (name, detail = {}) => { receipt.steps.push({ name, ...detail }); log(`- ${name}`); };
  const must = (result, action) => {
    if (result.status !== 0) throw new Error(`${action}: ${redact((result.stderr || result.stdout || `exit ${result.status}`).trim())}`);
    return result;
  };
  let primaryError;
  try {
    // 1. Accounts. Setup runs under the PostgreSQL Entra administrator's Azure CLI sign-in,
    // which is a user account. The workflow identity is not that administrator and holds no
    // role-assignment rights, so a service principal sign-in fails here before any change.
    const account = JSON.parse(must(exec('az', ['account', 'show', '-o', 'json']), 'Azure account lookup').stdout);
    if (account.id !== target.subscriptionId || account.tenantId !== target.tenantId)
      throw new Error('The signed-in Azure account is not on the approved subscription and tenant.');
    if (account.user?.type !== 'user') throw new Error('Run the setup with the Azure CLI signed in as the PostgreSQL Entra administrator, not a service principal.');
    receipt.operator = account.user.name;
    const admin = must(exec('gh', ['api', `repos/${SETUP_TARGET.repository}`, '--jq', '.permissions.admin']),
      'Could not read GitHub repository permissions').stdout.trim();
    if (admin !== 'true') throw new Error(`The GitHub sign-in is not an admin of ${SETUP_TARGET.repository}.`);
    step('accounts-verified');

    // 2. P1 identity, ServiceAccount, egress, and handover: the runner's own dry run says what is
    // missing, and its prepare-only execute run creates it. Both are safe to repeat.
    let p1Gaps = [];
    if (options.v1DeployOnly) {
      step('p1-setup-skipped');
    } else {
      const prepare = await deps.runner({ execute: options.execute, prepareOnly: true, handover: 'auto',
        reconcileBrokerEgress: false, allowAppImageMismatch: false, deployApps: false, appsOnly: false,
        receiptDir: options.receiptDir, jobTimeoutSeconds: 900 }, deps.runnerDependencies);
      receipt.prepare = { runId: prepare.runId, result: prepare.result, receiptPath: prepare.receiptPath };
      p1Gaps = options.execute ? [] : (prepare.plannedSteps ?? [])
        .filter(name => name === 'migration-identity-deploy' || name === 'handover-run')
        .map(name => name === 'handover-run' ? 'The schema ownership handover has not run for the migration identity.'
          : 'The P1 migration identity or its federated credential is missing.');
      step(options.execute ? 'p1-prepared' : 'p1-checked', { gaps: p1Gaps });
    }

    // 3. Workflow identity and GitHub settings.
    let current = readSetupState(exec);
    if (!options.execute) {
      receipt.gaps = [...p1Gaps, ...current.gaps];
      for (const gap of receipt.gaps) log(`missing: ${gap}`);
      receipt.result = receipt.gaps.length ? 'setup-needed' : 'verified';
      if (receipt.gaps.length) throw new Error(`Setup is incomplete (${receipt.gaps.length} gaps). Run with --execute.`);
      return receipt;
    }

    if (current.kubernetesAuthorization?.azureRbac !== true ||
        current.kubernetesAuthorization?.kubernetesRbac !== true)
      throw new Error('AKS must have both Azure RBAC for Kubernetes and Kubernetes RBAC enabled before setup can continue.');

    // The template is declarative: deploying it again changes nothing, and it creates only the
    // identity, federation, Reader, and Cluster User assignment. Kubernetes RBAC is applied below.
    if (current.azureGaps.length) {
      const head = must(exec('git', ['rev-parse', 'HEAD'], { cwd: repoRoot }), 'Could not read HEAD').stdout.trim();
      const tags = JSON.parse(must(exec('az', ['group', 'show', '--name', target.resourceGroup, '--query', 'tags', '-o', 'json']),
        'Could not read resource group tags').stdout || '{}') ?? {};
      const outputs = JSON.parse(must(exec('az', ['deployment', 'group', 'create', '--resource-group', target.resourceGroup,
        '--name', SETUP_TARGET.deploymentName, '--template-file', SETUP_TARGET.template,
        '--parameters', `tags=${JSON.stringify(tags)}`, `sourceSha=${head}`,
        '--query', 'properties.outputs', '-o', 'json'], { cwd: repoRoot, timeout: 600_000 }),
      'Workflow identity deployment failed').stdout);
      const maintenanceIdentity = outputs.maintenanceIdentity?.value;
      const projectsConfigMigrationIdentity = outputs.projectsConfigMigrationIdentity?.value;
      if (maintenanceIdentity?.name !== SETUP_TARGET.identity || !GUID.test(maintenanceIdentity?.clientId ?? '') ||
          projectsConfigMigrationIdentity?.name !== SETUP_TARGET.projectsConfigMigrationIdentity ||
          !GUID.test(projectsConfigMigrationIdentity?.clientId ?? '') ||
          !GUID.test(projectsConfigMigrationIdentity?.principalObjectId ?? ''))
        throw new Error('The workflow identity deployment returned an unexpected identity.');
      step('workflow-identities-deployed', {
        clientId: maintenanceIdentity.clientId,
        projectsConfigMigrationClientId: projectsConfigMigrationIdentity.clientId,
      });
      current = readSetupState(exec);
    }
    if (!GUID.test(current.clientId ?? '')) throw new Error('The workflow identity has no client ID.');
    if (current.kubernetesGaps.length) {
      if (!GUID.test(current.principalObjectId ?? '')) throw new Error('The workflow identity has no principal object ID.');
      applyWorkflowAuthorization(exec, target, current.principalObjectId, current.projectsConfigMigrationClientId);
      step('workflow-kubernetes-authorization-applied');
      current = readSetupState(exec);
    }

    // GitHub environment with a v1-only branch rule, then the client ID variable. PUT on the
    // environment is idempotent; the branch rule is added only when absent, and other rules fail
    // closed rather than being deleted.
    const repo = `repos/${SETUP_TARGET.repository}/environments/${SETUP_TARGET.environment}`;
    must(exec('gh', ['api', '--method', 'PUT', repo, '--input', '-'], { input: JSON.stringify({
      deployment_branch_policy: { protected_branches: false, custom_branch_policies: true } }) }),
    'Could not create the GitHub environment');
    const branches = JSON.parse(must(exec('gh', ['api', `${repo}/deployment-branch-policies`]), 'Could not read branch rules').stdout);
    const rules = branches.branch_policies ?? [];
    if (rules.some(rule => rule.name !== SETUP_TARGET.branch || (rule.type ?? 'branch') !== 'branch'))
      throw new Error(`GitHub environment ${SETUP_TARGET.environment} has branch rules other than ${SETUP_TARGET.branch}. ` +
        'Remove them and run the setup again.');
    if (!rules.length)
      must(exec('gh', ['api', '--method', 'POST', `${repo}/deployment-branch-policies`, '-f', `name=${SETUP_TARGET.branch}`,
        '-f', 'type=branch']), 'Could not add the v1 branch rule');
    must(exec('gh', ['variable', 'set', SETUP_TARGET.variable, '--env', SETUP_TARGET.environment,
      '--repo', SETUP_TARGET.repository, '--body', current.clientId]), 'Could not set the GitHub variable');
    step('github-environment-ready');

    // 4. Read everything back. Any remaining gap fails the setup.
    const verified = readSetupState(exec);
    receipt.gaps = verified.gaps;
    receipt.workflowIdentity = { clientId: verified.clientId, principalObjectId: verified.principalObjectId };
    if (verified.gaps.length) throw new Error(`Setup did not converge: ${verified.gaps.join(' ')}`);
    step('setup-verified');
    receipt.result = 'succeeded';
  } catch (error) {
    primaryError = error;
    if (receipt.result === 'running') receipt.result = 'failed';
    receipt.error = redact(error.message);
  } finally {
    // The receipt is written on every path, next to the maintenance receipts and outside the checkout.
    receipt.finishedAt = deps.now().toISOString();
    const directory = resolve(repoRoot, options.receiptDir);
    mkdirSync(directory, { recursive: true });
    receipt.receiptPath = join(directory, `setup-${runId}.json`);
    deps.writeReceipt(receipt.receiptPath, receipt);
    log(`Receipt: ${receipt.receiptPath} (${receipt.result})`);
  }
  if (primaryError) throw primaryError;
  return receipt;
}
