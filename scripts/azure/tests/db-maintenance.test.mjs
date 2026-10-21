import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  CONNECTIVITY_MARKER, DB_MAINTENANCE_TARGET, LOCK_NAME, describeEgressDrift, MIGRATION_IDENTITY, REVIEWED_PLAN_SHA256, STATE_NAME,
  connectivityProbeJob, imageReference, parseDbMaintenanceArgs, prepareJob, runDbMaintenance, verifyGrantsMarker,
} from '../lib/db-maintenance.mjs';
import { p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';
import { parseKustomizeResources } from '../../release/kustomize-runtime.mjs';

const digest = character => `sha256:${character.repeat(64)}`;
const ok = (stdout = '') => ({ status: 0, stdout, stderr: '' });
const fail = (stderr = 'failed', status = 1) => ({ status, stdout: '', stderr });
const CLIENT_ID = '12345678-1234-4123-8123-123456789abc';
const PRINCIPAL_ID = '87654321-4321-4321-8321-cba987654321';
const ISSUER = 'https://eastus.oic.prod-aks.azure.com/tenant/cluster';
const plan = JSON.parse(readFileSync('scripts/azure/lib/p1-postgres-plan.json', 'utf8'));
const marker = (service, schema, versions) =>
  `P1_POSTGRES_RUNTIME_GRANTS_VERIFIED service=${service} schema=${schema} role=agentweaver-id-${service} ` +
  `planSha256=${REVIEWED_PLAN_SHA256} versions=${versions.join(',')}`;
const goodLogs = {
  orchestrator: marker('orchestrator', 'orchestrator', plan.coreVersions),
  'events-and-sessions': marker('events-and-sessions', 'events_sessions', plan.eventsVersions),
};

function kustomize(directory) {
  const result = spawnSync('kubectl', ['kustomize', directory], { encoding: 'utf8' });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Builds a fake world for the runner. Rendering uses the real kubectl kustomize; every Azure,
 * git, dotnet, and cluster call is answered from in-memory state, so nothing leaves the machine.
 */
const brokerEgressSpec = parseKustomizeResources(kustomize('deploy/k8s/migrations/identity-broker').stdout).items
  .find(item => item.kind === 'CiliumNetworkPolicy').spec;
const driftedSpec = () => {
  const spec = structuredClone(brokerEgressSpec);
  spec.egress.find(rule => rule.toFQDNs).toFQDNs.push({ matchName: 'extra.example.test' });
  return spec;
};

function world({ lease, drift = false, state, jobOutcome = () => 'Complete', logs = goodLogs, identityExists = true,
  federated = true, accountType = 'user', relayError, appImages = {}, rolloutFails = false, probeLog } = {}) {
  const calls = [];
  const cluster = new Map();
  if (lease) cluster.set(`lease/${LOCK_NAME}`, lease);
  if (state) cluster.set(`configmap/${STATE_NAME}`, state);
  cluster.set('configmap/identity-broker-migration-config', { kind: 'ConfigMap', metadata: { name: 'identity-broker-migration-config' } });
  const receipts = [];
  const relayCalls = [];
  const exec = (command, args, settings = {}) => {
    calls.push({ command, args, settings });
    if (command === 'git') return args[0] === 'rev-parse' ? ok('a'.repeat(40) + '\n') : ok('');
    if (command === 'dotnet' || command === 'kubelogin') return ok();
    if (command === 'az') {
      if (args[0] === 'account') return ok(JSON.stringify({ id: DB_MAINTENANCE_TARGET.subscriptionId,
        tenantId: DB_MAINTENANCE_TARGET.tenantId, user: { name: 'operator@example.test', type: accountType } }));
      if (args[0] === 'aks') return args.includes('--query') ? ok(`${ISSUER}\n`) : ok();
      if (args[0] === 'group') return ok('{"owner":"test"}');
      if (args[0] === 'identity' && args[1] === 'federated-credential')
        return ok(JSON.stringify(federated ? [{ issuer: `${ISSUER}/`, audiences: ['api://AzureADTokenExchange'],
          subject: 'system:serviceaccount:agentweaver:p1-postgres-migration' }] : []));
      if (args[0] === 'identity') {
        if (args.includes('--query')) return ok(`${'d'.repeat(8)}-dddd-4ddd-8ddd-${'d'.repeat(12)}\n`);
        return identityExists ? ok(JSON.stringify({ clientId: CLIENT_ID, principalId: PRINCIPAL_ID }))
          : fail(`ERROR: The Resource '${MIGRATION_IDENTITY}' was not found.`, 3);
      }
      if (args[0] === 'deployment') return ok(JSON.stringify({ name: MIGRATION_IDENTITY, clientId: CLIENT_ID,
        principalObjectId: PRINCIPAL_ID, namespace: 'agentweaver', serviceAccount: 'p1-postgres-migration' }));
      throw new Error(`Unexpected az ${args.join(' ')}`);
    }
    if (command !== 'kubectl') throw new Error(`Unexpected ${command}`);
    if (args[0] === 'kustomize') return kustomize(args[1]);
    const action = args[4];
    const rest = args.slice(5);
    if (action === 'auth') return ok('yes\n');
    if (action === 'get' && rest[0] === 'ciliumnetworkpolicy' && rest[1] === 'identity-broker-migration-egress' &&
        !cluster.has('ciliumnetworkpolicy/identity-broker-migration-egress'))
      return ok(JSON.stringify({ spec: drift ? driftedSpec() : brokerEgressSpec }));
    if (action === 'get' && rest[0] === 'deployment')
      return appImages[rest[1]] ? ok(appImages[rest[1]]) : fail(`NotFound: deployment/${rest[1]}`);
    if (action === 'set') {
      // kubectl set image deployment/<name> <container>=<image>
      appImages[rest[1].slice('deployment/'.length)] = rest[2].slice(rest[2].indexOf('=') + 1);
      return ok();
    }
    if (action === 'rollout') return rolloutFails ? fail('deployment exceeded its progress deadline') : ok();
    if (action === 'create' || action === 'apply') {
      const object = JSON.parse(settings.input);
      const key = `${object.kind.toLowerCase()}/${object.metadata.name}`;
      if (action === 'create' && cluster.has(key)) return fail(`AlreadyExists: ${key}`);
      cluster.set(key, object);
      return ok(`${key} created`);
    }
    if (action === 'get') {
      const [kind, name] = rest;
      if (kind === 'pods') return ok('{"items":[]}');
      const object = cluster.get(`${kind}/${name}`);
      if (!object) return fail(`NotFound: ${kind}/${name}`);
      if (kind === 'job') {
        const type = jobOutcome(name);
        return ok(JSON.stringify({ status: { conditions: [{ type, status: 'True' }] } }));
      }
      const jsonpath = rest.find(arg => arg.startsWith('jsonpath='));
      if (jsonpath) return ok(jsonpath.includes('acquireTime')
        ? `${object.spec.holderIdentity} ${object.spec.acquireTime}` : object.spec.holderIdentity);
      return ok(JSON.stringify(object));
    }
    if (action === 'logs') {
      const name = rest[0].slice('job/'.length);
      if (name.includes('-probe-')) return ok(probeLog ?? `Name: ${DB_MAINTENANCE_TARGET.postgresHost}\n` +
        'agentweaver-pg.postgres.database.azure.com canonical name = agentweaver-pg.privatelink.postgres.database.azure.com\n' +
        `${CONNECTIVITY_MARKER} host=${DB_MAINTENANCE_TARGET.postgresHost} port=5432\n`);
      const key = Object.keys(logs).find(prefix => name.startsWith(`${prefix}-migration-`));
      return ok(key ? logs[key] : 'broker migrated\n');
    }
    if (action === 'delete') {
      cluster.delete(`${rest[0]}/${rest[1]}`);
      return ok();
    }
    throw new Error(`Unexpected kubectl ${args.join(' ')}`);
  };
  const receiptDir = mkdtempSync(join(tmpdir(), 'db-maintenance-test-'));
  return {
    calls, cluster, receipts, relayCalls, receiptDir,
    deps: {
      exec, repoRoot: process.cwd(), sleep: async () => {}, now: () => new Date('2026-10-22T10:00:00.123Z'),
      newRunId: () => 'abcdef123456', log: () => {},
      writeReceipt: (path, receipt) => receipts.push({ path, receipt: structuredClone(receipt) }),
      relay: async options => {
        relayCalls.push(options);
        if (relayError) throw new Error(relayError);
        return { p1OwnershipHandover: { readback: 'P1_POSTGRES_OWNERSHIP_HANDOVER_VERIFIED changed=0' },
          cleanup: { podRemoved: true } };
      },
    },
    done: () => rmSync(receiptDir, { recursive: true, force: true }),
  };
}

const baseArgs = ['--orchestrator-digest', digest('a'), '--events-digest', digest('b')];
const kubectlActions = calls => calls.filter(call => call.command === 'kubectl' && call.args[0] !== 'kustomize')
  .map(call => call.args[4]);

test('arguments require exact digests and reject unknown or malformed flags', () => {
  const parsed = parseDbMaintenanceArgs(baseArgs);
  assert.equal(parsed.execute, false);
  assert.equal(parsed.handover, 'auto');
  assert.equal(parsed.jobTimeoutSeconds, 900);
  assert.ok(!parsed.receiptDir.startsWith(process.cwd()));
  assert.throws(() => parseDbMaintenanceArgs([]), /--orchestrator-digest must be an exact sha256/);
  assert.throws(() => parseDbMaintenanceArgs(['--orchestrator-digest', 'latest', '--events-digest', digest('b')]), /exact sha256/);
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--broker-digest', 'sha256:abc']), /--broker-digest/);
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--handover', 'force']), /auto, always, or skip/);
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--reset']), /Unknown argument/);
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--handover']), /requires a value/);
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--job-timeout-seconds', '5']), /60 to 3600/);
  assert.equal(parseDbMaintenanceArgs(['--release-lock=abcdef123456']).releaseLock, 'abcdef123456');
  assert.throws(() => parseDbMaintenanceArgs(['--release-lock', 'someone']), /12-character run ID/);
});

test('image references pin the published component repository by digest', () => {
  assert.equal(imageReference('Agentweaver.Orchestrator', digest('a')),
    `ghcr.io/sabbour/agentweaver.orchestrator@${digest('a')}`);
});

test('grant markers must match the reviewed plan exactly', () => {
  const expected = { service: 'orchestrator', schema: 'orchestrator', versions: plan.coreVersions };
  assert.match(verifyGrantsMarker(`noise\n${goodLogs.orchestrator}\n`, expected), /versions=1,2/);
  assert.throws(() => verifyGrantsMarker('migrated\n', expected), /0 grant markers/);
  assert.throws(() => verifyGrantsMarker(`${goodLogs.orchestrator}\n${goodLogs.orchestrator}`, expected), /2 grant markers/);
  assert.throws(() => verifyGrantsMarker(marker('orchestrator', 'orchestrator', [...plan.coreVersions, 32]), expected),
    /differs from the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(marker('orchestrator', 'orchestrator', plan.coreVersions.slice(0, -1)), expected),
    /differs from the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(goodLogs.orchestrator.replace(REVIEWED_PLAN_SHA256, 'f'.repeat(64)), expected),
    /does not match the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(goodLogs['events-and-sessions'], expected), /does not match/);
});

test('the reviewed plan excludes 032 and matches the pinned hash', () => {
  assert.equal(p1PostgresPlanHash(), REVIEWED_PLAN_SHA256);
  assert.ok(!plan.coreVersions.includes(17));
  assert.ok(!plan.coreVersions.includes(32));
  assert.equal(Math.max(...plan.coreVersions), 31);
  assert.deepEqual(plan.eventsVersions, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
});

test('P1 migration manifests render one identity, one egress, and two fail-closed Jobs', () => {
  const rendered = kustomize('deploy/k8s/migrations/p1-postgres');
  assert.equal(rendered.status, 0, rendered.stderr);
  const items = parseKustomizeResources(rendered.stdout).items;
  assert.deepEqual(items.map(item => `${item.kind}/${item.metadata.name}`).sort(), [
    'CiliumNetworkPolicy/p1-postgres-migration-egress', 'Job/events-and-sessions-migration',
    'Job/orchestrator-migration', 'ServiceAccount/p1-postgres-migration',
  ]);
  const account = items.find(item => item.kind === 'ServiceAccount');
  assert.equal(account.metadata.annotations['azure.workload.identity/client-id'], '00000000-0000-0000-0000-000000000000');
  const egress = items.find(item => item.kind === 'CiliumNetworkPolicy');
  assert.equal(egress.spec.endpointSelector.matchLabels['agentweaver.io/workload'], 'p1-postgres-migration');
  assert.ok(JSON.stringify(egress).includes(DB_MAINTENANCE_TARGET.postgresHost));
  for (const [name, role, prefix] of [
    ['orchestrator-migration', 'agentweaver-id-orchestrator', 'Orchestrator'],
    ['events-and-sessions-migration', 'agentweaver-id-events-and-sessions', 'EventsAndSessions'],
  ]) {
    const job = items.find(item => item.kind === 'Job' && item.metadata.name === name);
    assert.equal(job.spec.backoffLimit, 0);
    assert.equal(job.spec.template.spec.serviceAccountName, 'p1-postgres-migration');
    assert.equal(job.spec.template.spec.restartPolicy, 'Never');
    const container = job.spec.template.spec.containers[0];
    assert.match(container.image, /^registry\.invalid\//);
    assert.ok(container.command.includes('--migrate'));
    const env = Object.fromEntries(container.env.map(entry => [entry.name, entry.value]));
    assert.match(container.env[0].value, new RegExp(`Host=${DB_MAINTENANCE_TARGET.postgresHost.replaceAll('.', '\\.')}`));
    assert.equal(env[`${prefix}__Migration__RuntimeRole`], role);
    assert.equal(env[`${prefix}__Migration__PlanSha256`], REVIEWED_PLAN_SHA256);
    assert.ok(!JSON.stringify(container.env).match(/Password=/i));
  }
});

test('the migration identity template has a federated credential and no Azure role assignment', () => {
  const result = spawnSync('az', ['bicep', 'build', '--file', 'infra/bicep/p1-postgres-migration-identity.bicep', '--stdout'],
    { encoding: 'utf8', shell: process.platform === 'win32' });
  if (result.error || result.status !== 0) {
    // The Bicep CLI is optional for offline runs; the source check below still applies.
    const source = readFileSync('infra/bicep/p1-postgres-migration-identity.bicep', 'utf8');
    assert.match(source, /agentweaver-id-p1-postgres-migration|'\$\{namePrefix\}-id-p1-postgres-migration'/);
    assert.doesNotMatch(source, /roleAssignments/);
    return;
  }
  const template = JSON.parse(result.stdout);
  const flat = JSON.stringify(template);
  assert.doesNotMatch(flat, /Microsoft\.Authorization\/roleAssignments/);
  assert.match(flat, /Microsoft\.ManagedIdentity\/userAssignedIdentities/);
  assert.match(flat, /system:serviceaccount:agentweaver:p1-postgres-migration|p1-postgres-migration/);
  assert.match(flat, /api:\/\/AzureADTokenExchange/);
});

test('prepareJob pins the image, names the Job per run, and fills the migration client ID', () => {
  const items = parseKustomizeResources(kustomize('deploy/k8s/migrations/p1-postgres').stdout).items;
  const template = items.find(item => item.kind === 'Job' && item.metadata.name === 'orchestrator-migration');
  const job = prepareJob(template, { name: 'orchestrator-migration-abcdef123456',
    image: imageReference('Agentweaver.Orchestrator', digest('a')), runId: 'abcdef123456', clientId: CLIENT_ID });
  assert.equal(job.metadata.name, 'orchestrator-migration-abcdef123456');
  assert.equal(job.spec.template.spec.containers[0].image, `ghcr.io/sabbour/agentweaver.orchestrator@${digest('a')}`);
  assert.equal(job.spec.template.metadata.labels['agentweaver.io/db-maintenance-run'], 'abcdef123456');
  const env = job.spec.template.spec.containers[0].env;
  assert.equal(env.find(entry => entry.name.endsWith('__WorkloadIdentity__ClientId')).value, CLIENT_ID);
  assert.match(template.spec.template.spec.containers[0].image, /registry\.invalid/);
});

test('dry run checks every precondition, takes no lock, and changes nothing', async () => {
  const fake = world({ drift: true });
  try {
    const receipt = await runDbMaintenance(
      { ...parseDbMaintenanceArgs(baseArgs), receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'dry-run-passed');
    assert.equal(receipt.brokerEgressDrift, true);
    assert.deepEqual(receipt.brokerEgressDriftDetail, { extraDnsNames: ['extra.example.test'], missingDnsNames: [] });
    assert.ok(receipt.plannedSteps.includes('orchestrator-job'));
    assert.ok(!receipt.plannedSteps.includes('broker-egress-reconcile'));
    assert.deepEqual([...fake.cluster.keys()], ['configmap/identity-broker-migration-config']);
    assert.ok(!kubectlActions(fake.calls).some(action => ['create', 'apply', 'delete'].includes(action)));
    assert.ok(fake.calls.some(call => call.command === 'dotnet' && call.args.includes('--locked-mode')));
    assert.equal(fake.receipts.length, 1);
    assert.equal(fake.receipts[0].receipt.result, 'dry-run-passed');
  } finally { fake.done(); }
});

test('a failed locked restore stops the run before any Azure or cluster call', async () => {
  const fake = world();
  const exec = fake.deps.exec;
  fake.deps.exec = (command, args, settings) => command === 'dotnet' ? fail('NU1004 lock file out of date') : exec(command, args, settings);
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']), receiptDir: fake.receiptDir },
      fake.deps), /Locked restore failed/);
    assert.ok(!fake.calls.some(call => call.command === 'az' || call.command === 'kubectl'));
    assert.equal(fake.receipts[0].receipt.result, 'failed');
  } finally { fake.done(); }
});

test('execute applies the identity, egress, handover, and Jobs in order, then cleans up', async () => {
  const fake = world({ identityExists: false });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute',
      '--broker-digest', digest('c')]), receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.deepEqual(receipt.steps.map(step => step.name), [
      'source-verified', 'locked-restore-verified', 'plan-verified', 'cluster-access-verified', 'broker-egress-checked',
      'migration-identity-checked', 'app-images-checked', 'lock-acquired', 'migration-identity-ready', 'serviceaccount-and-egress-applied',
      'ownership-handover-verified', 'connectivity-verified', 'connectivity-verified',
      'identity-broker-migrated', 'orchestrator-migrated', 'events-and-sessions-migrated',
    ]);
    const probes = receipt.steps.filter(step => step.name === 'connectivity-verified');
    assert.deepEqual(probes.map(step => step.workload), ['identity-broker-migration', 'p1-postgres-migration']);
    assert.deepEqual(probes[0].canonicalNames, ['agentweaver-pg.privatelink.postgres.database.azure.com']);
    assert.equal(fake.relayCalls.length, 1);
    assert.equal(fake.relayCalls[0].verifyOnly, true);
    assert.equal(fake.relayCalls[0].p1OwnershipHandover.migrationPrincipalObjectId, PRINCIPAL_ID);
    assert.equal(fake.cluster.get(`serviceaccount/p1-postgres-migration`).metadata.annotations['azure.workload.identity/client-id'], CLIENT_ID);
    assert.equal(fake.cluster.get(`configmap/${STATE_NAME}`).data.handoverPrincipalObjectId, PRINCIPAL_ID);
    // Only the durable resources remain: no Jobs and no lock.
    assert.deepEqual([...fake.cluster.keys()].sort(), [
      'ciliumnetworkpolicy/p1-postgres-migration-egress', 'configmap/identity-broker-migration-config',
      `configmap/${STATE_NAME}`, 'serviceaccount/p1-postgres-migration',
    ]);
    assert.equal(receipt.cleanup.lock, true);
    assert.equal(receipt.cleanup['job/orchestrator-migration-abcdef123456'], true);
    const deploy = fake.calls.find(call => call.command === 'az' && call.args[0] === 'deployment');
    assert.ok(deploy.args.includes(`sourceSha=${'a'.repeat(40)}`));
    assert.equal(fake.receipts[0].receipt.result, 'succeeded');
  } finally { fake.done(); }
});

