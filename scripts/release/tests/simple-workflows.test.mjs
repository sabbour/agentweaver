import assert from 'node:assert/strict';
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { parse } from 'yaml';
import { deployKustomize, parseDeployKustomizeArgs } from '../deploy-kustomize.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const REGISTRY = 'ghcr.io/sabbour';
const readYaml = relativePath => parse(readFileSync(path.join(root, relativePath), 'utf8'));
const imageDigest = `sha256:${'a'.repeat(64)}`;
const serviceClientId = '12345678-1234-4123-8123-123456789abc';
const serviceNames = [
  'identity-broker', 'events-and-sessions', 'gateway', 'mcp', 'web',
  'orchestrator', 'environment', 'knowledge', 'projects-config',
];
const jobNames = [
  'identity-broker-migration', 'environment-migration', 'orchestrator-migration',
  'events-and-sessions-migration', 'projects-config-migration',
];

function scratch() {
  const directory = path.join(root, '.test-scratch', `simple-deploy-${process.pid}-${Math.random().toString(16).slice(2)}`);
  mkdirSync(directory, { recursive: true });
  cpSync(path.join(root, 'deploy', 'k8s'), path.join(directory, 'deploy', 'k8s'), { recursive: true });
  return directory;
}

function job(name) {
  return {
    apiVersion: 'batch/v1',
    kind: 'Job',
    metadata: { name, namespace: 'agentweaver' },
    spec: { template: { metadata: { labels: {} }, spec: { containers: [{
      name: 'migration',
      image: 'registry.invalid/image@sha256:0000000000000000000000000000000000000000000000000000000000000000',
      env: [{ name: 'Service__Migration__WorkloadIdentity__ClientId', value: '00000000-0000-0000-0000-000000000000' }],
    }] } } },
  };
}

function renderedRoot(kustomizationPath) {
  const source = parse(readFileSync(kustomizationPath, 'utf8'));
  assert.equal(source.images.length, 10);
  assert.equal(source.images.every(image => image.digest === imageDigest), true);
  return [
    'apiVersion: v1\nkind: Namespace\nmetadata:\n  name: agentweaver\n  labels:\n    pod-security.kubernetes.io/enforce: restricted\n',
    ...serviceNames.map(name =>
      `apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: ${name}\nspec: {}\n`),
  ].join('---\n');
}

function fakeExec(calls, { failedJob, failedRollout, missingProjectsConfigClientId } = {}) {
  return (command, args, options = {}) => {
    calls.push({ command, args, options });
    if (command === 'docker') return { status: 0, stdout: `${imageDigest}\n`, stderr: '' };
    if (command === 'az') return { status: 0, stdout: `${serviceClientId}\n`, stderr: '' };
    if (command !== 'kubectl') return { status: 1, stdout: '', stderr: `unexpected command ${command}` };
    if (args[0] === 'config') return { status: 0, stdout: 'selected-context\n', stderr: '' };
    if (args[0] === 'auth') return { status: 0, stdout: 'yes\n', stderr: '' };
    if (args[0] === 'get' && args[1] === 'serviceaccount' && args[2] === 'p1-postgres-migration') {
      return { status: 0, stdout: JSON.stringify({ metadata: {
        annotations: { 'azure.workload.identity/client-id': serviceClientId },
      } }), stderr: '' };
    }
    if (args[0] === 'get' && args[1] === 'serviceaccount' && args[2] === 'projects-config-migration') {
      return { status: 0, stdout: JSON.stringify({ metadata: {
        annotations: missingProjectsConfigClientId ? {} : {
          'azure.workload.identity/client-id': serviceClientId,
        },
      } }), stderr: '' };
    }
    if (args[0] === 'get') {
      const name = args[2];
      return { status: 0, stdout: JSON.stringify({ kind: args[1], metadata: { name } }), stderr: '' };
    }
    if (args[0] === 'kustomize') {
      const relative = path.relative(calls[0].options.cwd, args[1]).replaceAll('\\', '/');
      if (relative === 'deploy/k8s') {
        return { status: 0, stdout: renderedRoot(path.join(args[1], 'kustomization.yaml')), stderr: '' };
      }
      if (relative.endsWith('migrations/p1-postgres')) {
        return { status: 0, stdout: [
          job('environment-migration'), job('orchestrator-migration'), job('events-and-sessions-migration'),
        ].map(value => `---\n${JSON.stringify(value)}`).join('\n'), stderr: '' };
      }
      if (relative.endsWith('migrations/identity-broker')) {
        return { status: 0, stdout: `---\n${JSON.stringify(job('identity-broker-migration'))}`, stderr: '' };
      }
      return { status: 1, stdout: '', stderr: `unexpected kustomize path ${relative}` };
    }
    if (args[0] === 'create') return { status: 0, stdout: 'job created\n', stderr: '' };
    if (args[0] === 'wait') {
      const name = args[2].slice('job/'.length);
      return name.startsWith(failedJob ?? '\u0000')
        ? { status: 1, stdout: '', stderr: 'job failed' }
        : { status: 0, stdout: 'condition met\n', stderr: '' };
    }
    if (args[0] === 'logs') return { status: 0, stdout: 'migration failed safely\n', stderr: '' };
    if (args[0] === 'apply') return { status: 0, stdout: 'resources applied\n', stderr: '' };
    if (args[0] === 'rollout' && args[2] === `deployment/${failedRollout}`) {
      return { status: 1, stdout: '', stderr: 'CrashLoopBackOff' };
    }
    if (args[0] === 'rollout') return { status: 0, stdout: 'deployment successfully rolled out\n', stderr: '' };
    return { status: 0, stdout: '', stderr: '' };
  };
}

