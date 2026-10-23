import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  CONNECTIVITY_MARKER, DB_MAINTENANCE_TARGET, LOCK_NAME, describeEgressDrift, MIGRATION_IDENTITY, REVIEWED_PLAN_SHA256, STATE_NAME,
  connectivityProbeJob, imageReference, parseDbMaintenanceArgs, planServiceOrder, prepareJob, readImageSchemaRange, runDbMaintenance,
  schemaRangeFromSource, verifyGrantsMarker,
} from '../lib/db-maintenance.mjs';
import { p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';
import { redact } from '../lib/exec.mjs';
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
  environment: marker('environment', 'environment', plan.environmentVersions),
  orchestrator: marker('orchestrator', 'orchestrator', plan.coreVersions),
  'events-and-sessions': marker('events-and-sessions', 'events_sessions', plan.eventsVersions),
};
const defaultSchemaRanges = {
  [digest('a')]: { minSchema: 31, maxSchema: 33 },
  [digest('b')]: { minSchema: 9, maxSchema: 9 },
  [digest('d')]: { minSchema: 11, maxSchema: 11 },
  [digest('f')]: { minSchema: 10, maxSchema: 10 },
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
  federated = true, accountType = 'user', relayError, appImages = {}, rolloutFails = false, probeLog,
  schemaRanges = {} } = {}) {
  const calls = [];
  const imageRanges = { ...defaultSchemaRanges, ...schemaRanges };
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
    if (action === 'rollout') {
      const name = rest[1].slice('deployment/'.length);
      return (typeof rolloutFails === 'function' ? rolloutFails(name) : rolloutFails)
        ? fail('deployment exceeded its progress deadline') : ok();
    }
    if (action === 'patch') {
      // kubectl patch configmap <name> --type merge -p {"data":{...}}; null removes a key.
      const key = `${rest[0]}/${rest[1]}`;
      const object = cluster.get(key);
      if (!object) return fail(`NotFound: ${key}`);
      const data = { ...(object.data ?? {}) };
      for (const [name, value] of Object.entries(JSON.parse(rest[rest.indexOf('-p') + 1]).data)) {
        if (value === null) delete data[name];
        else data[name] = value;
      }
      cluster.set(key, { ...object, data });
      return ok(`${key} patched`);
    }
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
      // Image schema ranges come from this table instead of the registry, so tests stay offline.
      imageSchemaRange: async image => imageRanges[image?.slice(image.indexOf('@') + 1)] ?? { unknown: 'not in the test registry' },
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

const baseArgs = ['--environment-digest', digest('d'), '--orchestrator-digest', digest('a'), '--events-digest', digest('b')];
const kubectlActions = calls => calls.filter(call => call.command === 'kubectl' && call.args[0] !== 'kustomize')
  .map(call => call.args[4]);

test('arguments require exact digests and reject unknown or malformed flags', () => {
  const parsed = parseDbMaintenanceArgs(baseArgs);
  assert.equal(parsed.execute, false);
  assert.equal(parsed.handover, 'auto');
  assert.equal(parsed.jobTimeoutSeconds, 900);
  assert.ok(!parsed.receiptDir.startsWith(process.cwd()));
  assert.throws(() => parseDbMaintenanceArgs([]), /--environment-digest must be an exact sha256/);
  assert.throws(() => parseDbMaintenanceArgs(['--environment-digest', 'latest', ...baseArgs.slice(2)]), /exact sha256/);
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
  assert.match(verifyGrantsMarker(goodLogs.environment, {
    service: 'environment', schema: 'environment', versions: plan.environmentVersions,
  }), /versions=1,2/);
  assert.throws(() => verifyGrantsMarker('migrated\n', expected), /0 grant markers/);
  assert.throws(() => verifyGrantsMarker(`${goodLogs.orchestrator}\n${goodLogs.orchestrator}`, expected), /2 grant markers/);
  assert.throws(() => verifyGrantsMarker(marker('orchestrator', 'orchestrator', [...plan.coreVersions, 34]), expected),
    /differs from the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(marker('orchestrator', 'orchestrator', plan.coreVersions.slice(0, -1)), expected),
    /differs from the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(goodLogs.orchestrator.replace(REVIEWED_PLAN_SHA256, 'f'.repeat(64)), expected),
    /does not match the reviewed plan/);
  assert.throws(() => verifyGrantsMarker(goodLogs['events-and-sessions'], expected), /does not match/);
});

test('the reviewed plan includes 032 and matches the pinned hash', () => {
  assert.equal(p1PostgresPlanHash(), REVIEWED_PLAN_SHA256);
  assert.ok(!plan.coreVersions.includes(17));
  assert.ok(plan.coreVersions.includes(32));
  assert.equal(Math.max(...plan.coreVersions), 33);
  assert.deepEqual(plan.eventsVersions, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
  assert.deepEqual(plan.environmentVersions, Array.from({ length: 11 }, (_, index) => index + 1));
});

test('P1 migration manifests render one identity, one egress, and three fail-closed Jobs', () => {
  const rendered = kustomize('deploy/k8s/migrations/p1-postgres');
  assert.equal(rendered.status, 0, rendered.stderr);
  const items = parseKustomizeResources(rendered.stdout).items;
  assert.deepEqual(items.map(item => `${item.kind}/${item.metadata.name}`).sort(), [
    'CiliumNetworkPolicy/p1-postgres-migration-egress', 'Job/environment-migration', 'Job/events-and-sessions-migration',
    'Job/orchestrator-migration', 'ServiceAccount/p1-postgres-migration',
  ]);
  const account = items.find(item => item.kind === 'ServiceAccount');
  assert.equal(account.metadata.annotations['azure.workload.identity/client-id'], '00000000-0000-0000-0000-000000000000');
  const egress = items.find(item => item.kind === 'CiliumNetworkPolicy');
  assert.equal(egress.spec.endpointSelector.matchLabels['agentweaver.io/workload'], 'p1-postgres-migration');
  assert.ok(JSON.stringify(egress).includes(DB_MAINTENANCE_TARGET.postgresHost));
  for (const [name, role, prefix] of [
    ['environment-migration', 'agentweaver-id-environment', 'Environment'],
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
    assert.ok(receipt.plannedSteps.includes('environment-job'));
    assert.ok(receipt.plannedSteps.includes('orchestrator-job'));
    assert.ok(!receipt.plannedSteps.includes('broker-egress-reconcile'));
    assert.deepEqual([...fake.cluster.keys()], ['configmap/identity-broker-migration-config']);
    assert.ok(!kubectlActions(fake.calls).some(action => ['create', 'apply', 'delete'].includes(action)));
    assert.ok(fake.calls.some(call => call.command === 'dotnet' && call.args.includes('--locked-mode')));
    const authChecks = fake.calls.filter(call => call.command === 'kubectl' && call.args[4] === 'auth')
      .map(call => call.args.slice(6));
    assert.ok(authChecks.some(([verb, resource]) => verb === 'list' && resource === 'jobs'));
    assert.ok(authChecks.some(([verb, resource]) => verb === 'watch' && resource === 'jobs'));
    assert.deepEqual(new Set(authChecks.filter(([, resource]) => resource.startsWith('deployments.apps/'))
      .map(([verb, resource]) => `${verb}:${resource.slice('deployments.apps/'.length)}`)), new Set([
      'get:environment', 'list:environment', 'watch:environment',
      'get:orchestrator', 'list:orchestrator', 'watch:orchestrator',
      'get:events-and-sessions', 'list:events-and-sessions', 'watch:events-and-sessions',
    ]));
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
      'identity-broker-migrated', 'environment-migrated', 'orchestrator-migrated', 'events-and-sessions-migrated',
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
  const fake = world({ logs: { ...goodLogs, orchestrator: marker('orchestrator', 'orchestrator', [...plan.coreVersions, 34]) },
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

const appWorld = (extra = {}) => {
  const { appImages = {}, ...overrides } = extra;
  return world({ state: { kind: 'ConfigMap', metadata: { name: STATE_NAME },
    data: { handoverPrincipalObjectId: PRINCIPAL_ID } }, ...overrides, appImages: {
    environment: imageReference('Agentweaver.Environment', digest('d')),
    orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')),
    'events-and-sessions': imageReference('Agentweaver.EventsAndSessions', digest('e')),
    ...appImages,
  } });
};

test('services on older images block the run unless the runner deploys the matching digests', async () => {
  const fake = appWorld();
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs(baseArgs), receiptDir: fake.receiptDir }, fake.deps),
      /Running services use other images \(events-and-sessions\)[\s\S]*--deploy-apps/);
    assert.equal(fake.receipts[0].receipt.result, 'failed');
    assert.deepEqual(fake.receipts[0].receipt.appImages.map(app => [app.deployment, app.status]),
      [['environment', 'match'], ['orchestrator', 'match'], ['events-and-sessions', 'mismatch']]);
  } finally { fake.done(); }
  // The planned-gap refusal happens in preflight, before the lock or any Job.
  assert.ok(!fake.calls.some(call => call.args[4] === 'create'));
  assert.equal(fake.receipts[0].receipt.serviceOrder.order, 'planned-readiness-gap');
  // There is no switch to migrate and leave services on images that reject the schema.
  assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--allow-app-image-mismatch']), /Unknown argument/);
});

test('a planned readiness gap migrates, deploys the matching digests in the same run, and records the gap', async () => {
  const fake = appWorld();
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.serviceOrder.order, 'planned-readiness-gap');
    assert.deepEqual(receipt.serviceOrder.services.map(service => [service.deployment, service.action]),
      [['environment', 'none'], ['orchestrator', 'none'], ['events-and-sessions', 'gap']]);
    const names = receipt.steps.map(step => step.name);
    assert.ok(names.indexOf('events-and-sessions-migrated') < names.indexOf('app-image-set'));
    assert.ok(names.indexOf('app-ready') < names.indexOf('roll-forward-cleared'));
    assert.deepEqual(receipt.readinessGap, { planned: true, deployments: ['events-and-sessions'],
      startedAt: '2026-10-22T10:00:00.123Z', endedAt: '2026-10-22T10:00:00.123Z', durationSeconds: 0 });
    // The state keeps the new schema versions and no roll-forward record.
    const data = fake.cluster.get(`configmap/${STATE_NAME}`).data;
    assert.equal(data['environment.schema'], String(Math.max(...plan.environmentVersions)));
    assert.equal(data['orchestrator.schema'], String(Math.max(...plan.coreVersions)));
    assert.equal(data['events-and-sessions.schema'], String(Math.max(...plan.eventsVersions)));
    assert.equal(data.handoverPrincipalObjectId, PRINCIPAL_ID);
    assert.ok(!Object.keys(data).some(key => key.startsWith('rollForward.')));
    assert.deepEqual(receipt.steps.filter(step => step.name === 'app-image-set').map(step => step.deployment),
      ['events-and-sessions']);
    assert.deepEqual(receipt.steps.filter(step => step.name === 'app-ready').map(step => step.deployment),
      ['environment', 'orchestrator', 'events-and-sessions']);
    const set = fake.calls.find(call => call.args[4] === 'set');
    assert.deepEqual(set.args.slice(5), ['image', 'deployment/events-and-sessions',
      `events-and-sessions=${imageReference('Agentweaver.EventsAndSessions', digest('b'))}`]);
    assert.ok(fake.calls.some(call => call.args[4] === 'auth' && call.args.includes('deployments.apps/events-and-sessions') &&
      call.args.includes('patch')));
  } finally { fake.done(); }
});

test('a service that does not become ready fails the run after cleanup', async () => {
  const fake = appWorld({ rolloutFails: name => name === 'orchestrator' });
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
    assert.equal(receipt.installation.status, 'partial');
    assert.deepEqual(receipt.steps.map(step => step.name).slice(-4),
      ['app-image-set', 'app-ready', 'app-ready', 'app-ready']);
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
    environment: imageReference('Agentweaver.Environment', digest('d')),
    orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')),
    'events-and-sessions': imageReference('Agentweaver.EventsAndSessions', digest('b')) } });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--apps-only']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.ok(!receipt.steps.some(step => step.name === 'app-image-set'));
    assert.equal(receipt.steps.filter(step => step.name === 'app-ready').length, 3);
  } finally { fake.done(); }
  for (const flag of [['--broker-digest', digest('c')], ['--reconcile-broker-egress'], ['--deploy-apps'],
    ['--handover', 'skip'], ['--postgres-admin', 'a@example.test']])
    assert.throws(() => parseDbMaintenanceArgs([...baseArgs, '--apps-only', ...flag]), /--apps-only cannot be combined/);
});