test('a rerun skips the recorded handover and still verifies every Job', async () => {
  const fake = world({ state: { kind: 'ConfigMap', metadata: { name: STATE_NAME },
    data: { handoverPrincipalObjectId: PRINCIPAL_ID } } });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(fake.relayCalls.length, 0);
    assert.ok(receipt.steps.some(step => step.name === 'ownership-handover-skipped'));
    assert.ok(receipt.steps.some(step => step.name === 'events-and-sessions-migrated'));
  } finally { fake.done(); }
});

test('a held lock fails closed before any change and leaves the other run alone', async () => {
  const fake = world({ lease: { kind: 'Lease', metadata: { name: LOCK_NAME },
    spec: { holderIdentity: 'fedcba654321', acquireTime: '2026-10-22T09:00:00.000000Z' } } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps), /holds p1-db-maintenance-lock \(fedcba654321/);
    assert.equal(fake.cluster.get(`lease/${LOCK_NAME}`).spec.holderIdentity, 'fedcba654321');
    assert.ok(!fake.calls.some(call => call.command === 'az' && call.args[0] === 'deployment'));
    assert.equal(fake.receipts[0].receipt.result, 'failed');
  } finally { fake.done(); }
});

test('a failed Job still removes Jobs, releases the lock, and writes a failed receipt', async () => {
  const fake = world({ jobOutcome: name => name.startsWith('orchestrator') ? 'Failed' : 'Complete',
    state: { kind: 'ConfigMap', metadata: { name: STATE_NAME }, data: { handoverPrincipalObjectId: PRINCIPAL_ID } } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps), /Job orchestrator-migration-abcdef123456 failed/);
    assert.ok(![...fake.cluster.keys()].some(key => key.startsWith('job/') || key.startsWith('lease/')));
    const { receipt } = fake.receipts[0];
    assert.equal(receipt.result, 'failed');
    assert.equal(receipt.cleanup.lock, true);
    assert.ok(!receipt.steps.some(step => step.name === 'events-and-sessions-migrated'));
  } finally { fake.done(); }
});

