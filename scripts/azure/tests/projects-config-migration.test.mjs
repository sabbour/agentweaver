import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import {
  parseProjectsConfigMigrationArgs, resolveProjectsConfigImage, runProjectsConfigMigration,
} from '../lib/projects-config-migration.mjs';
import { DB_MAINTENANCE_TARGET, LOCK_NAME, STATE_NAME } from '../lib/db-maintenance.mjs';
import { assertP0KubernetesPermissions } from '../lib/namespace-bootstrap.mjs';
import { parseDeployKustomizeArgs } from '../../release/deploy-kustomize.mjs';
import { parseDeployFromCommitArgs } from '../../release/deploy-from-commit.mjs';

const deploymentSourceSha = 'c'.repeat(40);
const imageDigest = `sha256:${'1'.repeat(64)}`;
const image = 'ghcr.io/sabbour/agentweaver.projects.config@' + imageDigest;
const bootstrapSubject = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
const bootstrapTenantId = '11111111-2222-4333-8444-555555555555';
const bootstrapIssuer = 'https://agentweaver.synthetic.example/';
const inspectionMembershipId = '33333333-4444-4555-8666-777777777777';
const inspectionAssignmentId = '22222222-3333-4444-8555-666666666666';
const inspectionOutput = [
  'PLATFORM_ADMIN_INSPECTION_V1',
  `MEMBERSHIP|Active|${inspectionMembershipId}|2`,
  'ACTIVE_ASSIGNMENT_COUNT|1',
  `ASSIGNMENT|${inspectionAssignmentId}|3|Active|Platform|default|PlatformAdmin|${inspectionMembershipId}`,
  'MEMBERSHIP_AUDIT_COUNT|1',
  `AUDIT|membership_granted|-|-|2026-10-23T00:00:00.000000Z|${inspectionMembershipId}`,
  'ROLE_ASSIGNMENT_AUDIT_COUNT|1',
  `AUDIT|role_assigned|Platform|default|PlatformAdmin|2026-10-23T00:00:01.000000Z|${inspectionMembershipId}|${inspectionAssignmentId}`,
  'OTHER_ACTIVE_PLATFORM_ADMIN_COUNT|1',
].join('\n');
function createWorld({
  jobOutcome = 'Complete',
  bootstrapLog = 'The first PlatformAdmin was created.',
  tenantBootstrapLog = 'The first TenantAdmin was created.',
  inspectionLog = [
    'PLATFORM_ADMIN_INSPECTION_V1',
    'MEMBERSHIP|Absent|-|0',
    'ACTIVE_ASSIGNMENT_COUNT|0',
    'MEMBERSHIP_AUDIT_COUNT|0',
    'ROLE_ASSIGNMENT_AUDIT_COUNT|0',
    'OTHER_ACTIVE_PLATFORM_ADMIN_COUNT|0',
  ].join('\n'),
  configMap = {
    kind: 'ConfigMap',
    metadata: { name: 'projects-config-runtime-config', namespace: DB_MAINTENANCE_TARGET.namespace },
    immutable: true,
    data: {
      ProjectsConfig__Authentication__Authority: bootstrapIssuer,
      ProjectsConfig__PrivilegedSetup__ApprovedIssuer: bootstrapIssuer,
      ProjectsConfig__PrivilegedSetup__ApprovedSubject: bootstrapSubject,
      ProjectsConfig__PrivilegedSetup__ApprovedTenantId: bootstrapTenantId,
    },
  },
  migrationServiceAccountClientId = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
  expectedMigrationClientId = migrationServiceAccountClientId,
  originV1 = deploymentSourceSha,
  projectsDeployment = null,
  projectsPods = [],
  supportsConsumeGuard = sha => sha === deploymentSourceSha,
} = {}) {
  const directory = path.join(
    process.cwd(), '.test-scratch', `projects-config-migration-test-${randomUUID()}`);
  mkdirSync(directory, { recursive: true });
  const calls = [];
  const kubeconfigOptions = [];
  const objects = new Map();
  const receipts = [];
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
    if (command === 'az' && args[0] === 'identity' && args[1] === 'show') {
      return { status: 0, stdout: `${expectedMigrationClientId}\n`, stderr: '' };
    }
    if (command === 'git') {
      if (['status', 'fetch', 'merge-base'].includes(args[0])) return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') {
        return { status: 0, stdout: `${deploymentSourceSha}\n`, stderr: '' };
      }
      if (args[0] === 'rev-parse' && args[1] === 'origin/v1') {
        return { status: 0, stdout: `${originV1}\n`, stderr: '' };
      }
      if (args[0] === 'cat-file' && args[1] === '-e') {
        const sha = args[2].split(':')[0];
        return { status: supportsConsumeGuard(sha) ? 0 : 1, stdout: '', stderr: '' };
      }
      if (args[0] === 'show') {
        const sha = args[1].split(':')[0];
        if (!supportsConsumeGuard(sha)) return { status: 1, stdout: '', stderr: '' };
        if (args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
          return {
            status: 0,
            stdout: readFileSync(path.join(process.cwd(), 'scripts', 'azure', 'lib', 'p1-postgres-plan.json'), 'utf8'),
            stderr: '',
          };
        }
        return {
          status: 0,
          stdout: readFileSync(
            path.join(process.cwd(), 'services', 'projects-config', 'Agentweaver.Projects.Config', 'Program.cs'),
            'utf8',
          ),
          stderr: '',
        };
      }
      throw new Error(`unexpected git ${args.join(' ')}`);
    }
    if (command !== 'kubectl') throw new Error(`unexpected command ${command}`);
    if (args.includes('get') && args.includes('serviceaccount')) {
      return {
        status: 0,
        stdout: JSON.stringify({
          kind: 'ServiceAccount',
          metadata: {
            name: 'projects-config-migration',
            namespace: DB_MAINTENANCE_TARGET.namespace,
            annotations: { 'azure.workload.identity/client-id': migrationServiceAccountClientId },
          },
        }),
        stderr: '',
      };
    }
    if (args.includes('create') && args.includes('--filename')) {
      const object = JSON.parse(settings.input);
      if (object.kind === 'Lease') {
        object.metadata = {
          ...object.metadata,
          uid: object.metadata?.uid ?? 'lease-uid-1',
          resourceVersion: object.metadata?.resourceVersion ?? '1',
        };
      }
      objects.set(`${object.kind.toLowerCase()}/${object.metadata.name}`, object);
      return { status: 0, stdout: `${object.kind}/${object.metadata.name} created`, stderr: '' };
    }
    if (args.includes('get') && args.includes('lease')) {
      if (args.some(arg => arg.startsWith('--output=jsonpath='))) {
        return { status: 0, stdout: objects.get(`lease/${LOCK_NAME}`)?.spec?.holderIdentity ?? '', stderr: '' };
      }
      return objects.has(`lease/${LOCK_NAME}`)
        ? { status: 0, stdout: JSON.stringify(objects.get(`lease/${LOCK_NAME}`)), stderr: '' }
        : { status: 1, stdout: '', stderr: 'Error from server (NotFound): leases not found' };
    }
    if (args.includes('get') && args.includes('configmap')) {
      if (args.includes(STATE_NAME)) {
        const state = objects.get(`configmap/${STATE_NAME}`);
        return { status: 0, stdout: state ? JSON.stringify(state) : '', stderr: '' };
      }
      return { status: 0, stdout: JSON.stringify(configMap), stderr: '' };
    }
    if (args.includes('patch') && args.includes('configmap') && args.includes(STATE_NAME)) {
      const existing = objects.get(`configmap/${STATE_NAME}`) ?? {
        apiVersion: 'v1',
        kind: 'ConfigMap',
        metadata: { name: STATE_NAME, namespace: DB_MAINTENANCE_TARGET.namespace },
        data: {},
      };
      const patch = JSON.parse(args[args.indexOf('--patch') + 1]);
      existing.data = { ...existing.data, ...patch.data };
      objects.set(`configmap/${STATE_NAME}`, existing);
      return { status: 0, stdout: `configmap/${STATE_NAME} patched`, stderr: '' };
    }
    if (args.includes('get') && args.includes('deployment') && args.includes('projects-config')) {
      return { status: 0, stdout: projectsDeployment ? JSON.stringify(projectsDeployment) : '', stderr: '' };
    }
    if (args.includes('scale') && args.includes('deployment/projects-config')) {
      if (projectsDeployment) {
        projectsDeployment.spec.replicas = 0;
      }
      projectsPods.length = 0;
      return { status: 0, stdout: 'deployment.apps/projects-config scaled', stderr: '' };
    }
    if (args.includes('get') && args.includes('jobs')) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: [] }), stderr: '' };
    }
    if (args.includes('get') && args.includes('job')) {
      const name = args[args.indexOf('job') + 1];
      const job = objects.get(`job/${name}`);
      return {
        status: 0,
        stdout: JSON.stringify({ ...job, status: { conditions: [{ type: jobOutcome, status: 'True' }] } }),
        stderr: '',
      };
    }
    if (args.includes('annotate') && args.includes('deployment/projects-config')) {
      return { status: 0, stdout: 'deployment.apps/projects-config annotated', stderr: '' };
    }
    if (args.includes('rollout') && args.includes('status')) {
      return { status: 0, stdout: 'deployment "projects-config" successfully rolled out', stderr: '' };
    }
    if (args.includes('logs')) {
      const output = args.some(argument => argument.includes('tenant-admin-bootstrap'))
        ? tenantBootstrapLog
        : args.some(argument => argument.includes('platform-admin-inspection'))
          ? inspectionLog : bootstrapLog;
      return { status: 0, stdout: `${output}\n`, stderr: '' };
    }
    if (args.includes('get') && args.includes('pods')) {
      const isRunSelector = args.some(a => a.includes('agentweaver.io/db-maintenance-run='));
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: isRunSelector ? [] : projectsPods }), stderr: '' };
    }
    if (args.includes('delete')) {
      const rawIdx = args.indexOf('--raw');
      if (rawIdx >= 0) {
        const rawPath = args[rawIdx + 1];
        const expectedLeasePath = `/apis/coordination.k8s.io/v1/namespaces/${DB_MAINTENANCE_TARGET.namespace}/leases/${LOCK_NAME}`;
        if (rawPath !== expectedLeasePath || !args.includes('--filename') || args[args.indexOf('--filename') + 1] !== '-') {
          return { status: 1, stdout: '', stderr: `unexpected raw delete ${rawPath}` };
        }
        const current = objects.get(`lease/${LOCK_NAME}`);
        let body;
        try {
          body = JSON.parse(settings?.input ?? '{}');
        } catch {
          return { status: 1, stdout: '', stderr: 'invalid DeleteOptions JSON' };
        }
        if (body.apiVersion !== 'v1' || body.kind !== 'DeleteOptions' ||
            !current ||
            body.preconditions?.uid !== current.metadata?.uid ||
            body.preconditions?.resourceVersion !== current.metadata?.resourceVersion) {
          return { status: 1, stdout: '', stderr: 'Error from server (Conflict): UID or resourceVersion mismatch' };
        }
        objects.delete(`lease/${LOCK_NAME}`);
        return { status: 0, stdout: '', stderr: '' };
      }
      const kind = args[args.indexOf('delete') + 1];
      const name = args[args.indexOf(kind) + 1];
      objects.delete(`${kind}/${name}`);
      return { status: 0, stdout: '', stderr: '' };
    }
    throw new Error(`unexpected kubectl ${args.join(' ')}`);
  };
  return {
    calls, kubeconfigOptions, objects, receipts,
    options: parseProjectsConfigMigrationArgs([
      '--image', image, '--receipt-dir', path.join(directory, 'receipts'),
    ]),
    overrides: {
      root: path.join(directory, 'checkout'),
      exec,
      readFile: file => readFileSync(file.endsWith('job.yaml')
          ? path.join(process.cwd(), 'deploy', 'k8s', 'migrations', 'projects-config', 'job.yaml')
          : file, 'utf8'),
      verifyImage: async () => ({ digest: imageDigest }),
      withKubeconfig: async (_target, action, settings) => {
        kubeconfigOptions.push(settings);
        return action(['--kubeconfig', 'offline']);
      },
      newRunId: () => 'abcdef123456',
      now: () => new Date('2026-10-23T00:00:00.000Z'),
      sleep: async () => {},
      log: () => {},
      deleteLease: async ({ baseArgs, apiPath, uid, resourceVersion }) => {
        assert.deepEqual(baseArgs, ['--kubeconfig', 'offline']);
        assert.equal(apiPath, `/apis/coordination.k8s.io/v1/namespaces/${DB_MAINTENANCE_TARGET.namespace}/leases/${LOCK_NAME}`);
        const current = objects.get(`lease/${LOCK_NAME}`);
        if (!current || uid !== current.metadata?.uid || resourceVersion !== current.metadata?.resourceVersion) {
          throw new Error('Conflict: UID or resourceVersion mismatch');
        }
        objects.delete(`lease/${LOCK_NAME}`);
      },
      writeReceipt: (file, receipt) => receipts.push({ file, receipt: structuredClone(receipt) }),
    },
    dispose: () => rmSync(directory, { recursive: true, force: true }),
  };
}