test('--apps-only fails closed when a service Deployment does not exist', async () => {
  const fake = world({ appImages: { orchestrator: imageReference('Agentweaver.Orchestrator', digest('a')) } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--apps-only']),
      receiptDir: fake.receiptDir }, fake.deps), /environment, events-and-sessions does not exist/);
    assert.ok(!fake.calls.some(call => call.args[4] === 'set' || call.args[4] === 'create'));
  } finally { fake.done(); }
});

test('the connectivity probe runs under the migration egress label with no token and no privileges', () => {
  const job = connectivityProbeJob({ name: 'p', workload: 'identity-broker-migration', runId: 'abcdef123456',
    host: DB_MAINTENANCE_TARGET.postgresHost, namespace: 'agentweaver' });
  const pod = job.spec.template.spec;
  assert.equal(job.spec.template.metadata.labels['agentweaver.io/workload'], 'identity-broker-migration');
  assert.equal(pod.automountServiceAccountToken, false);
  assert.deepEqual(pod.containers[0].resources.requests, { cpu: '25m', memory: '32Mi' });
  assert.equal(pod.containers[0].securityContext.allowPrivilegeEscalation, false);
  assert.match(pod.containers[0].image, /@sha256:[0-9a-f]{64}$/);
  assert.match(pod.containers[0].command[2], new RegExp(CONNECTIVITY_MARKER));
});