test('a ledger past the reviewed plan fails the run', async () => {
  const fake = world({ logs: { ...goodLogs, orchestrator: marker('orchestrator', 'orchestrator', [...plan.coreVersions, 32]) },
    state: { kind: 'ConfigMap', metadata: { name: STATE_NAME }, data: { handoverPrincipalObjectId: PRINCIPAL_ID } } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps), /differs from the reviewed plan/);
    assert.equal(fake.receipts[0].receipt.result, 'failed');
  } finally { fake.done(); }
});

test('a handover failure stops before any migration Job and releases the lock', async () => {
  const fake = world({ relayError: 'P1 ownership handover failed: permission denied' });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps), /permission denied/);
    assert.ok(!kubectlActions(fake.calls).includes('logs'));
    assert.ok(!fake.cluster.has(`lease/${LOCK_NAME}`));
  } finally { fake.done(); }
});

test('Broker egress drift is reconciled only when asked', async () => {
  const state = { kind: 'ConfigMap', metadata: { name: STATE_NAME }, data: { handoverPrincipalObjectId: PRINCIPAL_ID } };
  for (const reconcile of [false, true]) {
    const fake = world({ drift: true, state });
    try {
      const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute',
        ...(reconcile ? ['--reconcile-broker-egress'] : [])]), receiptDir: fake.receiptDir }, fake.deps);
      assert.equal(receipt.brokerEgressDrift, true);
      assert.equal(fake.cluster.has('ciliumnetworkpolicy/identity-broker-migration-egress'), reconcile);
      assert.equal(receipt.steps.some(step => step.name === 'broker-egress-reconciled'), reconcile);
    } finally { fake.done(); }
  }
});

