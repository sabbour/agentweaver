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

function createServiceProbeHarness({
  verdict = 'FORWARDED',
  servicePorts = [{ name: 'http', protocol: 'TCP', port: 80, targetPort: 8080 }],
  endpointPorts = [{ name: 'http', protocol: 'TCP', port: 8080 }],
  flows,
  probeStatuses = [verdict === 'FORWARDED' ? 0 : 1],
  mutatePolicyAfterRead,
  mutateServiceAfterRead,
} = {}) {
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
  const service = {
    apiVersion: 'v1',
    kind: 'Service',
    metadata: { name: 'allowed', namespace: 'p1-fixture', uid: 'service-uid-a' },
    spec: { ports: servicePorts },
  };
  const observedFlows = flows ?? [{
    time: '2026-01-01T00:00:00.000Z',
    verdict,
    source: { namespace: 'p1-fixture', podName: 'agenthost-a' },
    destination: { ip: '10.0.0.8' },
    l4: { TCP: { destinationPort: endpointPorts[0]?.port } },
    isReply: false,
  }];
  let policyReads = 0;
  let serviceReads = 0;
  let probeIndex = 0;
  const harness = createHarness((command, args) => {
    if (command === 'hubble') {
      return { status: 0, stdout: JSON.stringify(observedFlows.map(flow => ({ flow }))), stderr: '' };
    }
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('ciliumnetworkpolicies.cilium.io')) {
      policyReads += 1;
      const current = policyReads > 1 && mutatePolicyAfterRead
        ? mutatePolicyAfterRead(policy)
        : policy;
      return {
        status: 0,
        stdout: JSON.stringify({ apiVersion: 'v1', kind: 'List', items: [current] }),
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
            ports: endpointPorts,
          }],
        }),
        stderr: '',
      };
    }
    if (args.includes('service')) {
      serviceReads += 1;
      const current = serviceReads > 1 && mutateServiceAfterRead
        ? mutateServiceAfterRead(service)
        : service;
      return { status: 0, stdout: JSON.stringify(current), stderr: '' };
    }
    if (args.includes('pods')) {
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'List',
          items: [{
            apiVersion: 'v1',
            kind: 'Pod',
            metadata: {
              name: 'agenthost-a',
              namespace: 'p1-fixture',
              uid: 'pod-uid-a',
              labels: {
                'agentweaver.dev/managed-sandbox': 'true',
                'agentweaver.dev/sandbox-owner': ownerHash,
                'agents.x-k8s.io/claim-uid': 'sandbox-claim-uid-a',
              },
            },
          }],
        }),
        stderr: '',
      };
    }
    if (args.includes('pod')) {
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
    const status = probeStatuses[probeIndex] ?? probeStatuses.at(-1);
    probeIndex += 1;
    return { status, stdout: '', stderr: '' };
  }, { now: () => Date.parse('2026-01-01T00:00:00.000Z') });
  return {
    harness,
    request: {
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
    },
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

test('Hubble preflight requires the configured relay to report healthy', async () => {
  let status = 0;
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    assert.equal(command, 'hubble');
    assert.deepEqual(args, ['--server', '127.0.0.1:4245', 'status']);
    return { status, stdout: '', stderr: '' };
  });
  const request = {
    recipeId: 'verify-hubble',
    args: { expectedClusterIdentity },
  };

  assert.deepEqual(await harness.provider.run(request), {
    clusterIdentity: expectedClusterIdentity,
    relayEndpoint: '127.0.0.1:4245',
    healthy: true,
  });
  status = 1;
  await assert.rejects(harness.provider.run(request), /Hubble Relay health check failed/);
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

test('Sandbox Pod reads select a replacement by its actual placement, operation, and claim UID', async () => {
  const sandboxClaimUid = 'sandbox-claim-uid-replacement';
  const workspacePvcUid = 'workspace-pvc-uid-a';
  const originalLabels = {
    'agentweaver.dev/managed-sandbox': 'true',
    'agentweaver.dev/sandbox-owner': ownerLabel,
    'agentweaver.dev/sandbox-lifecycle': '2',
    'agentweaver.dev/sandbox-generation': '3',
    'agentweaver.dev/sandbox-fence': '5',
    'agentweaver.dev/sandbox-operation': 'originaloperation',
    'agents.x-k8s.io/claim-uid': 'sandbox-claim-uid-original',
  };
  const replacementLabels = {
    ...originalLabels,
    'agentweaver.dev/sandbox-lifecycle': '12',
    'agentweaver.dev/sandbox-generation': '13',
    'agentweaver.dev/sandbox-fence': '14',
    'agentweaver.dev/sandbox-operation': 'replacementoperation',
    'agents.x-k8s.io/claim-uid': sandboxClaimUid,
  };
  const expectedLabels = replacementLabels;
  const makePod = (name, uid, labels) => ({
    apiVersion: 'v1',
    kind: 'Pod',
    metadata: {
      name,
      namespace: 'p1-fixture',
      uid,
      labels,
    },
    spec: {
      containers: [{
        name: 'agenthost',
        image: 'registry.example.test/agenthost:fixture',
        volumeMounts: [{ name: 'workspace', mountPath: '/workspace' }],
      }],
      volumes: [{ name: 'workspace', persistentVolumeClaim: { claimName: 'workspace-a' } }],
    },
  });
  const originalPod = makePod('agenthost-original', 'pod-uid-original', originalLabels);
  const replacementPod = makePod('agenthost-replacement', 'pod-uid-replacement', replacementLabels);
  const placement = {
    lifecycleGeneration: 12,
    providerFencingGeneration: 14,
    resource: { resourceId: sandboxClaimUid, generation: 13 },
  };
  const expectedOperationId = 'replacement-operation';

  for (const key of [
    'agentweaver.dev/sandbox-lifecycle',
    'agentweaver.dev/sandbox-generation',
    'agentweaver.dev/sandbox-fence',
    'agentweaver.dev/sandbox-operation',
    'agents.x-k8s.io/claim-uid',
  ]) {
    assert.notEqual(originalLabels[key], replacementLabels[key]);
  }

  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('pods')) {
      const selectorPosition = args.indexOf('--selector');
      assert.notEqual(selectorPosition, -1);
      const selector = args[selectorPosition + 1];
      const selectedLabels = Object.fromEntries(selector.split(',').map(entry => entry.split('=')));
      assert.deepEqual(selectedLabels, expectedLabels);
      const items = [originalPod, replacementPod].filter(pod =>
        Object.entries(selectedLabels).every(([key, value]) => pod.metadata.labels[key] === value));
      return {
        status: 0, stdout: JSON.stringify({ apiVersion: 'v1', kind: 'List', items }),
        stderr: '',
      };
    }
    assert.ok(args.includes('persistentvolumeclaim'));
    return {
      status: 0,
      stdout: JSON.stringify({
        apiVersion: 'v1',
        kind: 'PersistentVolumeClaim',
        metadata: { name: 'workspace-a', namespace: 'p1-fixture', uid: workspacePvcUid },
      }),
      stderr: '',
    };
  });

  const result = await harness.provider.run({
    recipeId: 'observe-sandbox',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      owner: mountOwner,
      workspaceClaimUid: workspacePvcUid,
      sandboxClaimUid,
      placement,
      expectedOperationId,
      expectedPodUid: 'pod-uid-replacement',
      expectedLabels,
    },
  });

  assert.equal(result.pod.uid, 'pod-uid-replacement');
  assert.equal(result.pod.labels['agents.x-k8s.io/claim-uid'], sandboxClaimUid);
  assert.deepEqual(result.pod.mounts, [{
    name: 'workspace',
    containerName: 'agenthost',
    pvcName: 'workspace-a',
    mountPath: '/workspace',
    pvcUid: workspacePvcUid,
  }]);
});

