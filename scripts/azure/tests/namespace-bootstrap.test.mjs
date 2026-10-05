import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { dirname, isAbsolute, join } from 'node:path';
import { bootstrapP0Namespace } from '../lib/namespace-bootstrap.mjs';

const target = {
  resourceGroup: 'aw-v1-p0',
  subscriptionId: '11111111-1111-1111-1111-111111111111',
  repoRoot: process.cwd(),
  clusterName: 'aw-v1-p0-aks',
};
const namespace = {
  metadata: {
    name: 'agentweaver-v1-p0',
    labels: {
      'agentweaver.io/environment': 'v1-p0',
      'agentweaver.io/managed-by': 'kustomize',
      'pod-security.kubernetes.io/enforce': 'restricted',
      'pod-security.kubernetes.io/audit': 'restricted',
      'pod-security.kubernetes.io/warn': 'restricted',
    },
  },
};
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });
const denied = stderr => ({ status: 1, stdout: '', stderr });

function fakeKubelogin(calls = []) {
  return (args, options) => {
    calls.push({ args, options });
    return ok();
  };
}

function fakeKubernetes({
  authorization = () => ok('yes\n'),
  apply = () => ok('namespace/agentweaver-v1-p0 configured'),
  readback = () => ok(JSON.stringify(namespace)),
} = {}) {
  const calls = [];
  const execKubectl = (args, options) => {
    calls.push({ args, options });
    const command = args.slice(2);
    if (command[0] === 'auth') return authorization(command, calls);
    if (command[0] === 'apply') return apply(command, calls);
    if (command[0] === 'get') return readback(command, calls);
    throw new Error(`Unexpected kubectl command: ${args.join(' ')}`);
  };
  return { calls, execKubectl };
}

function kubeconfigPath(calls) {
  return calls[0].args[calls[0].args.indexOf('--kubeconfig') + 1];
}

test('AKS bootstrap uses normal user credentials and applies then reads back only the P0 namespace', () => {
  const azureCalls = [];
  const kubeloginCalls = [];
  const waits = [];
  const { calls, execKubectl } = fakeKubernetes({
    authorization: (() => {
      const counts = new Map();
      return command => {
        const key = command.slice(2).join(' ');
        const count = (counts.get(key) ?? 0) + 1;
        counts.set(key, count);
        return key === 'create namespaces' && count === 1 ? ok('no\n') : ok('yes\n');
      };
    })(),
    readback: (() => {
      let count = 0;
      return () => (++count === 1
        ? denied('(NotFound) namespaces "agentweaver-v1-p0" not found')
        : ok(JSON.stringify(namespace)));
    })(),
  });
  const result = bootstrapP0Namespace(target, {
    execAz: (args, options) => {
      azureCalls.push({ args, options });
      return ok();
    },
    execKubelogin: fakeKubelogin(kubeloginCalls),
    execKubectl,
    pause: milliseconds => waits.push(milliseconds),
  });

  assert.deepEqual(result, { namespace: 'agentweaver-v1-p0', clusterName: target.clusterName });
  assert.deepEqual(azureCalls[0].args.slice(0, 2), ['aks', 'get-credentials']);
  assert.ok(!azureCalls[0].args.includes('--admin'));
  assert.equal(azureCalls[0].args[azureCalls[0].args.indexOf('--resource-group') + 1], target.resourceGroup);
  assert.equal(azureCalls[0].args[azureCalls[0].args.indexOf('--subscription') + 1], target.subscriptionId);
  assert.equal(azureCalls[0].args[azureCalls[0].args.indexOf('--name') + 1], target.clusterName);
  assert.ok(isAbsolute(kubeconfigPath(calls)));
  assert.deepEqual(kubeloginCalls[0].args.slice(0, 3), ['convert-kubeconfig', '--login', 'azurecli']);
  assert.equal(kubeloginCalls[0].args[kubeloginCalls[0].args.indexOf('--kubeconfig') + 1],
    azureCalls[0].args[azureCalls[0].args.indexOf('--file') + 1]);
  assert.ok(!azureCalls[0].args.includes('--admin'));
  assert.deepEqual(waits, [5_000, 5_000]);
  const applyCalls = calls.filter(({ args }) => args.includes('apply'));
  assert.equal(applyCalls.length, 1);
  assert.equal(applyCalls[0].args[applyCalls[0].args.indexOf('--filename') + 1],
    join(target.repoRoot, 'deploy', 'k8s', 'base', 'namespace.yaml'));
  assert.ok(calls.every(({ args }) => args[args.indexOf('--kubeconfig') + 1] === kubeconfigPath(calls)));
  assert.ok(calls.every(({ args }) => !args.includes('-f') || args.includes('apply')));
  assert.equal(existsSync(dirname(kubeconfigPath(calls))), false);
});