test('lock release removes only the named run lock', async () => {
  const lease = { kind: 'Lease', metadata: { name: LOCK_NAME }, spec: { holderIdentity: 'fedcba654321' } };
  const wrong = world({ lease });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs(['--release-lock', '000000000000']),
      receiptDir: wrong.receiptDir }, wrong.deps), /held by fedcba654321/);
    assert.ok(wrong.cluster.has(`lease/${LOCK_NAME}`));
  } finally { wrong.done(); }
  const right = world({ lease });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs(['--release-lock', 'fedcba654321']),
      receiptDir: right.receiptDir }, right.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.ok(!right.cluster.has(`lease/${LOCK_NAME}`));
  } finally { right.done(); }
});

test('the workflow runs only by dispatch from v1 and always uploads the receipt', () => {
  const workflow = readFileSync('.github/workflows/v1-db-maintenance.yml', 'utf8');
  assert.match(workflow, /^on:\n  workflow_dispatch:/m);
  assert.doesNotMatch(workflow, /^\s+(push|pull_request|schedule):/m);
  assert.match(workflow, /refs\/heads\/v1/);
  assert.match(workflow, /npm run db:maintenance -- "\$\{args\[@\]\}"/);
  assert.match(workflow, /if: always\(\)[\s\S]*upload-artifact/);
  assert.match(workflow, /cancel-in-progress: false/);
});