test('owned-resource absence readback still reports a matching dependent Pod', async () => {
  const sandboxClaimUid = 'sandbox-claim-uid-a';
  const expectedPodUid = 'previously-observed-pod-uid';
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('sandboxclaims.extensions.agents.x-k8s.io')) {
      assert.equal(args[args.indexOf('--namespace') + 1], 'p1-fixture');
      return {
        status: 0,
        stdout: JSON.stringify({ apiVersion: 'v1', kind: 'List', items: [] }),
        stderr: '',
      };
    }
    assert.ok(args.includes('pods'));
    assert.equal(args[args.indexOf('--namespace') + 1], 'p1-fixture');
    assert.equal(args.includes('--selector'), false);
    return {
      status: 0,
      stdout: JSON.stringify({
        apiVersion: 'v1',
        kind: 'List',
        items: [{
          apiVersion: 'v1',
          kind: 'Pod',
          metadata: {
            name: 'orphan-agenthost',
            namespace: 'p1-fixture',
            uid: 'orphan-pod-uid',
            labels: {
              'agentweaver.dev/managed-sandbox': 'true',
              'agentweaver.dev/sandbox-owner': ownerLabel,
              'agents.x-k8s.io/claim-uid': sandboxClaimUid,
            },
          },
        }],
      }),
      stderr: '',
    };
  });

  const result = await harness.provider.run({
    recipeId: 'observe-owned-resource',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      owner: mountOwner,
      resourceId: sandboxClaimUid,
      expectedUid: sandboxClaimUid,
      expectedPodUids: [expectedPodUid],
    },
  });

  assert.deepEqual(result, {
    clusterIdentity: expectedClusterIdentity,
    exists: false,
    dependentPods: [{ name: 'orphan-agenthost', uid: 'orphan-pod-uid' }],
  });
});

