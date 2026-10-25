import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import {
  collectGrantStageReceipts, parseProjectsConfigMigrationArgs, runProjectsConfigMigration,
} from '../lib/projects-config-migration.mjs';
import { DB_MAINTENANCE_TARGET, LOCK_NAME } from '../lib/db-maintenance.mjs';

const publicationSourceSha = 'f21632d48f606da215cf37e70921c85c2d604f1d';
const deploymentSourceSha = 'c'.repeat(40);
const sourceTree = 'b'.repeat(40);
const imageDigest = `sha256:${'1'.repeat(64)}`;
const componentId = 'Agentweaver.Projects.Config';
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
const manifest = {
  stage: 'draft',
  components: [{ id: componentId, kind: 'service', version: '1.0.9' }],
};
const publication = {
  schemaVersion: 1,
  sourceSha: publicationSourceSha,
  status: 'published',
  provenanceSha256: 'd'.repeat(64),
  planned: [{
    id: componentId, kind: 'image', version: '1.0.9', sha256: 'e'.repeat(64),
    destination: 'ghcr.io/sabbour/agentweaver.projects.config:1.0.9',
  }],
  published: [{
    id: componentId, kind: 'image', version: '1.0.9', archiveSha256: 'e'.repeat(64), image,
  }],
};

const planSha256 = '9'.repeat(64);

function grantResult(overrides = {}) {
  return {
    executed: true, mode: 'projects-config-grants', sourceSha: deploymentSourceSha, planSha256,
    receipt: { p1Initializer: { verification: 'projects-config-grants-applied', planSha256 } },
    ...overrides,
  };
}