test('P1 migration Jobs provide valid settings and Events provider values match runtime config', () => {
  // Render the checked-in overlay and load the same Events & Sessions values used by its runtime Deployment.
  const resources = parseKustomizeResources(kustomize('deploy/k8s/migrations/p1-postgres').stdout).items;
  const runtime = JSON.parse(readFileSync(
    'deploy/k8s/runtime-configs/events-and-sessions-runtime-config.json', 'utf8')).data;
  // Select each named Job and index only its explicit environment so omitted values cannot pass by using defaults.
  const environmentFor = name => {
    const job = resources.find(item => item.kind === 'Job' && item.metadata?.name === name);
    assert.ok(job, `Rendered migration overlay must contain Job ${name}.`);
    return {
      command: job.spec.template.spec.containers[0].command,
      env: new Map(job.spec.template.spec.containers[0].env.map(({ name: key, value }) => [key, value])),
    };
  };
  const events = environmentFor('events-and-sessions-migration');
  const environment = environmentFor('environment-migration');
  const orchestrator = environmentFor('orchestrator-migration');
  const eventValue = key => {
    assert.ok(events.env.has(key), `${key} must be explicit in the Events & Sessions Job.`);
    return events.env.get(key);
  };
  const integer = key => {
    const value = eventValue(key);
    assert.match(value, /^-?\d+$/, `${key} must be an integer string.`);
    const parsed = Number(value);
    assert.ok(Number.isSafeInteger(parsed), `${key} must be safely representable as an integer.`);
    return parsed;
  };

  // Compare every Sessions and Messaging option that Program validates before it reaches --migrate.
  const providerKeys = [
    'EventsAndSessions__Provider__ResourceId',
    'EventsAndSessions__Provider__DatabaseName',
    'EventsAndSessions__Provider__ResourceGeneration',
    'EventsAndSessions__Provider__Schema',
    'EventsAndSessions__Provider__OptionsRevision',
    'EventsAndSessions__Provider__OptionsSchemaVersion',
    'EventsAndSessions__Provider__PollIntervalMilliseconds',
    'EventsAndSessions__Provider__ReferenceRetentionDays',
  ];
  const messagingKeys = [
    'EventsAndSessions__Messaging__OptionsRevision',
    'EventsAndSessions__Messaging__OptionsSchemaVersion',
    'EventsAndSessions__Messaging__ClaimLeaseSeconds',
    'EventsAndSessions__Messaging__MaximumMessageLifetimeDays',
  ];
  for (const key of [...providerKeys, ...messagingKeys])
    assert.equal(eventValue(key), runtime[key], `${key} must match the Events & Sessions runtime config.`);

  // Mirror the Sessions provider Validate() requirements so matching an invalid runtime value also fails.
  assert.match(eventValue(providerKeys[0]), /^[A-Za-z0-9._:-]{1,256}$/);
  assert.match(eventValue(providerKeys[1]), /^[A-Za-z0-9._:-]{1,256}$/);
  assert.ok(integer(providerKeys[2]) >= 1);
  const schema = eventValue(providerKeys[3]);
  assert.match(schema, /^[a-z][a-z0-9_]{0,62}$/);
  assert.ok(!['public', 'pg_catalog', 'information_schema'].includes(schema) && !schema.startsWith('pg_'));
  assert.match(eventValue(providerKeys[4]), /^[A-Za-z0-9._:-]{1,128}$/);
  assert.equal(integer(providerKeys[5]), 1);
  assert.ok(integer(providerKeys[6]) >= 50 && integer(providerKeys[6]) <= 30_000);
  assert.ok(integer(providerKeys[7]) >= 1 && integer(providerKeys[7]) <= 3650);

  // Mirror the Messaging provider Validate() requirements for revision and numeric bounds.
  const messagingRevision = eventValue(messagingKeys[0]);
  assert.ok(messagingRevision.length > 0 && messagingRevision.length <= 128 &&
    !/[\s\u0000-\u001f\u007f-\u009f]/u.test(messagingRevision));
  assert.equal(integer(messagingKeys[1]), 1);
  assert.ok(integer(messagingKeys[2]) >= 10 && integer(messagingKeys[2]) <= 3600);
  assert.ok(integer(messagingKeys[3]) >= 1 && integer(messagingKeys[3]) <= 7);

  // Check migration-only settings for all Jobs; Orchestrator skips provider validation before --migrate.
  assert.deepEqual(environment.command, ['dotnet', 'Agentweaver.Environment.dll', '--migrate']);
  assert.deepEqual(events.command, ['dotnet', 'Agentweaver.EventsAndSessions.dll', '--migrate']);
  assert.deepEqual(orchestrator.command, ['dotnet', 'Agentweaver.Orchestrator.dll', '--migrate']);
  for (const [env, service] of [
    [environment.env, 'Environment'],
    [events.env, 'EventsAndSessions'],
    [orchestrator.env, 'Orchestrator'],
  ]) {
    assert.ok(env.get(`ConnectionStrings__${service}Migration`), `${service} migration connection must be set.`);
    assert.match(env.get(`${service}__Migration__WorkloadIdentity__TenantId`), /^[0-9a-f-]{36}$/i);
    assert.match(env.get(`${service}__Migration__WorkloadIdentity__ClientId`), /^[0-9a-f-]{36}$/i);
    assert.equal(env.get(`${service}__Migration__WorkloadIdentity__TokenFilePath`),
      '/var/run/secrets/azure/tokens/azure-identity-token');
    assert.ok(env.get(`${service}__Migration__RuntimeRole`));
    assert.equal(env.get(`${service}__Migration__PlanSha256`), REVIEWED_PLAN_SHA256);
  }
});