test('egress drift ignores key order and reports missing policies and DNS differences', () => {
  // Reverse key order at every level, as the API server may return keys in another order.
  const reverseKeys = value => Array.isArray(value) ? value.map(reverseKeys)
    : value && typeof value === 'object'
      ? Object.fromEntries(Object.entries(value).reverse().map(([key, item]) => [key, reverseKeys(item)])) : value;
  const reordered = reverseKeys(brokerEgressSpec);
  assert.equal(describeEgressDrift(reordered, brokerEgressSpec), undefined);
  assert.deepEqual(describeEgressDrift(undefined, brokerEgressSpec), { missing: true });
  assert.deepEqual(describeEgressDrift(brokerEgressSpec, driftedSpec()),
    { extraDnsNames: [], missingDnsNames: ['extra.example.test'] });
});

const appWorld = (extra = {}) => world({ state: { kind: 'ConfigMap', metadata: { name: STATE_NAME },
  data: { handoverPrincipalObjectId: PRINCIPAL_ID } }, appImages: {
  orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')),
  'events-and-sessions': imageReference('Agentweaver.EventsAndSessions', digest('e')) }, ...extra });

test('services on older images block the run unless the runner deploys the matching digests', async () => {
  const fake = appWorld();
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs(baseArgs), receiptDir: fake.receiptDir }, fake.deps),
      /Running services use other images \(events-and-sessions\)[\s\S]*--deploy-apps/);
    assert.equal(fake.receipts[0].receipt.result, 'failed');
    assert.deepEqual(fake.receipts[0].receipt.appImages.map(app => [app.deployment, app.status]),
      [['orchestrator', 'match'], ['events-and-sessions', 'mismatch']]);
  } finally { fake.done(); }
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--deploy-apps', '--allow-app-image-mismatch']),
    /cannot be used together/);
});

