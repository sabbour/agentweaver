import { createRuntimeList } from '../runtime-list.mjs';

const imageNames = [
  'identity-broker', 'events-and-sessions', 'gateway', 'mcp', 'web',
  'orchestrator', 'environment', 'knowledge', 'projects-config', 'agenthost',
];

export function runtimeListFixture() {
  const images = imageNames.map(name => ({
    name,
    image: `ghcr.io/sabbour/agentweaver-${name}@sha256:${'a'.repeat(64)}`,
    ...(name === 'agenthost' ? {
      sandboxImage: `ghcr.io/sabbour/agentweaver-agenthost@sha256:${'b'.repeat(64)}`,
      compressedPullBytes: 123456,
    } : {}),
  }));
  return createRuntimeList(images, 'agentweaver-v1-p0', {
    configMapName: 'projects-config-reviewed-catalog',
    sourceCommit: 'b'.repeat(40),
  });
}

export function runtimeContractSettings(deployment) {
  const template = deployment.spec.template;
  const pod = template.spec;
  const container = pod.containers[0];
  return {
    labels: template.metadata.labels,
    annotations: template.metadata.annotations,
    serviceAccountName: pod.serviceAccountName,
    automountServiceAccountToken: pod.automountServiceAccountToken,
    securityContext: pod.securityContext,
    ports: container.ports,
    env: container.env,
    envFrom: container.envFrom,
    livenessProbe: container.livenessProbe,
    readinessProbe: container.readinessProbe,
  };
}