test('owned-resource readback rejects an exact claim UID with changed owner labels', async () => {
  const sandboxClaimUid = 'sandbox-claim-uid-owner-mismatch';
  let podListRead = false;
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('sandboxclaims.extensions.agents.x-k8s.io')) {
      assert.equal(args[args.indexOf('--namespace') + 1], 'p1-fixture');
      assert.equal(args.includes('--selector'), false);
      return {
        status: 0,
        stdout: JSON.stringify({
          apiVersion: 'v1',
          kind: 'List',
          items: [{
            apiVersion: 'extensions.agents.x-k8s.io/v1beta1',
            kind: 'SandboxClaim',
            metadata: {
              name: 'claim-a',
              namespace: 'p1-fixture',
              uid: sandboxClaimUid,
              labels: {
                'agentweaver.dev/managed-sandbox': 'true',
                'agentweaver.dev/sandbox-owner': 'changed-owner',
              },
            },
          }],
        }),
        stderr: '',
      };
    }
    podListRead = true;
    throw new Error('Pod read must not follow an owner-mismatched claim.');
  });

  await assert.rejects(harness.provider.run({
    recipeId: 'observe-owned-resource',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      owner: mountOwner,
      resourceId: sandboxClaimUid,
      expectedUid: sandboxClaimUid,
      expectedPodUids: ['previously-observed-pod-uid'],
    },
  }), /exact SandboxClaim UID is present but its owner labels do not match/);
  assert.equal(podListRead, false);
});