function createWorld({
  jobOutcome = 'Complete',
  bootstrapLog = 'The first PlatformAdmin was created.',
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
  originV1 = deploymentSourceSha,
  grants = async () => grantResult(),
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
    if (command === 'git') {
      if (['status', 'fetch', 'merge-base'].includes(args[0])) return { status: 0, stdout: '', stderr: '' };
      if (args[0] === 'rev-parse' && args[1] === 'HEAD') {
        return { status: 0, stdout: `${deploymentSourceSha}\n`, stderr: '' };
      }
      if (args[0] === 'rev-parse' && args[1] === 'origin/v1') {
        return { status: 0, stdout: `${originV1}\n`, stderr: '' };
      }
      if (args[0] === 'rev-parse' && args[1] === `${publicationSourceSha}^{tree}`) {
        return { status: 0, stdout: `${sourceTree}\n`, stderr: '' };
      }
      if (args[0] === 'show' && args[1] === `${publicationSourceSha}:releases/foundation.json`) {
        return { status: 0, stdout: JSON.stringify(manifest), stderr: '' };
      }
      throw new Error(`unexpected git ${args.join(' ')}`);
    }
    if (command !== 'kubectl') throw new Error(`unexpected command ${command}`);
    if (args.includes('create') && args.includes('--filename')) {
      const object = JSON.parse(settings.input);
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
      return { status: 0, stdout: JSON.stringify(configMap), stderr: '' };
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
    if (args.includes('logs')) {
      const output = args.some(argument => argument.includes('platform-admin-inspection'))
        ? inspectionLog
        : bootstrapLog;
      return { status: 0, stdout: `${output}\n`, stderr: '' };
    }
    if (args.includes('get') && args.includes('pods')) {
      return { status: 0, stdout: JSON.stringify({ kind: 'List', items: [] }), stderr: '' };
    }
    if (args.includes('delete')) {
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
      '--publication', 'publication.json', '--receipt-dir', path.join(directory, 'receipts'),
    ]),
    overrides: {
      root: path.join(directory, 'checkout'),
      exec,
      readFile: file => file.endsWith('publication.json')
        ? JSON.stringify(publication)
        : readFileSync(file.endsWith('job.yaml')
          ? path.join(process.cwd(), 'deploy', 'k8s', 'migrations', 'projects-config', 'job.yaml')
          : file, 'utf8'),
      verifyImage: async () => ({
        digest: imageDigest, sourceSha: publicationSourceSha, sourceTree, version: '1.0.9',
      }),
      withKubeconfig: async (_target, action, settings) => {
        kubeconfigOptions.push(settings);
        return action(['--kubeconfig', 'offline']);
      },
      // Record the grant step in the same call log so tests can check its order against kubectl calls.
      applyRuntimeGrants: async root => {
        calls.push({ command: 'grants', args: [root], lockHeld: objects.has(`lease/${LOCK_NAME}`) });
        return grants();
      },
      collectGrantStageReceipts: (_root, destination, runId) => {
        calls.push({ command: 'collect-grant-receipts', args: [destination, runId] });
        return [{ path: path.join(destination, `projects-config-grants-${runId}-p1-postgres.jsonl`), sha256: 'a'.repeat(64) }];
      },
      newRunId: () => 'abcdef123456',
      now: () => new Date('2026-10-23T00:00:00.000Z'),
      sleep: async () => {},
      log: () => {},
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

test('migration arguments are dry-run by default and require a publication', () => {
  assert.equal(parseProjectsConfigMigrationArgs(['--help']).help, true);
  assert.throws(() => parseProjectsConfigMigrationArgs([]), /--publication is required/);
  assert.throws(() => parseProjectsConfigMigrationArgs(['--publication', 'x', '--reset']), /unknown argument/);
  assert.equal(parseProjectsConfigMigrationArgs(['--publication', 'receipt.json']).execute, false);
  assert.equal(parseProjectsConfigMigrationArgs([
    '--publication', 'receipt.json', '--bootstrap-first-platform-admin',
  ]).bootstrapFirstPlatformAdmin, true);
  assert.equal(parseProjectsConfigMigrationArgs([
    '--publication', 'receipt.json', '--inspect-first-platform-admin',
  ]).inspectFirstPlatformAdmin, true);
  assert.throws(() => parseProjectsConfigMigrationArgs([
    '--publication', 'receipt.json', '--inspect-first-platform-admin', '--execute',
  ]), /must not be combined with --execute/);
  assert.throws(() => parseProjectsConfigMigrationArgs([
    '--publication', 'receipt.json', '--inspect-first-platform-admin', '--bootstrap-first-platform-admin',
  ]), /mutually exclusive/);
});

test('PlatformAdmin inspection runs read-only without --execute and writes a sanitized receipt', async () => {
  const world = createWorld({ inspectionLog: inspectionOutput });
  try {
    const receipt = await withMigrationCredentials(() => runProjectsConfigMigration({
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
    assert.deepEqual(receipt.cleanup, { job: true, secret: true, lock: true });
    assert.ok(receipt.plannedSteps.includes('create-platform-admin-inspection-job'));
    assert.equal(receipt.plannedSteps.includes('create-migration-job'), false);
    assert.equal(receipt.plannedSteps.includes('apply-runtime-grants'), false);
    assert.equal(receipt.bootstrap, undefined);
    assert.equal(receipt.grants, undefined);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
    assert.ok(world.kubeconfigOptions[0].requiredPermissions.some(permission =>
      JSON.stringify(permission) === JSON.stringify([
        'get', 'configmaps', DB_MAINTENANCE_TARGET.namespace,
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
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, inspectFirstPlatformAdmin: true,
    }, world.overrides)), /extra output/);
    const receipt = JSON.stringify(world.receipts[0].receipt);
    assert.equal(receipt.includes(bootstrapIssuer), false);
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, secret: true, lock: true });
    assert.equal(world.objects.size, 0);
  } finally {
    world.dispose();
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
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, inspectFirstPlatformAdmin: true,
    }, world.overrides)), /inspection command failed; review the sanitized diagnostic/);
    assert.equal(world.receipts[0].receipt.platformAdminInspection.outcome, 'failed');
    assert.equal(world.receipts[0].receipt.platformAdminInspection.diagnostic,
      'ExceptionType=InvalidOperationException; Category=invalid-bootstrap-configuration');
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, secret: true, lock: true });
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
      'publication-image-verified', 'migration-lock-checked', 'bootstrap-identity-config-verified',
    ]);
    assert.ok(receipt.plannedSteps.includes('create-platform-admin-bootstrap-job'));
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete', 'apply'].some(verb => call.args.includes(verb))), false);
    assert.equal(world.calls.some(call => call.command === 'grants'), false);
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
      const receipt = await withMigrationCredentials(() => runProjectsConfigMigration({
        ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
      }, world.overrides));
      assert.equal(receipt.result, 'succeeded');
      assert.equal(receipt.bootstrap.outcome, expectedOutcome);
      assert.equal(receipt.grants, undefined);
      assert.deepEqual(receipt.cleanup, { job: true, secret: true, lock: true });
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
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
    }, world.overrides)), /command failed; review the sanitized diagnostic in the receipt/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.equal(world.receipts[0].receipt.bootstrap.outcome, 'failed');
    assert.equal(
      world.receipts[0].receipt.bootstrap.diagnostic,
      'ExceptionType=DbUpdateException; InnerExceptionType=PostgresException; SqlState=23505; ConstraintName=unknown; TableName=unknown');
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, secret: true, lock: true });
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
      await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration({
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
    await assert.rejects(withMigrationCredentials(() => runProjectsConfigMigration({
      ...world.options, execute: true, bootstrapFirstPlatformAdmin: true,
    }, world.overrides)), /refused because another active PlatformAdmin exists/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.deepEqual(world.receipts[0].receipt.bootstrap, { outcome: 'conflict' });
    assert.deepEqual(world.receipts[0].receipt.cleanup, { job: true, secret: true, lock: true });
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
    await assert.rejects(runProjectsConfigMigration({
      ...world.options, bootstrapFirstPlatformAdmin: true,
    }, world.overrides), /does not match the authentication authority/);
    assert.equal(world.calls.some(call =>
      call.command === 'kubectl' && ['create', 'delete'].some(verb => call.args.includes(verb))), false);
  } finally {
    world.dispose();
  }
});