test('--deploy-apps migrates first, then rolls only mismatched services and waits for readiness', async () => {
  const fake = appWorld();
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    const names = receipt.steps.map(step => step.name);
    assert.ok(names.indexOf('events-and-sessions-migrated') < names.indexOf('app-image-set'));
    assert.deepEqual(receipt.steps.filter(step => step.name === 'app-image-set').map(step => step.deployment),
      ['events-and-sessions']);
    assert.deepEqual(receipt.steps.filter(step => step.name === 'app-ready').map(step => step.deployment),
      ['orchestrator', 'events-and-sessions']);
    const set = fake.calls.find(call => call.args[4] === 'set');
    assert.deepEqual(set.args.slice(5), ['image', 'deployment/events-and-sessions',
      `events-and-sessions=${imageReference('Agentweaver.EventsAndSessions', digest('b'))}`]);
    assert.ok(fake.calls.some(call => call.args[4] === 'auth' && call.args.includes('deployments.apps') && call.args.includes('patch')));
  } finally { fake.done(); }
});

test('a service that does not become ready fails the run after cleanup', async () => {
  const fake = appWorld({ rolloutFails: true });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps), /orchestrator did not become ready/);
    assert.equal(fake.receipts[0].receipt.cleanup.lock, true);
  } finally { fake.done(); }
});