const eventsImage = character => imageReference('Agentweaver.EventsAndSessions', digest(character));
const environmentImage = character => imageReference('Agentweaver.Environment', digest(character));
const apps = (running, expected = 'b') => [
  { deployment: 'environment', component: 'Agentweaver.Environment', status: 'match',
    expected: environmentImage('d'), running: environmentImage('d') },
  { deployment: 'orchestrator', component: 'Agentweaver.Orchestrator', status: 'match',
    expected: imageReference('Agentweaver.Orchestrator', digest('a')), running: imageReference('Agentweaver.Orchestrator', digest('a')) },
  { deployment: 'events-and-sessions', component: 'Agentweaver.EventsAndSessions', status: 'mismatch',
    expected: eventsImage(expected), running: eventsImage(running) },
];
const schemas = { environment: 11, orchestrator: 33, 'events-and-sessions': 9 };
const range = (minSchema, maxSchema) => ({ minSchema, maxSchema });

test('the service order follows the schema ranges of the running and target images', () => {
  // Ranges are keyed by image reference, as the runner reads them from each image.
  const order = (running, target, current) => planServiceOrder({ appImages: apps('e'),
    ranges: { [environmentImage('d')]: range(11, 11), [apps('e')[1].expected]: range(31, 33),
      [eventsImage('e')]: running, [eventsImage('b')]: target },
    targetSchema: schemas, currentSchema: { environment: 11, orchestrator: 31, 'events-and-sessions': current } });
  // The running image accepts the target schema: migrate first; deploying is optional.
  let result = order(range(8, 9), range(9, 9), 8);
  assert.equal(result.order, 'migrate-first');
  assert.equal(result.services[2].action, 'migrate-first');
  // The target image accepts the current schema: deploy first, so the service stays ready.
  result = order(undefined, range(8, 9), 8);
  assert.equal(result.order, 'deploy-first');
  assert.equal(result.services[2].action, 'deploy-first');
  // Neither image accepts the other's schema: a planned readiness gap, not an error.
  result = order(range(8, 8), range(9, 9), 8);
  assert.equal(result.order, 'planned-readiness-gap');
  assert.equal(result.services[2].action, 'gap');
  // An unknown running image and an unrecorded current schema never assume compatibility.
  assert.equal(order(undefined, range(8, 9), undefined).order, 'planned-readiness-gap');
  // A matching service needs no action.
  assert.equal(result.services[0].action, 'none');
  // A target image that rejects the reviewed plan's own target schema is the wrong digest.
  assert.throws(() => order(undefined, range(7, 8), 8), /accepts schema 7-8, not 9 from the reviewed plan/);
  assert.throws(() => order(undefined, undefined, 8), /accepts schema unknown, not 9/);
});