test('dry run accepts the older publication from a newer v1 tool commit and makes no writes', async () => {
  const world = createWorld();
  try {
    const receipt = await runProjectsConfigMigration(world.options, world.overrides);
    assert.equal(receipt.result, 'dry-run-passed');
    assert.equal(receipt.sourceSha, deploymentSourceSha);
    assert.equal(receipt.publicationSourceSha, publicationSourceSha);
    assert.equal(receipt.imageSourceSha, publicationSourceSha);
    assert.equal(receipt.target.namespace, 'agentweaver');
    assert.deepEqual(receipt.steps.map(step => step.name), ['publication-image-verified', 'migration-lock-checked']);
    assert.ok(receipt.plannedSteps.includes('apply-runtime-grants'));
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
      runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides));
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.schemaVersion, 2);
    assert.deepEqual(receipt.cleanup, {
      job: true, secret: true, lock: true, grantStageReceipts: true, sourceCheckout: true,
    });
    assert.deepEqual(receipt.grants, { status: 'exact', sourceSha: deploymentSourceSha, planSha256 });
    assert.deepEqual(receipt.steps.map(step => step.name).slice(-2), ['migration-job-completed', 'runtime-grants-applied']);
    assert.equal(receipt.grantStageReceipts.length, 1);
    // Grants run after the Job completes, while the lock is held, and before the Job, Secret and lock are removed.
    const grantIndex = world.calls.findIndex(call => call.command === 'grants');
    const jobReadIndex = world.calls.findLastIndex(call =>
      call.command === 'kubectl' && call.args.includes('get') && call.args.includes('job'));
    const firstDeleteIndex = world.calls.findIndex(call => call.command === 'kubectl' && call.args.includes('delete'));
    assert.ok(jobReadIndex < grantIndex && grantIndex < firstDeleteIndex);
    assert.equal(world.calls[grantIndex].lockHeld, true);
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
      runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides)),
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