test('a failed connectivity probe stops the run before any migration Job', async () => {
  const fake = world({ probeLog: 'PROBE_TCP_FAILED host=agentweaver-pg.postgres.database.azure.com port=5432\n',
    state: { kind: 'ConfigMap', metadata: { name: STATE_NAME }, data: { handoverPrincipalObjectId: PRINCIPAL_ID } } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--broker-digest', digest('c')]),
      receiptDir: fake.receiptDir }, fake.deps), /Connectivity probe under identity-broker-migration egress failed/);
    assert.ok(!fake.calls.some(call => call.args[4] === 'create' && call.settings.input?.includes('"kind":"Job"') &&
      call.settings.input.includes('-migration-abcdef123456')));
    assert.ok(![...fake.cluster.keys()].some(key => key.startsWith('job/') || key.startsWith('lease/')));
  } finally { fake.done(); }
});

test('--apps-only rolls services to the pinned digests under the lock and runs no migration step', async () => {
  const fake = appWorld();
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--apps-only']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.scope, 'apps-only');
    assert.deepEqual(receipt.steps.map(step => step.name).slice(-4),
      ['lock-acquired', 'app-image-set', 'app-ready', 'app-ready']);
    // No identity, handover, egress, probe, or migration Job is touched.
    assert.ok(!fake.calls.some(call => call.command === 'az' && ['deployment', 'identity'].includes(call.args[0])));
    assert.equal(fake.relayCalls.length, 0);
    assert.ok(!fake.calls.some(call => call.args[4] === 'create' && call.settings.input?.includes('"kind":"Job"')));
    assert.ok(!fake.calls.some(call => call.args[4] === 'get' && call.args[5] === 'ciliumnetworkpolicy'));
    assert.equal(fake.receipts[0].receipt.cleanup.lock, true);
    assert.ok(![...fake.cluster.keys()].some(key => key.startsWith('lease/')));
  } finally { fake.done(); }
});

test('--apps-only is safe to rerun and rejects flags that reach the database', async () => {
  const fake = appWorld({ appImages: {
    orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')),
    'events-and-sessions': imageReference('Agentweaver.EventsAndSessions', digest('b')) } });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--apps-only']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.ok(!receipt.steps.some(step => step.name === 'app-image-set'));
    assert.equal(receipt.steps.filter(step => step.name === 'app-ready').length, 2);
  } finally { fake.done(); }
  for (const flag of [['--broker-digest', digest('c')], ['--reconcile-broker-egress'], ['--deploy-apps'],
    ['--allow-app-image-mismatch'], ['--handover', 'skip'], ['--postgres-admin', 'a@example.test']])
    assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--apps-only', ...flag]), /--apps-only cannot be combined/);
});