test('owned-resource readback rejects a known Pod UID with a changed claim label', async () => {
  const sandboxClaimUid = 'sandbox-claim-uid-pod-mismatch';
  const expectedPodUid = 'previously-observed-pod-uid';
  const harness = createHarness((command, args) => {
    if (args.includes('config')) {
      return { status: 0, stdout: JSON.stringify(configResponse()), stderr: '' };
    }
    if (args.includes('sandboxclaims.extensions.agents.x-k8s.io')) {
      assert.equal(args[args.indexOf('--namespace') + 1], 'p1-fixture');
      return {
        status: 0,
        stdout: JSON.stringify({ apiVersion: 'v1', kind: 'List', items: [] }),
        stderr: '',
      };
    }
    assert.ok(args.includes('pods'));
    assert.equal(args[args.indexOf('--namespace') + 1], 'p1-fixture');
    assert.equal(args.includes('--selector'), false);
    return {
      status: 0,
      stdout: JSON.stringify({
        apiVersion: 'v1',
        kind: 'List',
        items: [{
          apiVersion: 'v1',
          kind: 'Pod',
          metadata: {
            name: 'previous-agenthost',
            namespace: 'p1-fixture',
            uid: expectedPodUid,
            labels: {
              'agentweaver.dev/managed-sandbox': 'true',
              'agentweaver.dev/sandbox-owner': ownerLabel,
              'agents.x-k8s.io/claim-uid': 'different-claim-uid',
            },
          },
        }],
      }),
      stderr: '',
    };
  });

  await assert.rejects(harness.provider.run({
    recipeId: 'observe-owned-resource',
    args: {
      expectedClusterIdentity,
      namespace: 'p1-fixture',
      owner: mountOwner,
      resourceId: sandboxClaimUid,
      expectedUid: sandboxClaimUid,
      expectedPodUids: [expectedPodUid],
    },
  }), /known Sandbox Pod UID has a mismatched SandboxClaim UID label/);
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
  for (const [verdict, probeStatus, expectedConnected] of [
    ['FORWARDED', 0, true],
    ['DROPPED', 1, false],
  ]) {
    const fixture = createServiceProbeHarness({
      verdict,
      probeStatuses: [probeStatus],
    });
    const result = await fixture.harness.provider.run(fixture.request);
    assert.deepEqual(result, {
      clusterIdentity: expectedClusterIdentity,
      sourcePodUid: 'pod-uid-a',
      serviceIdentity: 'p1-fixture/allowed',
      policyUid: 'policy-uid-a',
      policyGeneration: 6,
      targetServiceUid: 'service-uid-a',
      readyEndpointCount: 1,
      probedPorts: [80],
      portVerdicts: [{
        servicePort: 80,
        targetPort: 8080,
        verdict,
        connected: expectedConnected,
      }],
      datapathVerdict: expectedConnected ? 'allowed' : 'denied',
      connected: expectedConnected,
    });
    assert.ok(fixture.harness.calls.some(call =>
      call.command === 'hubble' &&
      call.args.includes('--from-pod') &&
      call.args.includes('--to-service')));
    assert.ok(fixture.harness.calls.every(call => call.options.check === false && !call.options.shell));
  }
});

test('every probed Service port requires its own fresh matching EndpointSlice flow', async () => {
  const fixture = createServiceProbeHarness({
    servicePorts: [
      { name: 'http', protocol: 'TCP', port: 80, targetPort: 8080 },
      { name: 'https', protocol: 'TCP', port: 443, targetPort: 8443 },
    ],
    endpointPorts: [
      { name: 'http', protocol: 'TCP', port: 8080 },
      { name: 'https', protocol: 'TCP', port: 8443 },
    ],
    flows: [{
      time: '2026-01-01T00:00:00.000Z',
      verdict: 'DROPPED',
      source: { namespace: 'p1-fixture', podName: 'agenthost-a' },
      destination: { ip: '10.0.0.8' },
      l4: { TCP: { destinationPort: 8080 } },
      isReply: false,
    }],
    probeStatuses: [1, 1],
  });

  await assert.rejects(
    fixture.harness.provider.run(fixture.request),
    /Each probed Service port requires a fresh matching Cilium datapath flow/,
  );
});

test('a changed Cilium policy blocks the datapath receipt', async () => {
  const fixture = createServiceProbeHarness({
    mutatePolicyAfterRead: policy => ({
      ...policy,
      metadata: {
        ...policy.metadata,
        annotations: {
          ...policy.metadata.annotations,
          'agentweaver.dev/egress-generation': '7',
        },
      },
    }),
  });

  await assert.rejects(
    fixture.harness.provider.run(fixture.request),
    /Cilium policy changed during the datapath probe/,
  );
});

test('a replaced target Service blocks the datapath receipt', async () => {
  const fixture = createServiceProbeHarness({
    mutateServiceAfterRead: service => ({
      ...service,
      metadata: { ...service.metadata, uid: 'service-uid-replaced' },
    }),
  });

  await assert.rejects(
    fixture.harness.provider.run(fixture.request),
    /target Service changed during the datapath probe/,
  );
});