test('namespace bootstrap fails explicitly and cleans up if Azure CLI login conversion fails', () => {
  const azureCalls = [];
  const kubeloginCalls = [];
  const { calls, execKubectl } = fakeKubernetes();
  assert.throws(() => bootstrapP0Namespace(target, {
    execAz: (args, options) => {
      azureCalls.push({ args, options });
      return ok();
    },
    execKubelogin: (args, options) => {
      kubeloginCalls.push({ args, options });
      return denied('kubelogin conversion failed');
    },
    execKubectl,
    pause() {},
  }), /Could not configure AKS kubeconfig to use the signed-in Azure CLI identity: kubelogin conversion failed/);
  const kubeconfig = kubeloginCalls[0].args[kubeloginCalls[0].args.indexOf('--kubeconfig') + 1];
  assert.equal(azureCalls[0].args[azureCalls[0].args.indexOf('--file') + 1], kubeconfig);
  assert.ok(!azureCalls[0].args.includes('--admin'));
  assert.equal(calls.length, 0);
  assert.equal(existsSync(dirname(kubeconfig)), false);
});

test('namespace bootstrap stops before apply when scoped namespace permissions remain denied', () => {
  const waits = [];
  const { calls, execKubectl } = fakeKubernetes({
    authorization: command => command[2] === 'patch' ? ok('no\n') : ok('yes\n'),
  });
  assert.throws(() => bootstrapP0Namespace(target, {
    execAz: () => ok(),
    execKubelogin: fakeKubelogin(),
    execKubectl,
    pause: milliseconds => waits.push(milliseconds),
  }), /permissions did not become available within 12 attempts \(patch namespace\/agentweaver-v1-p0/);
  assert.equal(calls.filter(({ args }) => args.includes('apply')).length, 0);
  assert.equal(waits.length, 11);
  assert.equal(existsSync(dirname(kubeconfigPath(calls))), false);
});

test('namespace bootstrap reports an apply/create denial without attempting readback', () => {
  const { calls, execKubectl } = fakeKubernetes({
    apply: () => denied('(Forbidden) cannot create resource "namespaces"'),
  });
  assert.throws(() => bootstrapP0Namespace(target, {
    execAz: () => ok(),
    execKubelogin: fakeKubelogin(),
    execKubectl,
    pause() {},
  }), /Could not apply the namespace-only manifest .*Forbidden/);
  assert.equal(calls.filter(({ args }) => args[2] === 'get').length, 0);
  assert.equal(calls.filter(({ args }) => args.includes('apply')).length, 1);
  assert.equal(existsSync(dirname(kubeconfigPath(calls))), false);
});

test('namespace bootstrap bounds denied readback and always removes its temporary kubeconfig', () => {
  const waits = [];
  const { calls, execKubectl } = fakeKubernetes({
    readback: () => denied('(Forbidden) cannot get resource "namespaces"'),
  });
  assert.throws(() => bootstrapP0Namespace(target, {
    execAz: () => ok(),
    execKubelogin: fakeKubelogin(),
    execKubectl,
    pause: milliseconds => waits.push(milliseconds),
  }), /readback did not converge within 12 attempts \(namespace readback: .*Forbidden/);
  assert.equal(calls.filter(({ args }) => args.includes('apply')).length, 1);
  assert.equal(calls.filter(({ args }) => args[2] === 'get').length, 12);
  assert.equal(waits.length, 11);
  assert.equal(existsSync(dirname(kubeconfigPath(calls))), false);
});