test('v1 image workflow pushes the ten existing Kustomize images directly', () => {
  const workflow = readYaml('.github/workflows/v1-integration-images.yml');
  const entries = workflow.jobs['build-and-push'].strategy.matrix.include;
  const overlay = readYaml('deploy/k8s/kustomization.yaml');

  assert.deepEqual(workflow.on.push.branches, ['v1']);
  assert.equal(entries.length, 10);
  assert.deepEqual(
    entries.map(({ repository }) => repository).sort(),
    overlay.images.map(({ name }) => name.replace('ghcr.io/sabbour/', '')).sort(),
  );
  assert.equal(new Set(entries.map(({ id }) => id)).size, 10);

  for (const entry of entries) {
    if (entry.mode === 'dotnet' || entry.mode === 'web')
      assert.equal(existsSync(path.join(root, entry.project)), true, `${entry.id} project exists`);
    if (entry.mode === 'docker')
      assert.equal(existsSync(path.join(root, entry.dockerfile)), true, `${entry.id} Dockerfile exists`);
  }

  const steps = workflow.jobs['build-and-push'].steps;
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6').length, 3);
  assert.equal(steps.filter(({ uses }) => uses === 'docker/build-push-action@v6')
    .every(({ with: options }) => options.provenance === false), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('PublishContainer')), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('imagetools inspect')), true);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);
  assert.equal(JSON.stringify(workflow).includes('source_sha'), false);
});

test('v1 package workflow publishes versioned NuGet packages without image receipts', () => {
  const workflow = readYaml('.github/workflows/v1-release-pack.yml');
  const steps = workflow.jobs['publish-packages'].steps;

  assert.deepEqual(workflow.on.push.branches, ['v1']);
  assert.deepEqual(workflow.on.push.paths, ['releases/foundation.json']);
  assert.equal(steps.some(({ run = '' }) => run.includes('dotnet pack')), true);
  assert.equal(steps.some(({ run = '' }) => run.includes('dotnet nuget push')), true);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);
  assert.equal(JSON.stringify(workflow).includes('publication'), false);
  assert.equal(JSON.stringify(workflow).includes('source_sha'), false);
});

test('manual deployment uses only direct migration Jobs and the Kustomize workload path', () => {
  const workflow = readYaml('.github/workflows/v1-deploy.yml');
  const steps = workflow.jobs.deploy.steps;
  const deployment = steps.find(({ run = '' }) => run.includes('deploy:kustomize'));
  const checkout = steps.find(({ uses = '' }) => uses.startsWith('actions/checkout@'));

  assert.deepEqual(Object.keys(workflow.on), ['workflow_dispatch']);
  assert.match(steps[0].run, /refs\/heads\/v1/);
  assert.equal(checkout.with.ref, 'v1');
  assert.equal(steps.some(({ run = '' }) => run.includes('db:maintenance')), false);
  assert.match(deployment.run, /npm run deploy:kustomize -- --image-tag/);
  assert.equal(JSON.stringify(workflow).includes('AZURE_DB_MAINTENANCE_CLIENT_ID'), true);
  assert.equal(JSON.stringify(workflow).includes('AZURE_V1_DEPLOY_CLIENT_ID'), false);
  assert.equal(steps.some(({ uses = '' }) => /upload-artifact|download-artifact/.test(uses)), false);
  assert.equal(JSON.stringify(workflow).match(/evidence|receipt|provenance|publication/i), null);
});