function withMigrationCredentials(callback, connection) {
  const oldConnection = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
  const oldToken = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
  process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION = connection ??
    `Host=${DB_MAINTENANCE_TARGET.postgresHost};Database=agentweaver;` +
    'Username=agentweaver-id-projects-config-migration;SSL Mode=VerifyFull';
  process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN = 'test-access-token';
  return Promise.resolve().then(callback).finally(() => {
    if (oldConnection === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION = oldConnection;
    if (oldToken === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN = oldToken;
  });
}

function withoutMigrationCredentials(callback) {
  const oldConnection = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
  const oldToken = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
  delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
  delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
  return Promise.resolve().then(callback).finally(() => {
    if (oldConnection === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION = oldConnection;
    if (oldToken === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN = oldToken;
  });
}

test('migration arguments are dry-run by default and require an immutable image', () => {
  assert.equal(parseProjectsConfigMigrationArgs(['--help']).help, true);
  assert.throws(() => parseProjectsConfigMigrationArgs([]), /--image is required/);
  assert.throws(() => parseProjectsConfigMigrationArgs(['--image', 'x', '--reset']), /unknown argument/);
  assert.throws(() => parseProjectsConfigMigrationArgs(['--image', 'x']), /immutable Projects & Config GHCR reference/);
  assert.equal(parseProjectsConfigMigrationArgs(['--image', image]).execute, false);
  assert.throws(() => parseProjectsConfigMigrationArgs(['--publication', 'receipt.json']), /unknown argument/);
  assert.equal(parseProjectsConfigMigrationArgs([
    '--image', image, '--bootstrap-first-tenant-admin',
  ]).bootstrapFirstTenantAdmin, true);
  assert.throws(() => parseProjectsConfigMigrationArgs([
    '--image', image, '--bootstrap-first-tenant-admin', '--bootstrap-first-platform-admin',
  ]), /mutually exclusive/);
  assert.equal(parseProjectsConfigMigrationArgs([
    '--image', image, '--bootstrap-first-platform-admin',
  ]).bootstrapFirstPlatformAdmin, true);
  assert.equal(parseProjectsConfigMigrationArgs([
    '--image', image, '--inspect-first-platform-admin',
  ]).inspectFirstPlatformAdmin, true);
  assert.throws(() => parseProjectsConfigMigrationArgs([
    '--image', image, '--inspect-first-platform-admin', '--execute',
  ]), /must not be combined with --execute/);
  assert.throws(() => parseProjectsConfigMigrationArgs([
    '--image', image, '--inspect-first-platform-admin', '--bootstrap-first-platform-admin',
  ]), /mutually exclusive/);
});

test('default Projects & Config image verification returns the immutable service digest', async () => {
  const hash = bytes => `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  const configBytes = Buffer.from(JSON.stringify({
    os: 'linux',
    architecture: 'amd64',
    config: { Labels: {
      'org.opencontainers.image.revision': deploymentSourceSha,
      'io.agentweaver.source-tree': 'd'.repeat(40),
      'org.opencontainers.image.version': '1.0.0',
    } },
  }));
  const configDigest = hash(configBytes);
  const manifestBytes = Buffer.from(JSON.stringify({
    schemaVersion: 2,
    mediaType: 'application/vnd.oci.image.manifest.v1+json',
    config: {
      mediaType: 'application/vnd.oci.image.config.v1+json',
      digest: configDigest,
      size: configBytes.length,
    },
  }));
  const manifestDigest = hash(manifestBytes);
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async url => {
    const request = String(url);
    if (request.startsWith('https://ghcr.io/token?')) {
      return { ok: true, status: 200, json: async () => ({ token: 'test-registry-token' }) };
    }
    if (request.endsWith(`/manifests/${manifestDigest}`)) {
      return {
        ok: true, status: 200,
        headers: { get: name => name.toLowerCase() === 'docker-content-digest' ? manifestDigest : null },
        text: async () => manifestBytes.toString('utf8'),
      };
    }
    if (request.endsWith(`/blobs/${configDigest}`)) {
      return { ok: true, status: 200, arrayBuffer: async () => configBytes };
    }
    assert.fail(`unexpected registry request: ${request}`);
  };
  try {
    const verified = await resolveProjectsConfigImage(
      `ghcr.io/sabbour/agentweaver.projects.config@${manifestDigest}`);
    assert.deepEqual(verified, {
      image: `ghcr.io/sabbour/agentweaver.projects.config@${manifestDigest}`,
      digest: manifestDigest,
    });
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('Projects & Config execution stops before Kubernetes writes without its schema-owner identity', async () => {
  const world = createWorld();
  const connection = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
  const accessToken = process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
  delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
  delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
  try {
    await assert.rejects(
      runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides),
      /no approved Projects & Config schema-owner migration identity is configured/,
    );
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'apply', 'delete'].some(verb => call.args.includes(verb))), false);
  } finally {
    if (connection === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_CONNECTION = connection;
    if (accessToken === undefined) delete process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN;
    else process.env.AGENTWEAVER_PROJECTS_CONFIG_MIGRATION_ACCESS_TOKEN = accessToken;
    world.dispose();
  }
});

test('PlatformAdmin inspection runs read-only without an operator token and writes a sanitized receipt', async () => {
  const world = createWorld({ inspectionLog: inspectionOutput });
  try {
    const receipt = await withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, inspectFirstPlatformAdmin: true,
    }, world.overrides));

    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.schemaVersion, 1);
    assert.equal(receipt.mode, 'read-only-inspection');
    assert.equal(receipt.operation, 'inspect-first-platform-admin');
    assert.deepEqual(receipt.platformAdminInspection, {
      outcome: 'inspected',
      membership: { state: 'active', membershipId: inspectionMembershipId, revision: 2 },
      activeAssignments: [{
        assignmentId: inspectionAssignmentId, revision: 3, state: 'active',
        resourceType: 'Platform', resourceId: 'default', role: 'PlatformAdmin',
        membershipId: inspectionMembershipId,
      }],
      membershipAudits: [{
        eventType: 'membership_granted', resourceType: null, resourceId: null,
        timestamp: '2026-10-23T00:00:00.000000Z', membershipId: inspectionMembershipId,
      }],
      roleAssignmentAudits: [{
        eventType: 'role_assigned', resourceType: 'Platform', resourceId: 'default', role: 'PlatformAdmin',
        timestamp: '2026-10-23T00:00:01.000000Z',
        membershipId: inspectionMembershipId, assignmentId: inspectionAssignmentId,
      }],
      otherActivePlatformAdminCount: 1,
    });
    assert.deepEqual(receipt.cleanup, { job: true, lock: true });
    assert.ok(receipt.plannedSteps.includes('create-platform-admin-inspection-job'));
    assert.equal(receipt.plannedSteps.includes('create-migration-job'), false);
    assert.equal(receipt.plannedSteps.includes('apply-runtime-grants'), false);
    assert.equal(receipt.bootstrap, undefined);
    assert.equal(receipt.grants, undefined);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && call.args.includes('create') &&
      JSON.parse(call.settings.input ?? '{}').kind === 'Secret'), false);
    assert.ok(world.calls.some(call => call.command === 'az' &&
      call.args.includes(`${DB_MAINTENANCE_TARGET.resourceGroup}-id-projects-config-migration`)));
    assert.ok(world.kubeconfigOptions[0].requiredPermissions.some(permission =>
      JSON.stringify(permission) === JSON.stringify([
        'get', 'configmaps', DB_MAINTENANCE_TARGET.namespace,
      ])));
    assert.ok(world.kubeconfigOptions[0].requiredPermissions.some(permission =>
      JSON.stringify(permission) === JSON.stringify([
        'get', 'serviceaccounts', DB_MAINTENANCE_TARGET.namespace,
      ])));
    const jobCreate = world.calls.find(call =>
      call.command === 'kubectl' && call.args.includes('create') &&
      JSON.parse(call.settings.input ?? '{}').kind === 'Job');
    const job = JSON.parse(jobCreate.settings.input);
    const container = job.spec.template.spec.containers[0];
    assert.deepEqual(container.command, [
      'dotnet', 'Agentweaver.Projects.Config.dll', '--inspect-first-platform-admin',
    ]);
    assert.equal(container.image, image);
    assert.equal(job.spec.template.spec.serviceAccountName, 'projects-config-migration');
    assert.equal(container.env.find(entry =>
      entry.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId').value,
    'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee');
    assert.equal(container.env.some(entry =>
      entry.name === 'ProjectsConfig__Migration__AccessToken'), false);
    assert.ok(container.env.find(entry =>
      entry.name === 'ConnectionStrings__ProjectsConfigMigration')?.value
      .endsWith(';GSS Encryption Mode=Disable'));
    assert.equal(world.objects.size, 0);
    const safeReceipt = JSON.stringify(receipt);
    for (const secret of [bootstrapSubject, bootstrapTenantId, bootstrapIssuer, 'test-access-token', 'Username=']) {
      assert.equal(safeReceipt.includes(secret), false);
    }
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin inspection rejects spoofed or extra output and still cleans up', async () => {
  const spoofed = `${inspectionOutput}\nissuer=${bootstrapIssuer}`;
  const world = createWorld({ inspectionLog: spoofed });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, inspectFirstPlatformAdmin: true,
    }, world.overrides)), /extra output/);
    const receipt = JSON.stringify(world.receipts[0].receipt);
    assert.equal(receipt.includes(bootstrapIssuer), false);
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin inspection rejects a non-protocol prelude and duplicate protocol output', async () => {
  for (const inspectionLog of [
    `Native loader diagnostic: optional library unavailable\n${inspectionOutput}`,
    `${inspectionOutput}\nPLATFORM_ADMIN_INSPECTION_V1`,
  ]) {
    const world = createWorld({ inspectionLog });
    try {
      await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
        ...world.options, inspectFirstPlatformAdmin: true,
      }, world.overrides)), /expected versioned output|extra output/);
      assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
      assert.equal(world.objects.size, 0);
    } finally {
      world.dispose();
    }
  }
});

test('PlatformAdmin inspection records sanitized command failures', async () => {
  const world = createWorld({
    jobOutcome: 'Failed',
    inspectionLog: [
      'First PlatformAdmin inspection failed. Check the approved identity, migration settings, and database connectivity.',
      'PlatformAdmin inspection diagnostic: ExceptionType=InvalidOperationException; Category=invalid-bootstrap-configuration',
    ].join('\n'),
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, inspectFirstPlatformAdmin: true,
    }, world.overrides)), /inspection command failed; review the sanitized diagnostic/);
    assert.equal(world.receipts[0].receipt.platformAdminInspection.outcome, 'failed');
    assert.equal(world.receipts[0].receipt.platformAdminInspection.diagnostic,
      'ExceptionType=InvalidOperationException; Category=invalid-bootstrap-configuration');
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin bootstrap dry run verifies the approved ConfigMap and avoids writes', async () => {
  const world = createWorld();
  try {
    const receipt = await runProjectsConfigMigration({
      ...world.options, bootstrapFirstPlatformAdmin: true,
    }, world.overrides);
    assert.equal(receipt.result, 'dry-run-passed');
    assert.equal(receipt.schemaVersion, 1);
    assert.equal(receipt.operation, 'bootstrap-first-platform-admin');
    assert.deepEqual(receipt.steps.map(step => step.name), [
      'image-verified', 'migration-lock-checked', 'bootstrap-identity-config-verified',
      'migration-workload-identity-verified',
    ]);
    assert.ok(receipt.plannedSteps.includes('create-platform-admin-bootstrap-job'));
    assert.equal(receipt.plannedSteps.some(step => step.includes('secret')), false);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete', 'apply'].some(verb => call.args.includes(verb))), false);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
  } finally {
    world.dispose();
  }
});

test('TenantAdmin bootstrap uses the verified workload identity and creates no credential Secret', async () => {
  for (const [tenantBootstrapLog, expectedOutcome] of [
    ['The first TenantAdmin was created.', 'created'],
    ['The approved TenantAdmin already exists; no changes were made.', 'already-present'],
  ]) {
    const world = createWorld({ tenantBootstrapLog });
    try {
      const receipt = await withoutMigrationCredentials(() => runProjectsConfigMigration({
        ...world.options, execute: true, bootstrapFirstTenantAdmin: true,
      }, world.overrides));
      assert.equal(receipt.result, 'succeeded');
      assert.equal(receipt.schemaVersion, 1);
      assert.equal(receipt.operation, 'bootstrap-first-tenant-admin');
      assert.deepEqual(receipt.bootstrap, { outcome: expectedOutcome });
      assert.deepEqual(receipt.cleanup, { job: true, lock: true });

      const jobCreate = world.calls.find(call =>
        call.command === 'kubectl' && call.args.includes('create') &&
        JSON.parse(call.settings.input ?? '{}').kind === 'Job');
      const job = JSON.parse(jobCreate.settings.input);
      const container = job.spec.template.spec.containers[0];
      assert.deepEqual(container.command, [
        'dotnet', 'Agentweaver.Projects.Config.dll', '--bootstrap-first-tenant-admin',
      ]);
      assert.equal(container.image, image);
      assert.equal(job.spec.template.spec.serviceAccountName, 'projects-config-migration');
      assert.equal(job.spec.template.spec.automountServiceAccountToken, false);
      assert.equal(container.env.find(entry =>
        entry.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId').value,
      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee');
      assert.equal(container.env.some(entry =>
        entry.name === 'ProjectsConfig__Migration__AccessToken'), false);
      for (const key of [
        'ProjectsConfig__Authentication__Authority',
        'ProjectsConfig__PrivilegedSetup__ApprovedIssuer',
        'ProjectsConfig__PrivilegedSetup__ApprovedSubject',
        'ProjectsConfig__PrivilegedSetup__ApprovedTenantId',
      ]) {
        assert.deepEqual(
          container.env.find(entry => entry.name === key)?.valueFrom?.configMapKeyRef,
          { name: 'projects-config-runtime-config', key });
      }
      assert.equal(world.calls.some(call =>
        call.command === 'kubectl' && call.args.includes('create') &&
        JSON.parse(call.settings.input ?? '{}').kind === 'Secret'), false);
      assert.equal(world.objects.size, 0);
    } finally {
      world.dispose();
    }
  }
});

test('TenantAdmin bootstrap refuses an unexpected workload identity before creating a Job', async () => {
  const world = createWorld({
    migrationServiceAccountClientId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee',
    expectedMigrationClientId: 'bbbbbbbb-cccc-4ddd-8eee-ffffffffffff',
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstTenantAdmin: true,
    }, world.overrides)), /does not name the approved migration identity/);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete'].some(verb => call.args.includes(verb))), false);
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('TenantAdmin bootstrap conflict is recorded and cleaned up without retry', async () => {
  const world = createWorld({
    jobOutcome: 'Failed',
    tenantBootstrapLog:
      'First TenantAdmin bootstrap refused because another active TenantAdmin exists for this tenant.',
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstTenantAdmin: true,
    }, world.overrides)), /refused because another active TenantAdmin exists/);
    assert.deepEqual(world.receipts[0].receipt.bootstrap, { outcome: 'conflict' });
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin bootstrap creates one pinned Job, parses the stable outcome, and cleans up', async () => {
  for (const [bootstrapLog, expectedOutcome] of [
    ['The first PlatformAdmin was created.', 'created'],
    ['The approved PlatformAdmin already exists; no changes were made.', 'already-present'],
  ]) {
    const world = createWorld({ bootstrapLog });
    try {
      const receipt = await withoutMigrationCredentials(() => runProjectsConfigMigration({
        ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
      }, world.overrides));
      assert.equal(receipt.result, 'succeeded');
      assert.equal(receipt.bootstrap.outcome, expectedOutcome);
      assert.equal(receipt.grants, undefined);
      assert.deepEqual(receipt.cleanup, { job: true, lock: true });
      assert.equal(world.calls.some(call => call.command === 'grants'), false);
      assert.deepEqual(
        world.kubeconfigOptions[0].requiredPermissions.find(permission =>
          permission[0] === 'get' && permission[1] === 'pods' && permission[3] === 'log'),
        ['get', 'pods', DB_MAINTENANCE_TARGET.namespace, 'log']);
      const jobCreate = world.calls.find(call =>
        call.command === 'kubectl' && call.args.includes('create') &&
        JSON.parse(call.settings.input ?? '{}').kind === 'Job');
      const job = JSON.parse(jobCreate.settings.input);
      const container = job.spec.template.spec.containers[0];
      assert.deepEqual(container.command, [
        'dotnet', 'Agentweaver.Projects.Config.dll', '--bootstrap-first-platform-admin',
      ]);
      assert.equal(container.image, image);
      assert.equal(job.spec.template.spec.serviceAccountName, 'projects-config-migration');
      assert.equal(container.env.find(entry =>
        entry.name === 'ProjectsConfig__Migration__WorkloadIdentity__ClientId').value,
      'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee');
      assert.equal(container.env.some(entry =>
        entry.name === 'ProjectsConfig__Migration__AccessToken'), false);
      assert.equal(world.calls.some(call =>
        call.command === 'kubectl' && call.args.includes('create') &&
        JSON.parse(call.settings.input ?? '{}').kind === 'Secret'), false);
      for (const key of [
        'ProjectsConfig__Authentication__Authority',
        'ProjectsConfig__PrivilegedSetup__ApprovedIssuer',
        'ProjectsConfig__PrivilegedSetup__ApprovedSubject',
        'ProjectsConfig__PrivilegedSetup__ApprovedTenantId',
      ]) {
        assert.deepEqual(
          container.env.find(entry => entry.name === key)?.valueFrom?.configMapKeyRef,
          { name: 'projects-config-runtime-config', key });
      }
      assert.equal(
        container.env.find(entry =>
          entry.name === 'ProjectsConfig__Authentication__Authority')?.valueFrom?.configMapKeyRef?.key,
        'ProjectsConfig__Authentication__Authority');
      assert.equal(
        container.env.find(entry =>
          entry.name === 'ProjectsConfig__PrivilegedSetup__ApprovedIssuer')?.valueFrom?.configMapKeyRef?.key,
        'ProjectsConfig__PrivilegedSetup__ApprovedIssuer');
      assert.equal(world.objects.size, 0);
      assert.equal(JSON.stringify(receipt).includes(bootstrapSubject), false);
      assert.equal(JSON.stringify(receipt).includes(bootstrapTenantId), false);
      assert.equal(JSON.stringify(receipt).includes(bootstrapIssuer), false);
    } finally {
      world.dispose();
    }
  }
});

test('PlatformAdmin bootstrap failure records its stable outcome and sanitized diagnostic', async () => {
  const world = createWorld({
    jobOutcome: 'Failed',
    bootstrapLog: [
      'First PlatformAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.',
      'PlatformAdmin bootstrap diagnostic: ExceptionType=DbUpdateException; InnerExceptionType=PostgresException; SqlState=23505; ConstraintName=unknown; TableName=unknown',
    ].join('\n'),
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
    }, world.overrides)), /command failed; review the sanitized diagnostic in the receipt/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.equal(world.receipts[0].receipt.bootstrap.outcome, 'failed');
    assert.equal(
      world.receipts[0].receipt.bootstrap.diagnostic,
      'ExceptionType=DbUpdateException; InnerExceptionType=PostgresException; SqlState=23505; ConstraintName=unknown; TableName=unknown');
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin bootstrap rejects untrusted diagnostic text without persisting it', async () => {
  const untrustedMessages = [
    'password="secret with spaces"',
    'authority=https://user:pass@example.test/path',
    'token=first-line\nsecond-line-secret',
  ];

  for (const message of untrustedMessages) {
    const world = createWorld({
      jobOutcome: 'Failed',
      bootstrapLog: [
        'First PlatformAdmin bootstrap failed. Check the approved identity, migration settings, and database connectivity.',
        `PlatformAdmin bootstrap diagnostic: ExceptionType=InvalidOperationException; Category=invalid-bootstrap-configuration; ${message}`,
      ].join('\n'),
    });
    try {
      await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
        ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
      }, world.overrides)), /unexpected diagnostic output/);
      const receipt = JSON.stringify(world.receipts[0].receipt);
      assert.equal(receipt.includes(message), false);
      assert.equal(receipt.includes('secret with spaces'), false);
      assert.equal(receipt.includes('user:pass@'), false);
      assert.equal(receipt.includes('second-line-secret'), false);
    } finally {
      world.dispose();
    }
  }
});

test('PlatformAdmin bootstrap conflict is recorded as a stable outcome and cleaned up', async () => {
  const world = createWorld({
    jobOutcome: 'Failed',
    bootstrapLog: 'First PlatformAdmin bootstrap refused because another active PlatformAdmin exists.',
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
    }, world.overrides)), /refused because another active PlatformAdmin exists/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.deepEqual(world.receipts[0].receipt.bootstrap, { outcome: 'conflict' });
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, lock: true });
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('PlatformAdmin bootstrap fails closed when ConfigMap issuer differs from authority', async () => {
  const world = createWorld({
    configMap: {
      kind: 'ConfigMap',
      metadata: { name: 'projects-config-runtime-config', namespace: DB_MAINTENANCE_TARGET.namespace },
      immutable: true,
      data: {
        ProjectsConfig__Authentication__Authority: 'https://agentweaver.example.aksapp.io/',
        ProjectsConfig__PrivilegedSetup__ApprovedIssuer: 'https://other.example.aksapp.io/',
        ProjectsConfig__PrivilegedSetup__ApprovedSubject: 'approved-subject',
        ProjectsConfig__PrivilegedSetup__ApprovedTenantId: 'approved-tenant',
      },
    },
  });
  try {
    await assert.rejects(withoutMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, bootstrapFirstPlatformAdmin: true,
    }, world.overrides)), /does not match the authentication authority/);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete'].some(verb => call.args.includes(verb))), false);
  } finally {
    world.dispose();
  }
});

test('dry run uses the selected immutable image and makes no writes', async () => {
  const world = createWorld();
  try {
    const receipt = await runProjectsConfigMigration(world.options, world.overrides);
    assert.equal(receipt.result, 'dry-run-passed');
    assert.equal(receipt.sourceSha, deploymentSourceSha);
    assert.equal(receipt.target.namespace, 'agentweaver');
    assert.equal(receipt.publicationSourceSha, undefined);
    assert.equal(receipt.imageSourceSha, undefined);
    assert.deepEqual(receipt.steps.map(step => step.name), ['image-verified', 'migration-lock-checked']);
    assert.equal(receipt.plannedSteps.includes('apply-runtime-grants'), false);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete', 'apply'].some(verb => call.args.includes(verb))), false);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
  } finally {
    world.dispose();
  }
});

test('execute rejects TLS bypass and connection aliases before Kubernetes access', async () => {
  const valid = `Host=${DB_MAINTENANCE_TARGET.postgresHost};Database=agentweaver;` +
    'Username=agentweaver-id-projects-config-migration;SSL Mode=VerifyFull';
  for (const connection of [
    `${valid};Trust Server Certificate=true`,
    `${valid};Server=attacker.example`,
    `${valid};Host=attacker.example`,
  ]) {
    const world = createWorld();
    try {
      await assert.rejects(withMigrationCredentials(
        () => runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides),
        connection,
      ), /migration connection may contain only unique/);
      assert.equal(world.calls.some(call => call.command === 'kubectl'), false);
    } finally {
      world.dispose();
    }
  }
});

test('execute passes credentials only through Secret stdin, cleans up, and records a safe receipt', async () => {
  const world = createWorld();
  try {
    const receipt = await withMigrationCredentials(() =>
      runProjectsConfigMigration({ ...world.options, execute: true }, {
        ...world.overrides,
        verifyExactPairA: async () => true,
      }));
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.schemaVersion, 2);
    assert.deepEqual(receipt.cleanup, { job: true, secret: true, lock: true });
    assert.deepEqual(receipt.steps.map(step => step.name).slice(-1), ['migration-job-completed']);
    const jobReadIndex = world.calls.findLastIndex(call =>
      call.command === 'kubectl' && call.args.includes('get') && call.args.includes('job'));
    const firstDeleteIndex = world.calls.findIndex(call => call.command === 'kubectl' && call.args.includes('delete'));
    assert.ok(jobReadIndex < firstDeleteIndex);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
    assert.equal(receipt.target.subscriptionId, DB_MAINTENANCE_TARGET.subscriptionId);
    assert.equal(receipt.target.resourceGroup, DB_MAINTENANCE_TARGET.resourceGroup);
    assert.equal(JSON.stringify(receipt).includes('test-access-token'), false);
    assert.equal(JSON.stringify(receipt).includes('Username='), false);
    const secretCreate = world.calls.find(call =>
      call.command === 'kubectl' && call.args.includes('create') &&
      JSON.parse(call.settings.input ?? '{}').kind === 'Secret');
    assert.ok(secretCreate);
    assert.equal(secretCreate.args.includes('test-access-token'), false);
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
  }
});

test('failed migration Job cleans up its resources and writes a failed receipt', async () => {
  const world = createWorld({ jobOutcome: 'Failed' });
  try {
    await assert.rejects(withMigrationCredentials(() =>
      runProjectsConfigMigration({ ...world.options, execute: true }, {
        ...world.overrides,
        verifyExactPairA: async () => true,
      })),
    /migration Job .* failed/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, secret: true, lock: true });
    assert.equal(world.objects.size, 0);
    assert.equal(JSON.stringify(world.receipts[0].receipt).includes('test-access-token'), false);
  } finally {
    world.dispose();
  }
});
test('failed migration Job does not apply grants', async () => {
  const world = createWorld({ jobOutcome: 'Failed' });
  try {
    await assert.rejects(withMigrationCredentials(() =>
      runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides)));
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
    assert.equal(world.receipts[0].receipt.grants, undefined);
  } finally {
    world.dispose();
  }
});

test('execute and dry run require the fetched origin/v1 tip before any Kubernetes access', async () => {
  for (const execute of [true, false]) {
    const world = createWorld({ originV1: 'e'.repeat(40) });
    try {
      await assert.rejects(withMigrationCredentials(() =>
        runProjectsConfigMigration({ ...world.options, execute }, world.overrides)),
      /must be the fetched origin\/v1 tip/);
      assert.equal(world.calls.some(call => call.command === 'kubectl'), false);
    } finally {
      world.dispose();
    }
  }
});

test('the receipt directory must be outside the source checkout', async () => {
  const world = createWorld();
  try {
    const options = { ...world.options, receiptDir: path.join(world.overrides.root, 'receipts') };
    await assert.rejects(runProjectsConfigMigration(options, world.overrides), /--receipt-dir must be outside/);
    assert.equal(world.calls.length, 0);
    assert.equal(world.receipts.length, 0);
  } finally {
    world.dispose();
  }
});

test('a separate target uses its own cluster and rejects an unregistered target', async () => {
  assert.throws(() => parseProjectsConfigMigrationArgs(['--image', image, '--resource-group', 'other']),
    /registered install target/);
  const world = createWorld();
  try {
    const targetsFile = path.join(world.overrides.root, 'deploy', 'k8s', 'targets', 'awwus2np01.json');
    mkdirSync(path.dirname(targetsFile), { recursive: true });
    writeFileSync(targetsFile, readFileSync(path.join(process.cwd(), 'deploy', 'k8s', 'targets', 'awwus2np01.json')));
    const targets = [];
    const receipt = await runProjectsConfigMigration({ ...world.options, resourceGroup: 'awwus2np01' }, {
      ...world.overrides,
      withKubeconfig: (target, action, settings) => { targets.push(target); return world.overrides.withKubeconfig(target, action, settings); },
    });
    assert.equal(receipt.result, 'dry-run-passed');
    assert.deepEqual(receipt.target, { subscriptionId: DB_MAINTENANCE_TARGET.subscriptionId, resourceGroup: 'awwus2np01',
      clusterName: 'awwus2np01-aks', namespace: DB_MAINTENANCE_TARGET.namespace });
    assert.deepEqual(targets.map(target => [target.resourceGroup, target.clusterName]), [['awwus2np01', 'awwus2np01-aks']]);
  } finally {
    world.dispose();
  }
});

test('migration scales down incompatible live projects-config deployment, records consume guard, and blocks image-only rollback', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    projectsDeployment: {
      kind: 'Deployment',
      metadata: { name: 'projects-config', namespace: DB_MAINTENANCE_TARGET.namespace },
      spec: {
        replicas: 2,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
    projectsPods: [
      { metadata: { name: 'projects-config-0', labels: { 'app.kubernetes.io/name': 'projects-config' } } },
    ],
  });
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };
  try {
    const receipt = await withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      { ...world.overrides, verifyImage }));
    assert.equal(receipt.result, 'succeeded');
    assert.ok(receipt.steps.some(step => step.name === 'projects-config-scaled-down-for-cutover'));
    assert.ok(receipt.steps.some(step => step.name === 'projects-config-consume-guard-recorded'));
    assert.equal(world.objects.get(`configmap/${STATE_NAME}`)?.data?.['projectsConfig.consumeGuardRequired'], 'true');
    const scaleCallIndex = world.calls.findIndex(call =>
      call.command === 'kubectl' && call.args.includes('scale') && call.args.includes('deployment/projects-config'));
    const jobCreateIndex = world.calls.findIndex(call =>
      call.command === 'kubectl' && call.args.includes('create') &&
      JSON.parse(call.settings.input ?? '{}').kind === 'Job');
    assert.ok(scaleCallIndex >= 0 && jobCreateIndex > scaleCallIndex);

    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      {
        ...parseProjectsConfigMigrationArgs([
          '--image', legacyImage, '--receipt-dir', path.dirname(receipt.receiptPath),
        ]),
        execute: true,
      },
      { ...world.overrides, verifyImage })),
    /Projects & Config rollback guard blocks/);
  } finally {
    world.dispose();
  }
});

test('v9: acquires Lease before state/scale, persists Pending before Job, and prints exact follow-up commands', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    projectsDeployment: {
      kind: 'Deployment',
      metadata: { name: 'projects-config', namespace: DB_MAINTENANCE_TARGET.namespace },
      spec: {
        replicas: 2,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
    projectsPods: [
      { metadata: { name: 'projects-config-0', labels: { 'app.kubernetes.io/name': 'projects-config' } } },
    ],
  });
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };
  const logs = [];
  try {
    const receipt = await withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      { ...world.overrides, verifyImage, log: msg => logs.push(msg) }));
    assert.equal(receipt.result, 'succeeded');

    const leaseCreateIdx = world.calls.findIndex(c =>
      c.command === 'kubectl' && c.args.includes('create') && JSON.parse(c.settings.input ?? '{}').kind === 'Lease');
    const stateReadIdx = world.calls.findIndex(c =>
      c.command === 'kubectl' && c.args.includes('get') && c.args.includes('configmap') && c.args.includes(STATE_NAME));
    const scaleIdx = world.calls.findIndex(c =>
      c.command === 'kubectl' && c.args.includes('scale') && c.args.includes('deployment/projects-config'));
    const pendingPatchIdx = world.calls.findIndex(c =>
      c.command === 'kubectl' && (c.args.includes('create') || c.args.includes('patch')) &&
      (c.settings.input ?? c.args.join(' ')).includes('consumeGuardPending'));
    const jobCreateIdx = world.calls.findIndex(c =>
      c.command === 'kubectl' && c.args.includes('create') && JSON.parse(c.settings.input ?? '{}').kind === 'Job');
    assert.ok(leaseCreateIdx >= 0 && leaseCreateIdx < stateReadIdx, 'Lease must be created before state read');
    assert.ok(leaseCreateIdx < scaleIdx, 'Lease must be created before scale-down');
    assert.ok(pendingPatchIdx >= 0 && pendingPatchIdx < jobCreateIdx, 'consumeGuardPending must be persisted before Job');

    assert.equal(receipt.followUpCommands.length, 2);
    assert.match(receipt.followUpCommands[0], /^npm run deploy:kustomize -- --image-manifest <projects-config-manifest\.json> --resource-group agentweaver --execute$/);
    assert.match(receipt.followUpCommands[1], new RegExp(`^npm run azure:deploy-from-commit -- ${deploymentSourceSha} --projects-config-only --resource-group agentweaver --execute$`));
    assert.ok(logs.some(l => l.includes(receipt.followUpCommands[0]) && l.includes(receipt.followUpCommands[1])));
    assert.ok(logs.some(l => l.includes(`"agentweaver.projects.config":"${imageDigest}"`)));

    const kustomizeArgs = receipt.followUpCommands[0].replace(/^npm run deploy:kustomize -- /, '').split(/\s+/);
    const parsedKustomize = parseDeployKustomizeArgs(kustomizeArgs);
    assert.equal(parsedKustomize.imageManifest, '<projects-config-manifest.json>');

    const commitArgs = receipt.followUpCommands[1].replace(/^npm run azure:deploy-from-commit -- /, '').split(/\s+/);
    const parsedCommit = parseDeployFromCommitArgs(commitArgs);
    assert.equal(parsedCommit.sourceSha, deploymentSourceSha);
    assert.equal(parsedCommit.projectsConfigOnly, true);

    const perms = world.kubeconfigOptions[0].requiredPermissions;
    for (const expected of [
      ['patch', 'deployments.apps'],
      ['patch', 'deployments.apps', 'scale'],
      ['list', 'deployments.apps'],
      ['watch', 'deployments.apps'],
      ['get', 'pods'],
      ['watch', 'pods'],
      ['get', 'pods', 'log'],
    ]) {
      assert.ok(
        perms.some(p => p[0] === expected[0] && p[1] === expected[1] && (expected[2] === undefined || p[3] === expected[2])),
        `missing required permission ${expected.join(' ')}`,
      );
    }
    assert.throws(
      () => assertP0KubernetesPermissions(
        ['--kubeconfig', 'offline'],
        args => (args.includes('deployments.apps') && args.includes('--subresource=scale')
          ? { status: 0, stdout: 'no\n', stderr: '' }
          : { status: 0, stdout: 'yes\n', stderr: '' }),
        perms,
        'Projects & Config database migration',
      ),
      /patch deployments\.apps \(subresource scale\)/,
    );
  } finally {
    world.dispose();
  }
});

test('v9: failed-before-commit settles Job first, verifies exact Pair A, clears Pending, and restores replicas', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: { name: 'projects-config', namespace: DB_MAINTENANCE_TARGET.namespace },
      spec: {
        replicas: 2,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
    projectsPods: [
      { metadata: { name: 'projects-config-0', labels: { 'app.kubernetes.io/name': 'projects-config' } } },
    ],
  });
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };
  const events = [];
  const baseExec = world.overrides.exec;
  const trackingExec = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('delete') && args.includes('job')) events.push('delete-job');
    if (cmd === 'kubectl' && args.includes('scale') && args.includes('--replicas=2')) events.push('restore-replicas');
    if (cmd === 'kubectl' && args.includes('patch') && args.join(' ').includes('consumeGuardPending":null')) {
      events.push('clear-pending');
    }
    return baseExec(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      {
        ...world.overrides,
        exec: trackingExec,
        verifyImage,
        verifyExactPairA: async () => {
          events.push('verify-pair-a');
          return true;
        },
      })),
    /migration Job .* failed/);
    assert.deepEqual(events, ['delete-job', 'verify-pair-a', 'clear-pending', 'restore-replicas']);
    assert.equal(world.objects.get(`configmap/${STATE_NAME}`)?.data?.['projectsConfig.consumeGuardPending'], null);
    assert.equal(world.receipts[0].receipt.cleanup.lock, true);
  } finally {
    world.dispose();
  }
});

test('v9: retains Lease and Pending when Job cleanup fails or Pair A is unproven', async () => {
  const world = createWorld({ jobOutcome: 'Failed' });
  const baseExec = world.overrides.exec;
  const failingJobDeleteExec = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('delete') && args.includes('job')) {
      return { status: 1, stdout: '', stderr: 'delete job timed out' };
    }
    return baseExec(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      {
        ...world.overrides,
        exec: failingJobDeleteExec,
        verifyImage: async () => ({ digest: imageDigest, sourceSha: deploymentSourceSha }),
        verifyExactPairA: async () => { throw new Error('must not run while Job is unsettled'); },
      })),
    /Cleanup failed/);
    assert.ok(world.objects.has('lease/p1-db-maintenance-lock'), 'Lease must be retained when Job cleanup fails');
    assert.equal(world.objects.get(`configmap/${STATE_NAME}`)?.data?.['projectsConfig.consumeGuardPending'], 'true');
    assert.equal(world.receipts[0].receipt.cleanup.lock, false);

    // --release-lock refuses mismatched holder, succeeds for exact holder once settled.
    await assert.rejects(
      runProjectsConfigMigration(parseProjectsConfigMigrationArgs([
        '--release-lock', 'wrongrunid00', '--receipt-dir', path.dirname(world.options.receiptDir) + '/receipts2',
      ]), world.overrides),
      /held by abcdef123456, not wrongrunid00/);
    const releaseReceipt = await runProjectsConfigMigration(parseProjectsConfigMigrationArgs([
      '--release-lock', 'abcdef123456', '--receipt-dir', path.dirname(world.options.receiptDir) + '/receipts2',
    ]), world.overrides);
    assert.equal(releaseReceipt.result, 'succeeded');
    assert.equal(world.objects.has('lease/p1-db-maintenance-lock'), false);
  } finally {
    world.dispose();
  }
});

test('v9: production Pair A verifier Job replaces command without --migrate, includes list jobs permission, and handles mismatch', async () => {
  const world = createWorld({ jobOutcome: 'Failed' });
  const baseExec = world.overrides.exec;
  const createdJobs = [];
  const exec = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('create') && args.includes('--filename')) {
      const doc = JSON.parse(settings.input ?? '{}');
      if (doc.kind === 'Job') createdJobs.push(doc);
    }
    if (cmd === 'kubectl' && args.includes('get') && args.includes('job') &&
        args.some(a => a.includes('paira'))) {
      const name = args[args.indexOf('job') + 1];
      const job = world.objects.get(`job/${name}`);
      return {
        status: 0,
        stdout: JSON.stringify({ ...job, status: { conditions: [{ type: 'Complete', status: 'True' }] } }),
        stderr: '',
      };
    }
    if (cmd === 'kubectl' && args.includes('logs') && args.some(a => a.includes('paira'))) {
      return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
    }
    return baseExec(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      {
        ...world.overrides,
        exec,
        verifyImage: async () => ({ digest: imageDigest, sourceSha: deploymentSourceSha }),
      })),
    /migration Job .* failed/);
    const pairAJob = createdJobs.find(j => j.metadata.name.includes('paira'));
    assert.ok(pairAJob, 'Pair A verification Job must be created without override');
    assert.deepEqual(pairAJob.spec.template.spec.containers[0].command, [
      'dotnet', 'Agentweaver.Projects.Config.dll', '--verify-projects-config-pair-a',
    ]);
    assert.equal(pairAJob.spec.template.spec.containers[0].args, undefined);
    assert.equal(pairAJob.metadata.labels['agentweaver.io/db-maintenance-run'], 'abcdef123456');
    assert.equal(pairAJob.spec.template.metadata.labels['agentweaver.io/db-maintenance-run'], 'abcdef123456');
    assert.ok(world.kubeconfigOptions[0].requiredPermissions.some(
      p => p[0] === 'list' && p[1] === 'jobs'));
  } finally {
    world.dispose();
  }
});

test('v9: --release-lock preserves a changed Lease UID/resourceVersion and checks list jobs permission', async () => {
  const world = createWorld();
  world.objects.set(`lease/${LOCK_NAME}`, {
    apiVersion: 'coordination.k8s.io/v1',
    kind: 'Lease',
    metadata: { name: LOCK_NAME, uid: 'uid-old', resourceVersion: '10' },
    spec: { holderIdentity: 'abcdef123456' },
  });
  const racingDeleteLease = async opts => {
    // Simulate another run replacing the Lease between read and delete.
    world.objects.set(`lease/${LOCK_NAME}`, {
      apiVersion: 'coordination.k8s.io/v1',
      kind: 'Lease',
      metadata: { name: LOCK_NAME, uid: 'uid-new', resourceVersion: '11' },
      spec: { holderIdentity: 'other-run-999' },
    });
    return world.overrides.deleteLease(opts);
  };
  try {
    await assert.rejects(
      runProjectsConfigMigration(parseProjectsConfigMigrationArgs([
        '--release-lock', 'abcdef123456', '--receipt-dir', path.dirname(world.options.receiptDir) + '/receipts2',
      ]), { ...world.overrides, deleteLease: racingDeleteLease }),
      /UID\/resourceVersion preconditions/);
    assert.equal(world.objects.get(`lease/${LOCK_NAME}`)?.metadata?.uid, 'uid-new');
    assert.ok(world.kubeconfigOptions[0].requiredPermissions.some(
      p => p[0] === 'list' && p[1] === 'jobs'));
  } finally {
    world.dispose();
  }
});

test('v9: recovers scaled-down deployment if consumeGuardPending write fails only when exact Pair A is proven', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };

  for (const pairAProven of [true, false]) {
    const world = createWorld({
      projectsDeployment: {
        kind: 'Deployment',
        metadata: { name: 'projects-config', namespace: DB_MAINTENANCE_TARGET.namespace },
        spec: {
          replicas: 2,
          template: { spec: { containers: [{ image: legacyImage }] } },
        },
      },
      projectsPods: [
        { metadata: { name: 'projects-config-0', labels: { 'app.kubernetes.io/name': 'projects-config' } } },
      ],
    });
    const baseExec = world.overrides.exec;
    const failingPendingExec = (cmd, args, settings) => {
      if (cmd === 'kubectl' && args.includes('create') &&
          (settings?.input ?? '').includes('consumeGuardPending')) {
        return { status: 1, stdout: '', stderr: 'simulated configmap write failure' };
      }
      return baseExec(cmd, args, settings);
    };
    try {
      await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
        { ...world.options, execute: true },
        {
          ...world.overrides,
          exec: failingPendingExec,
          verifyImage,
          verifyExactPairA: async () => pairAProven,
        })),
      /simulated configmap write failure/);
      const restored = world.calls.some(c =>
        c.command === 'kubectl' && c.args.includes('scale') && c.args.includes('--replicas=2'));
      assert.equal(restored, pairAProven);
    } finally {
      world.dispose();
    }
  }
});

test('v9: interrupted retry at zero replicas reads saved annotation and restores prior replicas on Pair A recovery', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: {
        replicas: 0,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
    projectsPods: [],
  });
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      {
        ...world.overrides,
        verifyImage,
        verifyExactPairA: async () => true,
      })),
    /migration Job .* failed/);
    assert.ok(world.calls.some(c =>
      c.command === 'kubectl' && c.args.includes('scale') && c.args.includes('--replicas=2')));
  } finally {
    world.dispose();
  }
});

test('v9: inherited consumeGuardPending is cleared on Pair A recovery even when this run fails before writing Pending', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: {
        replicas: 0,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
  });
  world.objects.set(`configmap/${STATE_NAME}`, {
    apiVersion: 'v1',
    kind: 'ConfigMap',
    metadata: { name: STATE_NAME, namespace: DB_MAINTENANCE_TARGET.namespace },
    data: { 'projectsConfig.consumeGuardPending': 'true' },
  });
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };
  const baseExec = world.overrides.exec;
  let patchCount = 0;
  const failFirstPendingPatchExec = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('patch') && args.includes(STATE_NAME) &&
        args.join(' ').includes('"projectsConfig.consumeGuardPending":"true"')) {
      patchCount += 1;
      if (patchCount === 1) {
        return { status: 1, stdout: '', stderr: 'simulated pending patch failure' };
      }
    }
    return baseExec(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world.options, execute: true },
      {
        ...world.overrides,
        exec: failFirstPendingPatchExec,
        verifyImage,
        verifyExactPairA: async () => true,
      })),
    /simulated pending patch failure/);
    assert.equal(world.objects.get(`configmap/${STATE_NAME}`)?.data?.['projectsConfig.consumeGuardPending'], null);
    assert.ok(world.calls.some(c =>
      c.command === 'kubectl' && c.args.includes('scale') && c.args.includes('--replicas=2')));
  } finally {
    world.dispose();
  }
});

test('v9: Pair A-to-Pair A standalone migration does not scale down projects-config', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const world = createWorld({
    projectsDeployment: {
      kind: 'Deployment',
      metadata: { name: 'projects-config', namespace: DB_MAINTENANCE_TARGET.namespace },
      spec: {
        replicas: 2,
        template: { spec: { containers: [{ image: legacyImage }] } },
      },
    },
    projectsPods: [
      { metadata: { name: 'projects-config-0', labels: { 'app.kubernetes.io/name': 'projects-config' } } },
    ],
  });
  try {
    const receipt = await withMigrationCredentials(() => runProjectsConfigMigration(
      parseProjectsConfigMigrationArgs([
        '--image', legacyImage, '--receipt-dir', world.options.receiptDir, '--execute',
      ]),
      {
        ...world.overrides,
        verifyImage: async () => ({ digest: legacyDigest, sourceSha: legacySourceSha }),
        verifyExactPairA: async () => true,
      }));
    assert.equal(receipt.result, 'succeeded');
    assert.equal(world.calls.some(c => c.command === 'kubectl' && c.args.includes('scale')), false);
    assert.equal(receipt.followUpCommands, undefined);
  } finally {
    world.dispose();
  }
});

test('v9: Pair A verifier Job delete failure and restore readiness timeout each retain Lock', async () => {
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);
  const verifyImage = async targetImage => targetImage === legacyImage
    ? { digest: legacyDigest, sourceSha: legacySourceSha }
    : { digest: imageDigest, sourceSha: deploymentSourceSha };

  // 1. Pair A verifier Job delete failure retains Lock and stops recovery.
  const world1 = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: { replicas: 0, template: { spec: { containers: [{ image: legacyImage }] } } },
    },
  });
  const base1 = world1.overrides.exec;
  const exec1 = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('get') && args.includes('job') && args.some(a => a.includes('paira'))) {
      const name = args[args.indexOf('job') + 1];
      const job = world1.objects.get(`job/${name}`);
      return { status: 0, stdout: JSON.stringify({ ...job, status: { conditions: [{ type: 'Complete', status: 'True' }] } }), stderr: '' };
    }
    if (cmd === 'kubectl' && args.includes('logs') && args.some(a => a.includes('paira'))) {
      return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
    }
    if (cmd === 'kubectl' && args.includes('delete') && args.includes('job') && args.some(a => a.includes('paira'))) {
      return { status: 1, stdout: '', stderr: 'delete paira job failed' };
    }
    return base1(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world1.options, execute: true },
      { ...world1.overrides, exec: exec1, verifyImage })),
    /could not clean up Pair A verification Job/);
    assert.ok(world1.objects.has(`lease/${LOCK_NAME}`), 'Lock must be retained when Pair A Job delete fails');
    assert.equal(world1.receipts[0].receipt.cleanup.lock, false);
    assert.equal(world1.calls.some(c => c.command === 'kubectl' && c.args.includes('scale')), false);
  } finally {
    world1.dispose();
  }

  // 2. Replica restore readiness timeout retains Lock.
  const world2 = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: { replicas: 0, template: { spec: { containers: [{ image: legacyImage }] } } },
    },
  });
  const base2 = world2.overrides.exec;
  const exec2 = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('rollout') && args.includes('status')) {
      return { status: 1, stdout: '', stderr: 'timed out waiting for rollout' };
    }
    return base2(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world2.options, execute: true },
      { ...world2.overrides, exec: exec2, verifyImage, verifyExactPairA: async () => true })),
    /timed out waiting for rollout/);
    assert.ok(world2.objects.has(`lease/${LOCK_NAME}`), 'Lock must be retained when restored replicas are unready');
    assert.equal(world2.receipts[0].receipt.cleanup.lock, false);
  } finally {
    world2.dispose();
  }

  // 3. Pair A verifier grant-contract failure (e.g. missing revoked_at) blocks clearing Pending and restoring replicas.
  const world3 = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: { replicas: 0, template: { spec: { containers: [{ image: legacyImage }] } } },
    },
  });
  world3.objects.set(`configmap/${STATE_NAME}`, {
    apiVersion: 'v1',
    kind: 'ConfigMap',
    metadata: { name: STATE_NAME, namespace: DB_MAINTENANCE_TARGET.namespace },
    data: { 'projectsConfig.consumeGuardPending': 'true' },
  });
  const base3 = world3.overrides.exec;
  const exec3 = (cmd, args, settings) => {
    if (cmd === 'kubectl' && args.includes('get') && args.includes('job') && args.some(a => a.includes('paira'))) {
      const name = args[args.indexOf('job') + 1];
      const job = world3.objects.get(`job/${name}`);
      return { status: 0, stdout: JSON.stringify({ ...job, status: { conditions: [{ type: 'Complete', status: 'True' }] } }), stderr: '' };
    }
    if (cmd === 'kubectl' && args.includes('logs') && args.some(a => a.includes('paira'))) {
      return { status: 0, stdout: 'PROJECTS_CONFIG_PAIR_A_NOT_PROVEN:runtime_grants_mismatch_pair_a\n', stderr: '' };
    }
    return base3(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      { ...world3.options, execute: true },
      { ...world3.overrides, exec: exec3, verifyImage })),
    /migration Job .* failed/);
    assert.equal(world3.objects.get(`configmap/${STATE_NAME}`)?.data?.['projectsConfig.consumeGuardPending'], 'true');
    assert.equal(world3.calls.some(c => c.command === 'kubectl' && c.args.includes('scale')), false);
  } finally {
    world3.dispose();
  }
});

test('v9: capable migration image lacking verifier uses explicit --verifier-image or fails closed', async () => {
  const noVerifierSha = 'd'.repeat(40);
  const noVerifierDigest = `sha256:${'3'.repeat(64)}`;
  const noVerifierImage = `ghcr.io/sabbour/agentweaver.projects.config@${noVerifierDigest}`;
  const verifierSha = 'e'.repeat(40);
  const verifierDigest = `sha256:${'4'.repeat(64)}`;
  const verifierImage = `ghcr.io/sabbour/agentweaver.projects.config@${verifierDigest}`;
  const legacyDigest = `sha256:${'2'.repeat(64)}`;
  const legacyImage = `ghcr.io/sabbour/agentweaver.projects.config@${legacyDigest}`;
  const legacySourceSha = 'b'.repeat(40);

  const verifyImage = async targetImage => {
    if (targetImage === noVerifierImage) return { digest: noVerifierDigest, sourceSha: noVerifierSha };
    if (targetImage === verifierImage) return { digest: verifierDigest, sourceSha: verifierSha };
    return { digest: legacyDigest, sourceSha: legacySourceSha };
  };

  // 1. Without --verifier-image: capable migration image lacking --verify-projects-config-pair-a does not create a verifier Job or restore replicas.
  const world1 = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: { replicas: 0, template: { spec: { containers: [{ image: legacyImage }] } } },
    },
  });
  const base1 = world1.overrides.exec;
  const exec1 = (cmd, args, settings) => {
    if (cmd === 'git' && args[0] === 'cat-file' && args[1] === '-e' && args[2].startsWith(noVerifierSha)) {
      return { status: 0, stdout: '', stderr: '' };
    }
    if (cmd === 'git' && args[0] === 'show' && args[1].startsWith(noVerifierSha)) {
      if (args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
        return {
          status: 0,
          stdout: readFileSync(path.join(process.cwd(), 'scripts', 'azure', 'lib', 'p1-postgres-plan.json'), 'utf8'),
          stderr: '',
        };
      }
      return { status: 0, stdout: 'static void Main() {}', stderr: '' };
    }
    return base1(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      parseProjectsConfigMigrationArgs([
        '--image', noVerifierImage, '--receipt-dir', world1.options.receiptDir, '--execute',
      ]),
      { ...world1.overrides, exec: exec1, verifyImage })),
    /migration Job .* failed/);
    assert.equal(world1.calls.some(c => c.command === 'kubectl' && c.args.includes('create') &&
      (c.settings?.input ?? '').includes('--verify-projects-config-pair-a')), false);
    assert.equal(world1.calls.some(c => c.command === 'kubectl' && c.args.includes('scale')), false);
  } finally {
    world1.dispose();
  }

  // 2. With --verifier-image: preserves explicit verifier image and restores replicas when Pair A is proven.
  const world2 = createWorld({
    jobOutcome: 'Failed',
    projectsDeployment: {
      kind: 'Deployment',
      metadata: {
        name: 'projects-config',
        namespace: DB_MAINTENANCE_TARGET.namespace,
        annotations: { 'agentweaver.io/local-deploy-restore-replicas': '2' },
      },
      spec: { replicas: 0, template: { spec: { containers: [{ image: legacyImage }] } } },
    },
  });
  const base2 = world2.overrides.exec;
  const createdJobs = [];
  const exec2 = (cmd, args, settings) => {
    if (cmd === 'git' && args[0] === 'cat-file' && args[1] === '-e' &&
        (args[2].startsWith(noVerifierSha) || args[2].startsWith(verifierSha))) {
      return { status: 0, stdout: '', stderr: '' };
    }
    if (cmd === 'git' && args[0] === 'show' && args[1].startsWith(noVerifierSha)) {
      if (args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
        return {
          status: 0,
          stdout: readFileSync(path.join(process.cwd(), 'scripts', 'azure', 'lib', 'p1-postgres-plan.json'), 'utf8'),
          stderr: '',
        };
      }
      return { status: 0, stdout: 'static void Main() {}', stderr: '' };
    }
    if (cmd === 'git' && args[0] === 'show' && args[1].startsWith(verifierSha)) {
      if (args[1].endsWith(':scripts/azure/lib/p1-postgres-plan.json')) {
        return {
          status: 0,
          stdout: readFileSync(path.join(process.cwd(), 'scripts', 'azure', 'lib', 'p1-postgres-plan.json'), 'utf8'),
          stderr: '',
        };
      }
      return {
        status: 0,
        stdout: readFileSync(
          path.join(process.cwd(), 'services', 'projects-config', 'Agentweaver.Projects.Config', 'Program.cs'),
          'utf8',
        ),
        stderr: '',
      };
    }
    if (cmd === 'kubectl' && args.includes('create') && args.includes('--filename')) {
      const doc = JSON.parse(settings.input ?? '{}');
      if (doc.kind === 'Job') createdJobs.push(doc);
    }
    if (cmd === 'kubectl' && args.includes('get') && args.includes('job') && args.some(a => a.includes('paira'))) {
      const name = args[args.indexOf('job') + 1];
      const job = world2.objects.get(`job/${name}`);
      return { status: 0, stdout: JSON.stringify({ ...job, status: { conditions: [{ type: 'Complete', status: 'True' }] } }), stderr: '' };
    }
    if (cmd === 'kubectl' && args.includes('logs') && args.some(a => a.includes('paira'))) {
      return { status: 0, stdout: 'PROJECTS_CONFIG_EXACT_PAIR_A\n', stderr: '' };
    }
    return base2(cmd, args, settings);
  };
  try {
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration(
      parseProjectsConfigMigrationArgs([
        '--image', noVerifierImage, '--verifier-image', verifierImage,
        '--receipt-dir', world2.options.receiptDir, '--execute',
      ]),
      { ...world2.overrides, exec: exec2, verifyImage })),
    /migration Job .* failed/);
    const pairAJob = createdJobs.find(j => j.metadata.name.includes('paira'));
    assert.equal(pairAJob?.spec.template.spec.containers[0].image, verifierImage);
    assert.ok(world2.calls.some(c => c.command === 'kubectl' && c.args.includes('scale') && c.args.includes('--replicas=2')));
  } finally {
    world2.dispose();
  }
});