test('the schema range comes from the readiness check in the migrator source', () => {
  // Orchestrator requires 31 at startup and accepts optional 032 and rollout-compatible 033.
  // Git output passes through redaction, which must not change the parsed text.
  const read = (path, component) => schemaRangeFromSource(redact(readFileSync(path, 'utf8')), component);
  assert.deepEqual(read('services/orchestrator/Agentweaver.Orchestrator/CoordinationOwnerMigrator.cs',
    'Agentweaver.Orchestrator'), range(31, 33));
  assert.deepEqual(read('services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs',
    'Agentweaver.EventsAndSessions'), range(Math.max(...plan.eventsVersions), Math.max(...plan.eventsVersions)));
  const environmentSource = readFileSync(
    'services/environment/Agentweaver.Environment/EnvironmentMigrator.cs', 'utf8');
  assert.deepEqual(schemaRangeFromSource(environmentSource, 'Agentweaver.Environment'), range(11, 11));
  assert.deepEqual(schemaRangeFromSource(environmentSource.replace('CurrentSchemaVersion = 11', 'CurrentSchemaVersion = 10'),
    'Agentweaver.Environment'), range(10, 10));
  assert.equal(schemaRangeFromSource(environmentSource.replace('pending.Length > 0', 'pending.Length > 1'),
    'Agentweaver.Environment'), undefined);
  // An older image with no optional version accepts exactly its own highest version.
  const older = ['WHERE version = 1),', 'WHERE version = 21) = 1,', 'WHERE version NOT IN (1, 2, 21)),']
    .map(line => `SELECT count(*) FROM s.coordination_schema_migrations ${line}`).join('\n');
  assert.deepEqual(schemaRangeFromSource(older, 'Agentweaver.Orchestrator'), range(21, 21));
  // Other tables, a missing or repeated allow-list, and unknown components give no range.
  assert.equal(schemaRangeFromSource(older, 'Agentweaver.EventsAndSessions'), undefined);
  assert.equal(schemaRangeFromSource(older.split('\n').slice(0, 2).join('\n'), 'Agentweaver.Orchestrator'), undefined);
  assert.equal(schemaRangeFromSource(`${older}\n${older}`, 'Agentweaver.Orchestrator'), undefined);
  assert.equal(schemaRangeFromSource(older, 'Agentweaver.Other'), undefined);
});