test('a failed or unproven grant step fails the run, moves its receipt, and still cleans up', async () => {
  for (const grants of [
    async () => { throw new Error('grant readback incomplete'); },
    async () => grantResult({ receipt: { p1Initializer: { verification: 'projects-config-grants-dry-run', planSha256 } } }),
    async () => grantResult({ sourceSha: 'e'.repeat(40) }),
    async () => grantResult({ planSha256: '8'.repeat(64) }),
  ]) {
    const world = createWorld({ grants });
    try {
      await assert.rejects(withMigrationCredentials(() =>
        runProjectsConfigMigration({ ...world.options, execute: true }, world.overrides)),
      /grant readback incomplete|did not prove exact Projects & Config grants/);
      const receipt = world.receipts[0].receipt;
      assert.equal(receipt.result, 'failed');
      assert.equal(receipt.grants, undefined);
      assert.equal(receipt.steps.some(step => step.name === 'runtime-grants-applied'), false);
      assert.deepEqual(receipt.cleanup, {
        job: true, secret: true, lock: true, grantStageReceipts: true, sourceCheckout: true,
      });
      assert.equal(receipt.grantStageReceipts.length, 1);
      assert.equal(world.objects.size, 0);
    } finally {
      world.dispose();
    }
  }
});

test('a checkout left dirty by the grant step fails the run', async () => {
  const world = createWorld();
  try {
    let grantsDone = false;
    const exec = world.overrides.exec;
    const overrides = {
      ...world.overrides,
      applyRuntimeGrants: async root => {
        const result = await world.overrides.applyRuntimeGrants(root);
        grantsDone = true;
        return result;
      },
      exec: (command, args, settings) => command === 'git' && args[0] === 'status' && grantsDone
        ? { status: 0, stdout: '?? Temp/unexpected.txt\n', stderr: '' }
        : exec(command, args, settings),
    };
    await assert.rejects(withMigrationCredentials(() =>
      runProjectsConfigMigration({ ...world.options, execute: true }, overrides)),
    /source checkout left dirty/);
    assert.equal(world.receipts[0].receipt.result, 'failed');
    assert.equal(world.receipts[0].receipt.cleanup.sourceCheckout, false);
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
      assert.equal(world.calls.some(call => call.command === 'kubectl' || call.command === 'grants'), false);
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

test('grant stage receipts move out of Temp and leave unrelated files in place', () => {
  const scratch = path.join(process.cwd(), '.test-scratch', `projects-config-grants-${randomUUID()}`);
  const root = path.join(scratch, 'checkout');
  const destination = path.join(scratch, 'receipts');
  mkdirSync(root, { recursive: true });
  mkdirSync(destination, { recursive: true });
  try {
    assert.deepEqual(collectGrantStageReceipts(root, destination, 'abcdef123456'), []);
    const temp = path.join(root, 'Temp');
    mkdirSync(temp);
    const name = `p1-postgres-${'a'.repeat(32)}.jsonl`;
    writeFileSync(path.join(temp, name), '{"stage":"grants"}\n');
    const moved = collectGrantStageReceipts(root, destination, 'abcdef123456');
    assert.equal(moved.length, 1);
    assert.equal(moved[0].path, path.join(destination, `projects-config-grants-abcdef123456-${name}`));
    assert.match(moved[0].sha256, /^[a-f0-9]{64}$/);
    assert.equal(readFileSync(moved[0].path, 'utf8'), '{"stage":"grants"}\n');
    assert.equal(existsSync(temp), false);

    mkdirSync(temp);
    writeFileSync(path.join(temp, 'unrelated.txt'), 'keep');
    assert.deepEqual(collectGrantStageReceipts(root, destination, 'abcdef123456'), []);
    assert.equal(existsSync(path.join(temp, 'unrelated.txt')), true);
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
});
