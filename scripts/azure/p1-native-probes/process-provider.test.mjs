import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { createP1NativeProcessProvider } from './process-provider.mjs';

const server = 'https://p1-cluster.example.test/';
const clusterIdentity = {
  uid: createHash('sha256').update(new URL(server).origin).digest('hex'),
};
const expectedClusterIdentity = { uid: clusterIdentity.uid };
const mountOwner = {
  ownerId: 'owner-a',
  actorId: 'actor-a',
  tenantId: 'tenant-a',
  projectId: 'project-a',
  runId: 'run-a',
  environmentId: 'environment-a',
};
const ownerLabel = createHash('sha256')
  .update([
    mountOwner.tenantId,
    mountOwner.projectId,
    mountOwner.runId,
    mountOwner.environmentId,
  ].join('\0'))
  .digest('hex')
  .slice(0, 40);

function createHarness(respond, providerOptions = {}) {
  const calls = [];
  const execute = (command, args, options) => {
    calls.push({ command, args, options });
    return respond(command, args, options);
  };
  return {
    calls,
    provider: createP1NativeProcessProvider({
      kubeContext: 'p1-fixture',
      clusterIdentity,
      execute,
      ...providerOptions,
    }),
  };
}

function configResponse() {
  return {
    apiVersion: 'v1',
    kind: 'Config',
    contexts: [{ name: 'p1-fixture', context: { cluster: 'p1-cluster' } }],
    clusters: [{ name: 'p1-cluster', cluster: { server } }],
  };
}

test('only fixed recipes can invoke native commands', async () => {
  const harness = createHarness(() => {
    throw new Error('unexpected command');
  });

  await assert.rejects(
    harness.provider.run({
      recipeId: 'user-command',
      args: { command: 'kubectl delete pod anything', expectedClusterIdentity },
    }),
    /source-owned P1 native recipes/,
  );
  assert.deepEqual(harness.calls, []);
});

test('cluster and namespace receipts come from the selected Kubernetes target', async () => {
  const harness = createHarness((command, args) => {
    assert.equal(command, 'kubectl');
    assert.equal(args.includes('--context'), true);
    assert.equal(args.includes('p1-fixture'), true);
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    assert.deepEqual(args.slice(-4), ['namespace', 'p1-fixture', '-o', 'json']);
    return {
      status: 0,
      stdout: JSON.stringify({
        apiVersion: 'v1',
        kind: 'Namespace',
        metadata: { name: 'p1-fixture', uid: 'namespace-uid-1' },
      }),
      stderr: '',
    };
  });

  const result = await harness.provider.run({
    recipeId: 'observe-cluster',
    args: { namespace: 'p1-fixture', expectedClusterIdentity },
  });
  assert.deepEqual(result, {
    clusterIdentity: expectedClusterIdentity,
    namespace: { name: 'p1-fixture', uid: 'namespace-uid-1' },
  });
  assert.equal(harness.calls.length, 2);
  assert.ok(harness.calls.every(call => call.options.check === false));
});

test('a context pointing at a different API server fails closed', async () => {
  const harness = createHarness((command, args) => ({
    status: 0,
    stdout: JSON.stringify({
      ...configResponse(),
      clusters: [{ name: 'p1-cluster', cluster: { server: 'https://other.example.test/' } }],
    }),
    stderr: '',
  }));

  await assert.rejects(
    harness.provider.run({
      recipeId: 'observe-cluster',
      args: { namespace: 'p1-fixture', expectedClusterIdentity },
    }),
    /does not match the pinned cluster identity/,
  );
  assert.equal(harness.calls.length, 1);
});

