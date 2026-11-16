import assert from 'node:assert/strict';
import test from 'node:test';
import {
  brokerRunBindingFingerprint,
  createRuntimeList,
  BROKER_RUN_BINDING_ANNOTATION,
  BROKER_RUN_BINDING_EGRESS_POLICY_NAME,
  BROKER_RUN_BINDING_INGRESS_POLICY_NAME,
} from '../runtime-list.mjs';

const serviceNames = [
  'identity-broker', 'events-and-sessions', 'gateway', 'mcp', 'web',
  'orchestrator', 'environment', 'knowledge', 'projects-config', 'agenthost',
];

function inputs() {
  return {
    images: serviceNames.map((name) => ({
      name,
      image: `ghcr.io/sabbour/agentweaver-${name}@sha256:${'a'.repeat(64)}`,
      ...(name === 'agenthost' ? {
        sandboxImage: `ghcr.io/sabbour/agentweaver-agenthost@sha256:${'b'.repeat(64)}`,
        compressedPullBytes: 123456,
      } : {}),
    })),
    namespace: 'agentweaver',
    providerCatalog: {
      configMapName: 'projects-config-reviewed-catalog',
      sourceCommit: 'c'.repeat(40),
    },
  };
}

const binding = {
  projectsOwnerAddress: 'https://projects-config.agentweaver.svc.cluster.local:8443/',
  projectsAudience: 'https://projects-config.agentweaver.svc.cluster.local',
  projectsServerCertificateSha256: 'd'.repeat(64),
  projectsTrustBundleSha256: 'e'.repeat(64),
};

function deployment(list, name) {
  return list.items.find((item) => item.kind === 'Deployment' && item.metadata.name === name);
}

test('runtime list emits service contracts and broker run-binding policies', () => {
  const { images, namespace, providerCatalog } = inputs();
  const list = createRuntimeList(images, namespace, providerCatalog, binding);
  const identity = deployment(list, 'identity-broker');
  const projects = deployment(list, 'projects-config');
  const environment = deployment(list, 'environment');
  const webService = list.items.find((item) => item.kind === 'Service' && item.metadata.name === 'web');
  const policies = list.items.filter((item) => item.kind === 'CiliumNetworkPolicy');

  assert.equal(list.apiVersion, 'v1');
  assert.equal(list.kind, 'List');
  assert.equal(list.items.length, 20);
  assert.equal(identity.spec.template.spec.containers[0].image,
    images.find((image) => image.name === 'identity-broker').image);
  assert.equal(identity.spec.template.metadata.annotations[BROKER_RUN_BINDING_ANNOTATION],
    brokerRunBindingFingerprint(binding));
  assert.equal(projects.spec.template.metadata.annotations[BROKER_RUN_BINDING_ANNOTATION],
    brokerRunBindingFingerprint(binding));
  assert.deepEqual(webService.spec.ports, [{
    name: 'http', port: 8080, targetPort: 'http', protocol: 'TCP',
  }]);
  assert.equal(environment.spec.template.spec.containers[0].env.find(({ name }) =>
    name === 'Environment__Sandbox__AgentSandbox__ContainerImageCompressedPullBytes').value, '123456');
  assert.deepEqual(policies.map(({ metadata }) => metadata.name), [
    BROKER_RUN_BINDING_EGRESS_POLICY_NAME,
    BROKER_RUN_BINDING_INGRESS_POLICY_NAME,
  ]);
  assert.deepEqual(policies[0].spec.endpointSelector.matchLabels,
    { 'agentweaver.io/workload': 'identity-broker' });
  assert.deepEqual(policies[0].spec.egress[0].toEndpoints[0].matchLabels, {
    'k8s:io.kubernetes.pod.namespace': namespace,
    'agentweaver.io/workload': 'projects-config',
  });
  assert.deepEqual(policies[1].spec.endpointSelector.matchLabels,
    { 'agentweaver.io/workload': 'projects-config' });
  assert.deepEqual(policies[1].spec.ingress[0].fromEndpoints[0].matchLabels, {
    'k8s:io.kubernetes.pod.namespace': namespace,
    'agentweaver.io/workload': 'identity-broker',
  });
  assert.deepEqual(policies[0].spec.egress[0].toPorts[0].ports, [{ port: '8443', protocol: 'TCP' }]);
  assert.deepEqual(policies[1].spec.ingress[0].toPorts[0].ports, [{ port: '8443', protocol: 'TCP' }]);
});

test('runtime list rejects unapproved namespaces, catalogs, missing images, and invalid binding data', () => {
  const valid = inputs();
  assert.throws(() => createRuntimeList(valid.images, 'default', valid.providerCatalog),
    /namespace must be the exact approved v1 P0 namespace/);
  assert.throws(() => createRuntimeList(valid.images, valid.namespace,
    { ...valid.providerCatalog, configMapName: 'projects-config-runtime-config' }),
  /an explicit existing Projects provider catalog ConfigMap name is required/);
  assert.throws(() => createRuntimeList(valid.images.filter(({ name }) => name !== 'knowledge'),
    valid.namespace, valid.providerCatalog),
  /the validated publication receipt has no image for knowledge/);
  assert.throws(() => createRuntimeList(valid.images, valid.namespace, valid.providerCatalog, {
    ...binding,
    projectsTrustBundleSha256: 'invalid',
  }), /projectsTrustBundleSha256 must be a lowercase SHA-256 hex digest/);
});