test('pinned owner readiness shapes derive 31-32 before repository identity and 31-33 after it', async () => {
  // Ledger projections from the two published-source commits. Keep the nested optional-033
  // parentheses: this regression must not depend on the current source or a full git checkout.
  const oldCommit = 'aab23a5daeca847966e92944a653423f2541d665';
  const newCommit = '077884663e7f7b727199fa56d405c005ccebe650';
  const oldSource = `
    (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version NOT IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32)),
    (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 31) = 1,
    ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 0
    ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 1`;
  const newSource = `
    (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version NOT IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 33, 32)),
    (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 31) = 1,
    (((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 33) = 0
     OR ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 33) = 1
    ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 0
    ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 1`;
  const imageCommits = {
    [imageReference('Agentweaver.Orchestrator', digest('e'))]: oldCommit,
    [imageReference('Agentweaver.Orchestrator', digest('a'))]: newCommit,
  };
  const sources = { [oldCommit]: oldSource, [newCommit]: newSource };
  const derive = async image => readImageSchemaRange({ image, component: 'Agentweaver.Orchestrator', repoRoot: '.',
    readImage: async reference => ({ sourceSha: imageCommits[reference] }),
    exec: (command, args) => {
      // No registry or git network access: cat-file and show return pinned local shapes.
      if (command === 'git' && args[0] === 'cat-file') return ok();
      if (command === 'git' && args[0] === 'show') return ok(sources[args[1].split(':')[0]]);
      throw new Error(`Unexpected ${command} ${args.join(' ')}`);
    } });
  const oldRange = await derive(imageReference('Agentweaver.Orchestrator', digest('e')));
  const newRange = await derive(imageReference('Agentweaver.Orchestrator', digest('a')));
  assert.deepEqual(oldRange, { ...range(31, 32), sourceSha: oldCommit });
  assert.deepEqual(newRange, { ...range(31, 33), sourceSha: newCommit });
  // The source-owned compatibility contract agrees with the parsed readiness range, without
  // requiring the parser to depend on metadata that older images do not have.
  const compatibility = JSON.parse(readFileSync(
    'services/orchestrator/Agentweaver.Orchestrator/schema-compatibility.json', 'utf8'));
  assert.equal(newRange.minSchema, Math.max(...compatibility.currentImage.requiredCoreVersions));
  assert.equal(newRange.maxSchema, Math.max(...compatibility.currentImage.optionalCoreVersions));
  assert.equal(oldRange.maxSchema, compatibility.preRepositoryIdentityImage.maximumKnownCoreVersion);
  assert.ok(oldRange.maxSchema < 33);
  assert.deepEqual(plan.coreVersions, compatibility.applyByDefault);
  assert.ok(!plan.coreVersions.includes(17));
  assert.ok(plan.coreVersions.includes(32));
});