test('storage readback verifies PVC, PV, claim, and Environment owner annotations', async () => {
  const ownerAnnotations = {
    'agentweaver.dev/owner-kind': 'Run',
    'agentweaver.dev/owner-id': 'run-a',
  };
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('persistentvolumeclaim')) {
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'PersistentVolumeClaim',
          metadata: {
            name: 'claim-a',
            namespace: 'p1-fixture',
            uid: 'claim-uid-a',
            annotations: {
              'agentweaver.dev/project-id': 'project-a',
              'agentweaver.dev/volume-id': 'volume-a',
              'agentweaver.dev/generation': '3',
              'agentweaver.dev/environment-id': 'environment-a',
              ...ownerAnnotations,
            },
          },
          spec: { volumeName: 'pv-a' },
          status: { phase: 'Bound' },
        }),
        stderr: '',
      };
    }
    assert.ok(args.includes('persistentvolume'));
    return {
      status: 0,
      stdout: JSON.stringify({
        apiVersion: 'v1',
        kind: 'PersistentVolume',
        metadata: { name: 'pv-a', uid: 'pv-uid-a' },
        spec: {
          claimRef: {
            namespace: 'p1-fixture',
            name: 'claim-a',
            uid: 'claim-uid-a',
          },
        },
        status: { phase: 'Bound' },
      }),
      stderr: '',
    };
  });

  const result = await harness.provider.run({
    recipeId: 'observe-storage',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      owner: {
        ownerId: 'owner-a',
        projectId: 'project-a',
        environmentId: 'environment-a',
      },
      volumeOwner: { kind: 'Run', id: 'run-a' },
      volumeId: 'volume-a',
      resourceGeneration: 3,
      claimUid: 'claim-uid-a',
      providerBinding: { providerId: 'azure-files-csi' },
      pvc: { name: 'claim-a', uid: 'claim-uid-a' },
      pv: { name: 'pv-a', uid: 'pv-uid-a' },
    },
  });
  assert.deepEqual(result, {
    clusterIdentity: expectedClusterIdentity,
    pvc: { name: 'claim-a', uid: 'claim-uid-a' },
    pv: { name: 'pv-a', uid: 'pv-uid-a' },
    claimUid: 'claim-uid-a',
  });

  for (const [key, value] of [
    ['agentweaver.dev/owner-kind', 'Team'],
    ['agentweaver.dev/owner-id', 'run-b'],
  ]) {
    ownerAnnotations['agentweaver.dev/owner-kind'] = 'Run';
    ownerAnnotations['agentweaver.dev/owner-id'] = 'run-a';
    ownerAnnotations[key] = value;
    await assert.rejects(
      harness.provider.run({
        recipeId: 'observe-storage',
        args: {
          expectedClusterIdentity,
          namespace: 'p1-fixture',
          owner: {
            ownerId: 'owner-a',
            projectId: 'project-a',
            environmentId: 'environment-a',
          },
          volumeOwner: { kind: 'Run', id: 'run-a' },
          volumeId: 'volume-a',
          resourceGeneration: 3,
          claimUid: 'claim-uid-a',
          providerBinding: { providerId: 'azure-files' },
          pvc: { name: 'claim-a', uid: 'claim-uid-a' },
          pv: { name: 'pv-a', uid: 'pv-uid-a' },
        },
      }),
      /owner annotations do not match/,
    );
  }
});

test('marker reads are bound to the current Pod UID, owner, and exact PVC mount', async () => {
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('get') && args.includes('pod')) {
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'Pod',
          metadata: {
            name: 'agenthost-a',
            namespace: 'p1-fixture',
            uid: 'pod-uid-a',
            labels: { 'agentweaver.dev/sandbox-owner': ownerLabel },
          },
          spec: {
            containers: [{
              name: 'agenthost',
              volumeMounts: [{ name: 'workspace', mountPath: '/workspace' }],
            }],
            volumes: [{
              name: 'workspace',
              persistentVolumeClaim: { claimName: 'claim-a' },
            }],
          },
        }),
        stderr: '',
      };
    }
    if (args.includes('persistentvolumeclaim')) {
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'PersistentVolumeClaim',
          metadata: { name: 'claim-a', namespace: 'p1-fixture', uid: 'claim-uid-a' },
        }),
        stderr: '',
      };
    }
    assert.ok(args.includes('exec'));
    assert.ok(args.includes('sh'));
    return { status: 0, stdout: 'P1:MISSING\n', stderr: '' };
  });

  const result = await harness.provider.run({
    recipeId: 'mount-file',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      podName: 'agenthost-a',
      podUid: 'pod-uid-a',
      expectedPodUid: 'pod-uid-a',
      ownerId: 'owner-a',
      owner: mountOwner,
      workspaceClaimUid: 'claim-uid-a',
      containerName: 'agenthost',
      mountPath: '/workspace',
      path: '/workspace/.agentweaver-p1-a6a04b52-34bd-4c58-8d95-537912dc8e55-0123456789',
      operation: 'stat',
    },
  });
  assert.equal(result.exists, false);
  assert.equal(result.ownerId, 'owner-a');
  assert.equal(result.podUid, 'pod-uid-a');
  assert.ok(harness.calls.every(call =>
    ['kubectl'].includes(call.command) &&
    call.options.check === false &&
    !call.options.shell));
  assert.ok(harness.calls.every(call => !call.args.includes('delete')));
});

test('marker traversal is rejected before running a container command', async () => {
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    throw new Error('unexpected resource read');
  });

  await assert.rejects(
    harness.provider.run({
      recipeId: 'mount-file',
      args: {
        expectedClusterIdentity,
        namespace: 'p1-fixture',
        podName: 'agenthost-a',
        podUid: 'pod-uid-a',
        ownerId: 'owner-a',
        owner: mountOwner,
        workspaceClaimUid: 'claim-uid-a',
        mountPath: '/workspace',
        path: '/workspace/../outside',
        operation: 'unlink',
        expectedContent: 'agentweaver-p1:a6a04b52-34bd-4c58-8d95-537912dc8e55:01234567-89ab-cdef-0123-456789abcdef',
        expectedPodUid: 'pod-uid-a',
      },
    }),
    /outside its exact owned marker path/,
  );
  assert.equal(harness.calls.length, 1);
  assert.equal(harness.calls[0].args.includes('exec'), false);
});