test('deployment creates existing migration Jobs directly before one namespaced apply', async () => {
  const directory = scratch();
  const kustomizationPath = path.join(directory, 'deploy', 'k8s', 'kustomization.yaml');
  const original = readFileSync(kustomizationPath, 'utf8');
  const calls = [];
  const logs = [];
  try {
    const exec = fakeExec(calls);
    await deployKustomize({ imageTag: `sha-${'b'.repeat(40)}` }, {
      root: directory, exec, log: message => logs.push(message),
    });

    const createCalls = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'create');
    const createdJobs = createCalls.map(call => parse(call.options.input));
    assert.deepEqual(createdJobs.map(value => value.metadata.name.split('-').slice(0, -1).join('-')), jobNames);
    assert.equal(createdJobs.every(value => value.spec.ttlSecondsAfterFinished === 3600), true);
    assert.equal(createdJobs.every(value => value.spec.template.spec.containers[0].image.startsWith(`${REGISTRY}/`)), true);
    assert.equal(createdJobs[1].spec.template.spec.containers[0].env[0].value, serviceClientId);
    const projectsConfigJob = createdJobs[4];
    const projectsConfigSpec = projectsConfigJob.spec.template.spec;
    const projectsConfigEnv = Object.fromEntries(
      projectsConfigSpec.containers[0].env.map(({ name, value }) => [name, value]),
    );
    assert.equal(projectsConfigSpec.serviceAccountName, 'projects-config-migration');
    assert.equal(projectsConfigJob.spec.template.metadata.labels['azure.workload.identity/use'], 'true');
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__ClientId, serviceClientId);
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__TenantId,
      '72f988bf-86f1-41af-91ab-2d7cd011db47');
    assert.equal(projectsConfigEnv.ProjectsConfig__Migration__WorkloadIdentity__TokenFilePath,
      '/var/run/secrets/azure/tokens/azure-identity-token');
    assert.match(projectsConfigEnv.ConnectionStrings__ProjectsConfigMigration, /SSL Mode=VerifyFull/);
    assert.match(projectsConfigEnv.ConnectionStrings__ProjectsConfigMigration, /Username=agentweaver-id-projects-config-migration/);
    assert.equal(projectsConfigSpec.containers[0].env.some(setting => setting.valueFrom?.secretKeyRef), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'auth' &&
      call.args[3] === 'secrets'), false);
    assert.equal(calls.some(call => call.command === 'npm'), false);
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), true);

    const render = calls.findIndex(call => call.command === 'kubectl' &&
      call.args[0] === 'kustomize' && call.args[1].endsWith(path.join('deploy', 'k8s')));
    const firstJob = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'create');
    const apply = calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply');
    const rollouts = calls.filter(call => call.command === 'kubectl' && call.args[0] === 'rollout');
    assert.ok(render >= 0 && render < firstJob && firstJob < apply);
    assert.equal(apply, calls.findIndex(call => call.command === 'kubectl' && call.args[0] === 'apply'));
    assert.deepEqual(calls[apply].args.slice(1, 5), [
      '--filename', '-', '--context', 'selected-context',
    ]);
    const applied = parse(calls[apply].options.input);
    assert.equal(applied.kind, 'List');
    assert.equal(applied.items.some(resource => resource.kind === 'Namespace'), false);
    assert.equal(applied.items.filter(resource => resource.kind === 'Deployment').length, 9);
    assert.equal(rollouts.length, 9);
    assert.equal(logs.some(message => /receipt|evidence/i.test(message)), false);
    assert.equal(readFileSync(kustomizationPath, 'utf8'), original);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a failed migration Job prevents workload apply and rollout', async () => {
  const directory = scratch();
  const calls = [];
  try {
    const exec = fakeExec(calls, { failedJob: 'orchestrator-migration-' });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'c'.repeat(40)}` }, { root: directory, exec, log() {} }),
      /Migration Job orchestrator-migration-.* failed: job failed/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' &&
      ['apply', 'rollout'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a Projects Config migration ServiceAccount without a client ID prevents every migration Job and workload apply', async () => {
  const directory = scratch();
  const calls = [];
  try {
    const exec = fakeExec(calls, { missingProjectsConfigClientId: true });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'e'.repeat(40)}` }, { root: directory, exec, log() {} }),
      /ServiceAccount\/projects-config-migration does not name the approved migration identity/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' &&
      ['create', 'apply', 'rollout'].includes(call.args[0])), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('a failed Deployment rollout does not report a complete deployment', async () => {
  const directory = scratch();
  const calls = [];
  const logs = [];
  try {
    const exec = fakeExec(calls, { failedRollout: 'projects-config' });
    await assert.rejects(
      deployKustomize({ imageTag: `sha-${'d'.repeat(40)}` }, {
        root: directory, exec, log: message => logs.push(message),
      }),
      /Deployment projects-config did not become ready: CrashLoopBackOff/,
    );
    assert.equal(calls.some(call => call.command === 'kubectl' && call.args[0] === 'apply'), true);
    assert.equal(logs.filter(message => message.startsWith('Deployment ') && message.endsWith(' is ready.')).length, 8);
    assert.equal(logs.includes('Deployment projects-config is ready.'), false);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});