test('old-to-new owner upgrade deploys before Core 033 when schema 031 is recorded, otherwise plans a gap', async () => {
  for (const current of [31, undefined]) {
    // The new owner accepts the complete old schema at startup, so it can roll out before 033.
    // An unrecorded schema cannot prove that and takes the bounded planned-gap path instead.
    const fake = world({
      appImages: { orchestrator: imageReference('Agentweaver.Orchestrator', digest('e')),
        'events-and-sessions': eventsImage('b') },
      state: { kind: 'ConfigMap', metadata: { name: STATE_NAME },
        data: { handoverPrincipalObjectId: PRINCIPAL_ID, ...(current ? { 'orchestrator.schema': String(current) } : {}) } },
      schemaRanges: { [digest('a')]: range(31, 33), [digest('e')]: range(31, 32), [digest('b')]: range(9, 9) },
    });
    try {
      const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
        receiptDir: fake.receiptDir }, fake.deps);
      assert.equal(receipt.result, 'succeeded');
      assert.equal(receipt.serviceOrder.order, current ? 'deploy-first' : 'planned-readiness-gap');
      const names = receipt.steps.map(step => step.name);
      assert.equal(names.indexOf('app-ready') < names.indexOf('orchestrator-migrated'), Boolean(current));
      assert.equal(fake.cluster.get(`configmap/${STATE_NAME}`).data['orchestrator.schema'], '33');
      assert.equal(receipt.readinessGap?.planned, current ? undefined : true);
    } finally { fake.done(); }
  }
});

test('a matching or absent old target image cannot bypass the Core 033 compatibility gate', async () => {
  for (const running of [undefined, imageReference('Agentweaver.Orchestrator', digest('a'))]) {
    const fake = world({ appImages: { orchestrator: running },
      schemaRanges: { [digest('a')]: range(31, 32), [digest('b')]: range(9, 9) } });
    try {
      await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
        receiptDir: fake.receiptDir }, fake.deps), /accepts schema 31-32, not 33 from the reviewed plan/);
      assert.ok(!fake.calls.some(call => call.args[4] === 'create'));
    } finally { fake.done(); }
  }
});

test('an image schema range is read from its source label and the source at that commit', async () => {
  const sha = '1'.repeat(40);
  const source = readFileSync('services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs', 'utf8');
  const gitCalls = [];
  // A fake git: the commit is missing until fetched, then show returns the migrator source.
  const fakeGit = ({ fetched = false, showFails = false } = {}) => (command, args) => {
    gitCalls.push(args);
    if (args[0] === 'cat-file') return fetched ? ok() : fail('missing');
    if (args[0] === 'fetch') { fetched = true; return ok(); }
    if (args[0] === 'show') return showFails ? fail('bad object') : ok(source);
    throw new Error(`Unexpected git ${args.join(' ')}`);
  };
  const read = (overrides = {}) => readImageSchemaRange({ image: eventsImage('b'), component: 'Agentweaver.EventsAndSessions',
    readImage: async () => ({ sourceSha: sha }), exec: fakeGit(), repoRoot: '.', ...overrides });
  assert.deepEqual(await read(), { minSchema: 9, maxSchema: 9, sourceSha: sha });
  // A missing commit is fetched by name once, then read.
  assert.deepEqual(gitCalls.map(args => args[0]), ['cat-file', 'fetch', 'show']);
  assert.deepEqual(gitCalls[2], ['show', `${sha}:services/events-and-sessions/Agentweaver.EventsAndSessions/EventsAndSessionsMigrator.cs`]);
  // Every failure is reported as unknown with a reason; the caller decides what that means.
  assert.match((await read({ readImage: async () => { throw new Error('denied'); } })).unknown, /could not read the image labels: denied/);
  assert.match((await read({ readImage: async () => ({ sourceSha: 'main' }) })).unknown, /no source commit label/);
  assert.match((await read({ exec: fakeGit({ showFails: true }) })).unknown, /could not read .*EventsAndSessionsMigrator\.cs at 1{40}/);
  assert.match((await read({ image: 'ghcr.io/sabbour/agentweaver-events-and-sessions:latest' })).unknown, /not a published/);
  assert.match((await read({ image: `docker.io/other/events@${digest('b')}` })).unknown, /not a published/);
});