test('service probes require ready endpoints and a matching current Cilium flow', async () => {
  const ownerHash = 'c'.repeat(40);
  const policy = {
    apiVersion: 'cilium.io/v2',
    kind: 'CiliumNetworkPolicy',
    metadata: {
      name: 'egress-a',
      namespace: 'p1-fixture',
      uid: 'policy-uid-a',
      annotations: {
        'agentweaver.dev/egress-generation': '6',
        'agentweaver.dev/egress-intent-sha256': 'd'.repeat(64),
        'agentweaver.dev/egress-revoked': 'false',
      },
    },
    spec: { endpointSelector: { matchLabels: { 'agentweaver.dev/sandbox-owner': ownerHash } } },
  };

  for (const [verdict, probeStatus, expectedConnected] of [
    ['FORWARDED', 0, true],
    ['DROPPED', 1, false],
  ]) {
    const harness = createHarness((command, args) => {
      if (command === 'hubble') {
        return {
          status: 0,
          stdout: JSON.stringify({
            flow: {
              time: '2026-01-01T00:00:00.000Z',
              verdict,
              source: { namespace: 'p1-fixture', podName: 'agenthost-a' },
              destination: { ip: '10.0.0.8' },
              l4: { TCP: { destinationPort: 8080 } },
              isReply: false,
            },
          }),
          stderr: '',
        };
      }
      if (args.includes('config')) {
        return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
      }
      if (args.includes('ciliumnetworkpolicies.cilium.io')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'cilium.io/v2',
            kind: 'CiliumNetworkPolicyList',
            items: [policy],
          }),
          stderr: '',
        };
      }
      if (args.includes('endpointslices.discovery.k8s.io')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'discovery.k8s.io/v1',
            kind: 'EndpointSliceList',
            items: [{
              endpoints: [{ addresses: ['10.0.0.8'], conditions: { ready: true } }],
              ports: [{ protocol: 'TCP', port: 8080 }],
            }],
          }),
          stderr: '',
        };
      }
      if (args.includes('service')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'v1',
            kind: 'Service',
            metadata: { name: 'allowed', namespace: 'p1-fixture', uid: 'service-uid-a' },
            spec: { ports: [{ protocol: 'TCP', port: 80 }] },
          }),
          stderr: '',
        };
      }
      if (args.includes('pods')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'v1',
            kind: 'PodList',
            items: [{
              metadata: {
                name: 'agenthost-a',
                namespace: 'p1-fixture',
                uid: 'pod-uid-a',
                labels: {
                  'agentweaver.dev/managed-sandbox': 'true',
                  'agentweaver.dev/sandbox-owner': ownerHash,
                  'agents.x-k8s.io/claim-uid': 'claim-uid-a',
                },
              },
            }],
          }),
          stderr: '',
        };
      }
      if (args.includes('get') && args.includes('pod')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            apiVersion: 'v1',
            kind: 'Pod',
            metadata: {
              name: 'agenthost-a',
              namespace: 'p1-fixture',
              uid: 'pod-uid-a',
            },
          }),
          stderr: '',
        };
      }
      assert.ok(args.includes('exec'));
      assert.ok(args.includes('bash'));
      assert.ok(args.includes('timeout'));
      assert.ok(args.some(value => typeof value === 'string' && value.includes('/dev/tcp/$1/$2')));
      return { status: probeStatus, stdout: '', stderr: '' };
    }, { now: () => Date.parse('2026-01-01T00:00:00.000Z') });

    const result = await harness.provider.run({
      recipeId: 'probe-service',
      args: {
        expectedClusterIdentity,
        namespace: 'p1-fixture',
        sourcePodUid: 'pod-uid-a',
        serviceIdentity: 'p1-fixture/allowed',
        networkPolicyResourceId: 'environment/policy-a',
        networkPolicyUid: 'policy-uid-a',
        networkPolicyGeneration: 6,
        timeoutSeconds: 5,
      },
    });
    assert.deepEqual(result, {
      clusterIdentity: expectedClusterIdentity,
      sourcePodUid: 'pod-uid-a',
      serviceIdentity: 'p1-fixture/allowed',
      policyUid: 'policy-uid-a',
      policyGeneration: 6,
      targetServiceUid: 'service-uid-a',
      readyEndpointCount: 1,
      probedPorts: [80],
      datapathVerdict: expectedConnected ? 'allowed' : 'denied',
      connected: expectedConnected,
    });
    assert.ok(harness.calls.some(call =>
      call.command === 'hubble' &&
      call.args.includes('--from-pod') &&
      call.args.includes('--to-service')));
    assert.ok(harness.calls.every(call => call.options.check === false && !call.options.shell));
  }
});