test('--apps-only fails closed when a service Deployment does not exist', async () => {
  const fake = world({ appImages: { orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')) } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--apps-only']),
      receiptDir: fake.receiptDir }, fake.deps), /events-and-sessions does not exist/);
    assert.ok(!fake.calls.some(call => call.args[4] === 'set' || call.args[4] === 'create'));
  } finally { fake.done(); }
});

test('the connectivity probe runs under the migration egress label with no token and no privileges', () => {
  const job = connectivityProbeJob({ name: 'p', workload: 'identity-broker-migration', runId: 'abcdef123456',
    host: DB_MAINTENANCE_TARGET.postgresHost, namespace: 'agentweaver' });
  const pod = job.spec.template.spec;
  assert.equal(job.spec.template.metadata.labels['agentweaver.io/workload'], 'identity-broker-migration');
  assert.equal(pod.automountServiceAccountToken, false);
  assert.equal(pod.containers[0].securityContext.allowPrivilegeEscalation, false);
  assert.match(pod.containers[0].image, /@sha256:[0-9a-f]{64}$/);
  assert.match(pod.containers[0].command[2], new RegExp(CONNECTIVITY_MARKER));
});
const recordedState = { kind: 'ConfigMap', metadata: { name: STATE_NAME }, data: { handoverPrincipalObjectId: PRINCIPAL_ID } };

test('a workflow identity runs without Azure deployment rights once setup is done', async () => {
  const fake = world({ accountType: 'servicePrincipal', state: recordedState });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.ok(!fake.calls.some(call => call.command === 'az' && ['deployment', 'group'].includes(call.args[0])));
    assert.equal(fake.relayCalls.length, 0);
    const ready = receipt.steps.find(step => step.name === 'migration-identity-ready');
    assert.equal(ready.created, false);
    assert.equal(ready.deployed, false);
    assert.equal(fake.cluster.get('serviceaccount/p1-postgres-migration').metadata.annotations['azure.workload.identity/client-id'], CLIENT_ID);
  } finally { fake.done(); }
});

test('a workflow identity fails closed before the lock when setup is missing', async () => {
  for (const [settings, expected] of [
    [{}, /needs the ownership handover, which needs the PostgreSQL Entra administrator sign-in/],
    [{ federated: false, state: recordedState }, /needs the migration identity, which needs the PostgreSQL Entra administrator sign-in/],
    [{ identityExists: false }, /needs the migration identity and the ownership handover/],
  ]) {
    const fake = world({ accountType: 'servicePrincipal', ...settings });
    try {
      await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
        receiptDir: fake.receiptDir }, fake.deps), expected);
      assert.match(fake.receipts[0].receipt.error, /npm run db:maintenance:setup -- --execute/);
      assert.ok(!kubectlActions(fake.calls).some(action => ['create', 'apply', 'delete'].includes(action)));
      assert.ok(!fake.calls.some(call => call.command === 'az' && call.args[0] === 'deployment'));
    } finally { fake.done(); }
  }
});

test('--prepare-only sets up identity, egress, and handover, then stops before any Job', async () => {
  const fake = world({ identityExists: false });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs(['--prepare-only', '--execute']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.scope, 'prepare');
    assert.deepEqual(receipt.steps.map(step => step.name).slice(-4),
      ['lock-acquired', 'migration-identity-ready', 'serviceaccount-and-egress-applied', 'ownership-handover-verified']);
    assert.equal(fake.relayCalls.length, 1);
    assert.ok(!fake.calls.some(call => call.args[4] === 'create' && call.settings.input?.includes('"kind":"Job"')));
    assert.ok(!fake.calls.some(call => call.args[4] === 'get' && call.args[5] === 'deployment'));
    assert.ok(![...fake.cluster.keys()].some(key => key.startsWith('lease/')));
  } finally { fake.done(); }
  assert.throws(() => parseDbMaintenanceArgs(['--prepare-only', ...baseArgs]), /--prepare-only cannot be combined with --orchestrator-digest/);
  assert.throws(() => parseDbMaintenanceArgs(['--prepare-only', '--deploy-apps']), /--deploy-apps/);
});