test('a target image whose schema range cannot be read stops the run before any change', async () => {
  const fake = appWorld({ schemaRanges: { [digest('a')]: range(31, 33) } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([
      ...baseArgs, '--events-digest', digest('c'), '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps), /Could not read the schema range of events-and-sessions image .*not in the test registry/);
    assert.ok(!fake.calls.some(call => call.args[4] === 'create'));
  } finally { fake.done(); }
});

test('a target digest built for another schema stops the run before any change', async () => {
  const fake = appWorld({ schemaRanges: { [digest('a')]: range(31, 33), [digest('b')]: range(8, 8) } });
  try {
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps), /accepts schema 8-8, not 9 from the reviewed plan/);
    assert.ok(!fake.calls.some(call => call.args[4] === 'create'));
  } finally { fake.done(); }
});

test('deploy-first moves a service to an image that accepts the current schema before any migration Job', async () => {
  const fake = appWorld({ state: { kind: 'ConfigMap', metadata: { name: STATE_NAME },
      data: { handoverPrincipalObjectId: PRINCIPAL_ID, 'events-and-sessions.schema': '8' } },
    schemaRanges: { [digest('a')]: range(31, 33), [digest('b')]: range(8, 9) } });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute', '--deploy-apps']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.serviceOrder.order, 'deploy-first');
    assert.equal(receipt.readinessGap, undefined);
    const names = receipt.steps.map(step => step.name);
    assert.ok(names.indexOf('app-image-set') < names.indexOf('app-ready'));
    assert.ok(names.indexOf('app-ready') < names.indexOf('orchestrator-migrated'));
    assert.equal(names.filter(name => name === 'app-image-set').length, 1);
  } finally { fake.done(); }
});

test('migrate-first keeps a running image that accepts the target schema when no deploy is asked for', async () => {
  const fake = appWorld({ schemaRanges: { [digest('a')]: range(31, 33), [digest('b')]: range(9, 9), [digest('e')]: range(8, 9) } });
  try {
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs([...baseArgs, '--execute']),
      receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.serviceOrder.order, 'migrate-first');
    assert.ok(!receipt.steps.some(step => step.name === 'app-image-set'));
    assert.deepEqual(receipt.steps.filter(step => step.name === 'app-ready').map(step => step.image),
      [environmentImage('d'), imageReference('Agentweaver.Orchestrator', digest('a')),
        imageReference('Agentweaver.EventsAndSessions', digest('e'))]);
  } finally { fake.done(); }
});

test('a readiness timeout after the migration only rolls forward to the same digests', async () => {
  let unready = true;
  const fake = appWorld({ rolloutFails: name => unready && name === 'events-and-sessions' });
  try {
    const args = [...baseArgs, '--execute', '--deploy-apps', '--readiness-timeout-seconds', '60'];
    await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs(args), receiptDir: fake.receiptDir }, fake.deps),
      /events-and-sessions did not become ready within 60 seconds[\s\S]*Do not roll back; rerun the same command to roll forward/);
    const failed = fake.receipts[0].receipt;
    assert.equal(failed.result, 'failed');
    assert.equal(failed.readinessGap.startedAt, '2026-10-22T10:00:00.123Z');
    assert.equal(failed.readinessGap.endedAt, undefined);
    assert.equal(failed.cleanup.lock, true);
    const expected = imageReference('Agentweaver.EventsAndSessions', digest('b'));
    const data = fake.cluster.get(`configmap/${STATE_NAME}`).data;
    assert.equal(data['rollForward.events-and-sessions'], expected);
    assert.equal(data['rollForward.runId'], 'abcdef123456');

    // Other digests, including the old image, are refused before the lock.
    for (const other of ['e', 'f']) {
      const before = fake.calls.length;
      const otherArgs = [...args];
      otherArgs[5] = digest(other);
      await assert.rejects(runDbMaintenance({ ...parseDbMaintenanceArgs(otherArgs), receiptDir: fake.receiptDir }, fake.deps),
      /must roll forward[\s\S]*events-and-sessions=.*sha256:b{64}[\s\S]*Rolling back to older images is not supported/);
      assert.ok(!fake.calls.slice(before).some(call => call.args[4] === 'create' || call.args[4] === 'set'));
    }

    // The same command rolls forward and clears the record; no old image is ever set.
    unready = false;
    const receipt = await runDbMaintenance({ ...parseDbMaintenanceArgs(args), receiptDir: fake.receiptDir }, fake.deps);
    assert.equal(receipt.result, 'succeeded');
    assert.equal(receipt.rollForward.pendingFromRun, 'abcdef123456');
    assert.ok(receipt.steps.some(step => step.name === 'roll-forward-cleared'));
    assert.ok(!Object.keys(fake.cluster.get(`configmap/${STATE_NAME}`).data).some(key => key.startsWith('rollForward.')));
    assert.ok(fake.calls.filter(call => call.args[4] === 'set').every(call => call.args.at(-1).endsWith(digest('b'))));
  } finally { fake.done(); }
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
  assert.throws(() => parseDbMaintenanceArgs(['--prepare-only', ...baseArgs]), /--prepare-only cannot be combined with --environment-digest/);
  assert.throws(() => parseDbMaintenanceArgs(['--prepare-only', '--deploy-apps']), /--deploy-apps/);
});
