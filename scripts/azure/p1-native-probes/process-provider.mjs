import { createHash } from 'node:crypto';
import { run } from '../lib/exec.mjs';

const SAFE_RECIPES = new Set([
  'observe-cluster',
  'verify-hubble',
  'observe-storage',
  'observe-sandbox',
  'observe-network-policy',
  'probe-service',
  'observe-owned-resource',
  'mount-file',
]);
const LABEL_KEY = /^[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?\/?[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?$/;
const LABEL_VALUE = /^(?:[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?)?$/;
const DNS_LABEL = /^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$/;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SHA256 = /^[a-f0-9]{64}$/i;
const CILIUM_GENERATION = 'agentweaver.dev/egress-generation';
const CILIUM_HASH = 'agentweaver.dev/egress-intent-sha256';
const CILIUM_REVOKED = 'agentweaver.dev/egress-revoked';
const CLAIM_UID = 'agents.x-k8s.io/claim-uid';
const MANAGED = 'agentweaver.dev/managed-sandbox';
const OWNER = 'agentweaver.dev/sandbox-owner';
const PROJECT_ANNOTATION = 'agentweaver.dev/project-id';
const VOLUME_ANNOTATION = 'agentweaver.dev/volume-id';
const GENERATION_ANNOTATION = 'agentweaver.dev/generation';
const ENVIRONMENT_ANNOTATION = 'agentweaver.dev/environment-id';
const VOLUME_OWNER_KINDS = new Set(['Run', 'Agent', 'Team']);
const HUBBLE_SERVER = '127.0.0.1:4245';

const MOUNT_SCRIPTS = Object.freeze({
  stat: [
    'set -eu',
    'path=$1',
    'if [ -L "$path" ]; then exit 44; fi',
    'if [ -f "$path" ]; then',
    '  size=$(wc -c < "$path")',
    '  [ "$size" -le 256 ] || exit 45',
    '  printf "P1:EXISTS\\n"',
    '  cat -- "$path"',
    'else',
    '  printf "P1:MISSING\\n"',
    'fi',
  ].join('\n'),
  read: [
    'set -eu',
    'path=$1',
    'if [ -L "$path" ]; then exit 44; fi',
    'if [ -f "$path" ]; then',
    '  size=$(wc -c < "$path")',
    '  [ "$size" -le 256 ] || exit 45',
    '  printf "P1:EXISTS\\n"',
    '  cat -- "$path"',
    'else',
    '  printf "P1:MISSING\\n"',
    'fi',
  ].join('\n'),
  write: [
    'set -eu',
    'umask 077',
    'path=$1',
    'content=$2',
    'if [ -e "$path" ] || [ -L "$path" ]; then exit 46; fi',
    'set -C',
    'printf "%s" "$content" > "$path"',
    'printf "P1:WRITTEN\\n"',
  ].join('\n'),
  unlink: [
    'set -eu',
    'path=$1',
    'expected=$2',
    'if [ -L "$path" ]; then exit 44; fi',
    'if [ -f "$path" ]; then',
    '  size=$(wc -c < "$path")',
    '  [ "$size" -le 256 ] || exit 45',
    'fi',
    'if [ -f "$path" ] && [ "$(cat -- "$path")" = "$expected" ]; then',
    '  rm -- "$path"',
    '  printf "P1:REMOVED\\n"',
    'else',
    '  printf "P1:NOT_REMOVED\\n"',
    'fi',
  ].join('\n'),
});
const TCP_CONNECT_SCRIPT = 'exec 3<>"/dev/tcp/$1/$2"';

function isRecord(value) {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function requireString(value, name, pattern = null) {
  if (typeof value !== 'string' || value.length === 0 || pattern && !pattern.test(value)) {
    throw new Error(`Invalid ${name} for the fixed native probe.`);
  }
  return value;
}

function requirePositive(value, name) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`Invalid ${name} for the fixed native probe.`);
  }
  return value;
}

function parseJson(text, description) {
  if (typeof text !== 'string' || text.length === 0 || text.length > 1024 * 1024) {
    throw new Error(`${description} JSON output is missing or too large.`);
  }
  try {
    const value = JSON.parse(text);
    if (!isRecord(value)) throw new Error('not an object');
    return value;
  } catch {
    throw new Error(`${description} returned invalid JSON.`);
  }
}

function ownerFingerprint(owner) {
  const values = [owner?.tenantId, owner?.projectId, owner?.runId, owner?.environmentId];
  if (values.some(value => typeof value !== 'string' || value.length === 0)) {
    throw new Error('An exact Environment owner is required for the fixed native probe.');
  }
  return createHash('sha256').update(values.join('\0')).digest('hex').slice(0, 40);
}

function parseNamespaceName(value) {
  const match = /^([a-z0-9](?:[a-z0-9-]*[a-z0-9])?)\/([a-z0-9](?:[a-z0-9-]*[a-z0-9])?)$/.exec(value ?? '');
  if (!match) throw new Error('Invalid namespaced service identity.');
  return { namespace: match[1], name: match[2] };
}

function labelSelector(labels) {
  if (!isRecord(labels) || Object.keys(labels).length === 0) {
    throw new Error('Exact owner labels are required for the fixed native probe.');
  }
  return Object.entries(labels)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, value]) => {
      if (!LABEL_KEY.test(key) || typeof value !== 'string' || !LABEL_VALUE.test(value)) {
        throw new Error('Invalid owner label for the fixed native probe.');
      }
      return `${key}=${value}`;
    })
    .join(',');
}

function verifyKubernetesServer(server, expectedUid) {
  let parsed;
  try {
    parsed = new URL(server);
  } catch {
    throw new Error('The selected Kubernetes context has no valid API server URL.');
  }
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password ||
      parsed.search || parsed.hash || parsed.pathname !== '/' ||
      parsed.port && parsed.port !== '443') {
    throw new Error('The selected Kubernetes context does not use the pinned HTTPS API server.');
  }
  const identity = createHash('sha256').update(parsed.origin).digest('hex');
  if (identity !== expectedUid) {
    throw new Error('The selected Kubernetes API server does not match the pinned cluster identity.');
  }
}

function objectIdentity(resource, namespace, name, uid) {
  const metadata = resource?.metadata;
  return metadata?.namespace === namespace &&
    metadata?.name === name &&
    typeof metadata?.uid === 'string' &&
    metadata.uid === uid;
}

function mountReceipt(pod, namespace, expectedClaimUid, pvc) {
  const containers = Array.isArray(pod?.spec?.containers) ? pod.spec.containers : [];
  const volumes = Array.isArray(pod?.spec?.volumes) ? pod.spec.volumes : [];
  const container = containers.find(item => item?.name === 'agenthost');
  if (!container || !Array.isArray(container.volumeMounts)) return [];
  const result = [];
  for (const mount of container.volumeMounts) {
    const volume = volumes.find(item => item?.name === mount.name);
    const claimName = volume?.persistentVolumeClaim?.claimName;
    if (!claimName) continue;
    const claim = pvc(claimName);
    if (claim.metadata?.uid !== expectedClaimUid) continue;
    result.push({
      name: mount.name,
      containerName: container.name,
      pvcName: claimName,
      mountPath: mount.mountPath,
      pvcUid: claim.metadata.uid,
    });
  }
  return result;
}

function selectReadyEndpoints(slices) {
  const addresses = new Set();
  const ports = new Set();
  for (const slice of Array.isArray(slices?.items) ? slices.items : []) {
    for (const endpoint of Array.isArray(slice?.endpoints) ? slice.endpoints : []) {
      if (endpoint?.conditions?.ready !== true || !Array.isArray(endpoint.addresses)) continue;
      endpoint.addresses.forEach(address => {
        if (typeof address === 'string' && address.length > 0) addresses.add(address);
      });
    }
    for (const port of Array.isArray(slice?.ports) ? slice.ports : []) {
      if ((port?.protocol ?? 'TCP') === 'TCP' && Number.isSafeInteger(port.port) && port.port > 0) ports.add(port.port);
    }
  }
  return { addresses, ports };
}

function parseHubbleFlows(text) {
  if (typeof text !== 'string' || text.length === 0 || text.length > 1024 * 1024) {
    throw new Error('Hubble returned no bounded flow evidence.');
  }
  try {
    const parsed = JSON.parse(text);
    if (Array.isArray(parsed)) return parsed.filter(isRecord);
    if (isRecord(parsed)) return [parsed];
  } catch {
    // Hubble can emit one JSON flow per line.
  }
  const flows = [];
  for (const line of text.split(/\r?\n/)) {
    if (!line.trim()) continue;
    try {
      const value = JSON.parse(line);
      if (isRecord(value)) flows.push(value);
    } catch {
      throw new Error('Hubble returned malformed flow evidence.');
    }
  }
  return flows;
}

function flowFields(envelope) {
  const flow = envelope.flow ?? envelope;
  const source = flow.source ?? {};
  const destination = flow.destination ?? {};
  const ip = flow.IP ?? flow.ip ?? {};
  const l4 = flow.l4 ?? {};
  const tcp = l4.TCP ?? l4.tcp ?? {};
  return {
    sourceNamespace: source.namespace,
    sourcePodName: source.podName ?? source.pod_name,
    destinationAddress: destination.ip ?? destination.IP ?? ip.destination,
    destinationPort: tcp.destinationPort ?? tcp.destination_port ?? destination.port,
    verdict: flow.verdict,
    isReply: flow.isReply ?? flow.is_reply ?? false,
    time: flow.time,
  };
}

function mountMarker(value) {
  return typeof value === 'string' &&
    /^agentweaver-p1:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

export function createP1NativeProcessProvider({
  kubeContext,
  clusterIdentity,
  execute = run,
  now = Date.now,
} = {}) {
  requireString(kubeContext, 'Kubernetes context');
  if (!isRecord(clusterIdentity) || !SHA256.test(clusterIdentity.uid ?? '')) {
    throw new Error('A SHA-256 Kubernetes cluster identity is required.');
  }
  if (typeof execute !== 'function' || typeof now !== 'function') {
    throw new Error('The fixed native probe requires process and clock functions.');
  }
  const expectedClusterIdentity = Object.freeze({ uid: clusterIdentity.uid });

  const invoke = (command, args, timeout = 15_000) => {
    const result = execute(command, args, { check: false, timeout });
    if (!isRecord(result) || !Number.isInteger(result.status) ||
        typeof result.stdout !== 'string' || typeof result.stderr !== 'string') {
      throw new Error('The fixed native process returned an invalid result.');
    }
    return result;
  };

  const kubectlArgs = (namespace, args) => [
    '--context', kubeContext,
    '--request-timeout=15s',
    ...(namespace ? ['--namespace', namespace] : []),
    ...args,
  ];

  const kubectlJson = (namespace, args, description) => {
    const result = invoke('kubectl', kubectlArgs(namespace, [...args, '-o', 'json']));
    if (result.status !== 0) throw new Error(`${description} observation failed.`);
    return parseJson(result.stdout, description);
  };

  const verifyTarget = () => {
    const config = kubectlJson(null, [
      'config', 'view', '--minify',
    ], 'Kubernetes context');
    const contexts = config.contexts;
    const clusters = config.clusters;
    if (!Array.isArray(contexts) || contexts.length !== 1 ||
        contexts[0]?.name !== kubeContext ||
        !Array.isArray(clusters) || clusters.length !== 1 ||
        !isRecord(clusters[0]?.cluster) ||
        typeof clusters[0].cluster.server !== 'string') {
      throw new Error('The selected Kubernetes context is ambiguous.');
    }
    verifyKubernetesServer(clusters[0].cluster.server, expectedClusterIdentity.uid);
  };

  const readPod = (namespace, name) => {
    const pod = kubectlJson(namespace, ['get', 'pod', name], 'Pod');
    if (pod.apiVersion !== 'v1' || pod.kind !== 'Pod' ||
        pod.metadata?.namespace !== namespace || pod.metadata?.name !== name ||
        typeof pod.metadata?.uid !== 'string') {
      throw new Error('The current Pod identity is invalid.');
    }
    return pod;
  };

  const readMountFile = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const podName = requireString(args.podName, 'Pod name', DNS_LABEL);
    const podUid = requireString(args.podUid, 'Pod UID');
    const ownerId = requireString(args.ownerId, 'owner ID');
    const owner = args.owner;
    const mountPath = requireString(args.mountPath, 'mount path');
    const path = requireString(args.path, 'marker path');
    const operation = requireString(args.operation, 'mount operation');
    const normalizedMount = mountPath.replace(/\/+$/, '');
    const segments = path.slice(1).split('/');
    if (!mountPath.startsWith('/') || mountPath.includes('\\') ||
        segments.some(segment => !segment || segment === '.' || segment === '..') ||
        path.slice(0, path.lastIndexOf('/')) !== normalizedMount ||
        !/^\.agentweaver-p1-[0-9a-f-]{36}-[a-f0-9]{10}$/i.test(segments.at(-1)) ||
        !Object.hasOwn(MOUNT_SCRIPTS, operation)) {
      throw new Error('The mount operation is outside its exact owned marker path.');
    }
    if ((operation === 'write' && !mountMarker(args.content)) ||
        (operation === 'unlink' &&
          (!mountMarker(args.expectedContent) || args.expectedPodUid !== podUid))) {
      throw new Error('The marker content or Pod UID is not valid for the fixed mount operation.');
    }
    const pod = readPod(namespace, podName);
    if (pod.metadata.uid !== podUid ||
        owner?.ownerId !== ownerId ||
        pod.metadata.labels?.[OWNER] !== ownerFingerprint(owner)) {
      throw new Error('The current Pod UID or owner does not match the mount request.');
    }
    const mount = mountReceipt(pod, namespace, args.workspaceClaimUid,
      name => kubectlJson(namespace, ['get', 'persistentvolumeclaim', name], 'PVC'));
    if (!mount.some(item => item.mountPath === mountPath && item.containerName === 'agenthost')) {
      throw new Error('The exact writable AgentHost mount was not observed on the current Pod.');
    }
    const shellArgs = [
      'exec', podName, '--container', 'agenthost', '--',
      'sh', '-c', MOUNT_SCRIPTS[operation], 'sh', path,
      ...(operation === 'write' ? [args.content] :
        operation === 'unlink' ? [args.expectedContent] : []),
    ];
    const result = invoke('kubectl', kubectlArgs(namespace, shellArgs), 10_000);
    if (result.status !== 0) throw new Error('The exact mounted marker operation failed.');
    const [markerStatus, ...contentLines] = result.stdout.split('\n');
    const content = contentLines.join('\n').replace(/\r$/, '');
    const identity = { clusterIdentity: expectedClusterIdentity, podUid, ownerId };
    if (operation === 'write') {
      if (markerStatus !== 'P1:WRITTEN') throw new Error('The marker write did not return its completion receipt.');
      return {
        ...identity,
        written: true,
        sha256: createHash('sha256').update(args.content).digest('hex'),
      };
    }
    if (operation === 'unlink') {
      if (!['P1:REMOVED', 'P1:NOT_REMOVED'].includes(markerStatus)) {
        throw new Error('The marker removal did not return its completion receipt.');
      }
      return { ...identity, removed: markerStatus === 'P1:REMOVED' };
    }
    if (!['P1:EXISTS', 'P1:MISSING'].includes(markerStatus)) {
      throw new Error('The marker read did not return its completion receipt.');
    }
    return {
      ...identity,
      exists: markerStatus === 'P1:EXISTS',
      ...(markerStatus === 'P1:EXISTS' ? { content } : {}),
    };
  };

  const observeStorage = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const owner = args.owner;
    const volumeOwner = args.volumeOwner;
    if (!isRecord(volumeOwner) || !VOLUME_OWNER_KINDS.has(volumeOwner.kind) ||
        typeof volumeOwner.id !== 'string' || volumeOwner.id.length === 0) {
      throw new Error('The exact workspace-volume owner is required.');
    }
    const volumeId = requireString(args.volumeId, 'volume ID');
    const generation = requirePositive(args.resourceGeneration, 'volume generation');
    const pvcName = requireString(args.pvc?.name, 'PVC name', DNS_LABEL);
    const pvName = requireString(args.pv?.name, 'PV name', DNS_LABEL);
    const pvc = kubectlJson(namespace, ['get', 'persistentvolumeclaim', pvcName], 'PVC');
    const pv = kubectlJson(null, ['get', 'persistentvolume', pvName], 'PV');
    const annotations = pvc.metadata?.annotations ?? {};
    const claim = pv.spec?.claimRef;
    if (pvc.apiVersion !== 'v1' || pvc.kind !== 'PersistentVolumeClaim' ||
        pvc.metadata?.namespace !== namespace || pvc.metadata?.name !== pvcName ||
        pvc.metadata?.uid !== args.pvc.uid ||
        pvc.status?.phase !== 'Bound' ||
        pvc.spec?.volumeName !== pvName ||
        pv.apiVersion !== 'v1' || pv.kind !== 'PersistentVolume' ||
        pv.metadata?.name !== pvName || pv.metadata?.uid !== args.pv.uid ||
        pv.status?.phase !== 'Bound' ||
        claim?.namespace !== namespace || claim?.name !== pvcName ||
        claim?.uid !== pvc.metadata.uid ||
        args.claimUid !== pvc.metadata.uid ||
        annotations[PROJECT_ANNOTATION] !== owner.projectId ||
        annotations[VOLUME_ANNOTATION] !== volumeId ||
        annotations[GENERATION_ANNOTATION] !== String(generation) ||
        annotations[ENVIRONMENT_ANNOTATION] !== owner.environmentId ||
        annotations['agentweaver.dev/owner-kind'] !== volumeOwner.kind ||
        annotations['agentweaver.dev/owner-id'] !== volumeOwner.id) {
      throw new Error('The current PVC/PV identities or owner annotations do not match the storage receipt.');
    }
    return {
      clusterIdentity: expectedClusterIdentity,
      pvc: { name: pvcName, uid: pvc.metadata.uid },
      pv: { name: pvName, uid: pv.metadata.uid },
      claimUid: pvc.metadata.uid,
    };
  };

  const verifyHubble = () => {
    const result = invoke('hubble', ['--server', HUBBLE_SERVER, 'status'], 5_000);
    if (result.status !== 0) {
      throw new Error('The configured Hubble Relay health check failed.');
    }
    return { relayEndpoint: HUBBLE_SERVER, healthy: true };
  };

  const findPod = (namespace, labels, expectedUid) => {
    const selector = labelSelector(labels);
    const pods = kubectlJson(namespace, [
      'get', 'pods', '--selector', selector,
    ], 'Pod list');
    if (pods.apiVersion !== 'v1' || pods.kind !== 'List' || !Array.isArray(pods.items)) {
      throw new Error('The current Pod list is invalid.');
    }
    const matches = pods.items.filter(pod =>
      pod.metadata?.namespace === namespace &&
      Object.entries(labels).every(([key, value]) => pod.metadata?.labels?.[key] === value) &&
      (!expectedUid || pod.metadata?.uid === expectedUid));
    if (matches.length !== 1) throw new Error('The exact current owner Pod is missing or ambiguous.');
    return matches[0];
  };

  const observeSandbox = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const owner = args.owner;
    const expectedLabels = args.expectedLabels;
    if (expectedLabels?.[MANAGED] !== 'true' ||
        expectedLabels?.[OWNER] !== ownerFingerprint(owner) ||
        expectedLabels?.[CLAIM_UID] !== args.workspaceClaimUid) {
      throw new Error('The exact owner, claim UID, and managed Sandbox labels are required.');
    }
    const pod = findPod(namespace, {
      ...expectedLabels,
      [CLAIM_UID]: args.workspaceClaimUid,
    }, args.expectedPodUid);
    const mounts = mountReceipt(pod, namespace, args.workspaceClaimUid,
      name => kubectlJson(namespace, ['get', 'persistentvolumeclaim', name], 'PVC'));
    const containers = (pod.spec?.containers ?? []).map(container => ({
      name: container.name,
      image: container.image,
    }));
    const result = {
      clusterIdentity: expectedClusterIdentity,
      pod: {
        name: pod.metadata.name,
        uid: pod.metadata.uid,
        labels: pod.metadata.labels,
        containers,
        mounts,
      },
    };
    return result;
  };

  const observeOwnedResource = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const expectedUid = requireString(args.expectedUid, 'resource UID');
    const fingerprint = ownerFingerprint(args.owner);
    const items = kubectlJson(namespace, [
      'get', 'sandboxclaims.extensions.agents.x-k8s.io',
      '--selector', `${MANAGED}=true,${OWNER}=${fingerprint}`,
    ], 'Sandbox claim list');
    if (items.apiVersion !== 'v1' || items.kind !== 'List' || !Array.isArray(items.items)) {
      throw new Error('The current Sandbox claim list is invalid.');
    }
    const matches = items.items.filter(item =>
      item.metadata?.namespace === namespace &&
      item.metadata?.uid === expectedUid &&
      item.metadata?.labels?.[MANAGED] === 'true' &&
      item.metadata?.labels?.[OWNER] === fingerprint);
    if (matches.length > 1) throw new Error('The invocation-owned Sandbox claim is ambiguous.');
    return {
      clusterIdentity: expectedClusterIdentity,
      exists: matches.length === 1,
      ...(matches.length === 1
        ? { uid: matches[0].metadata.uid, ownerId: args.owner.ownerId }
        : {}),
    };
  };

  const observeNetworkPolicy = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    requireString(args.resourceId, 'Cilium policy resource ID');
    const policies = kubectlJson(namespace, [
      'get', 'ciliumnetworkpolicies.cilium.io',
    ], 'Cilium policy list');
    if (policies.apiVersion !== 'cilium.io/v2' || policies.kind !== 'CiliumNetworkPolicyList' ||
        !Array.isArray(policies.items)) {
      throw new Error('The current Cilium policy list is invalid.');
    }
    const matches = policies.items.filter(item =>
      item.metadata?.namespace === namespace &&
      item.metadata?.uid === args.expectedUid);
    if (matches.length !== 1) throw new Error('The exact current Cilium policy UID is missing or ambiguous.');
    const policy = matches[0];
    const annotations = policy.metadata?.annotations ?? {};
    const generation = Number(annotations[CILIUM_GENERATION]);
    const selectorLabels = policy.spec?.endpointSelector?.matchLabels;
    if (policy.apiVersion !== 'cilium.io/v2' || policy.kind !== 'CiliumNetworkPolicy' ||
        policy.metadata?.namespace !== namespace ||
        policy.metadata?.uid !== args.expectedUid ||
        !Number.isSafeInteger(generation) || generation <= 0 ||
        !SHA256.test(annotations[CILIUM_HASH] ?? '') ||
        !['true', 'false'].includes(annotations[CILIUM_REVOKED]) ||
        !isRecord(selectorLabels)) {
      throw new Error('The exact current Cilium policy receipt is incomplete.');
    }
    return {
      clusterIdentity: expectedClusterIdentity,
      uid: policy.metadata.uid,
      appliedIntentGeneration: generation,
      intentHash: annotations[CILIUM_HASH],
      revoked: annotations[CILIUM_REVOKED] === 'true',
      selectorLabels,
    };
  };

  const probeService = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const sourcePodUid = requireString(args.sourcePodUid, 'source Pod UID');
    const { namespace: serviceNamespace, name: serviceName } = parseNamespaceName(args.serviceIdentity);
    const policyUid = requireString(args.networkPolicyUid, 'policy UID');
    const policyGeneration = requirePositive(args.networkPolicyGeneration, 'policy generation');
    const networkPolicyResourceId = requireString(args.networkPolicyResourceId, 'Cilium policy resource ID');
    if (args.timeoutSeconds !== 5) {
      throw new Error('The service probe must use the exact namespace and five-second bound.');
    }
    const pods = kubectlJson(namespace, ['get', 'pods'], 'Pod list');
    const sourcePods = (pods.items ?? []).filter(pod =>
      pod.metadata?.namespace === namespace && pod.metadata?.uid === sourcePodUid &&
      pod.metadata?.labels?.[MANAGED] === 'true' &&
      typeof pod.metadata?.labels?.[CLAIM_UID] === 'string');
    if (sourcePods.length !== 1) throw new Error('The exact source Pod UID is missing or ambiguous.');
    const sourcePod = sourcePods[0];
    const policy = observeNetworkPolicy({
      namespace,
      resourceId: networkPolicyResourceId,
      expectedUid: policyUid,
    });
    if (policy.appliedIntentGeneration !== policyGeneration || policy.revoked ||
        Object.entries(policy.selectorLabels).some(([key, value]) =>
          sourcePod.metadata?.labels?.[key] !== value)) {
      throw new Error('The current Cilium policy generation is not active.');
    }
    const service = kubectlJson(serviceNamespace, ['get', 'service', serviceName], 'Service');
    if (service.apiVersion !== 'v1' || service.kind !== 'Service' ||
        service.metadata?.namespace !== serviceNamespace ||
        service.metadata?.name !== serviceName ||
        typeof service.metadata?.uid !== 'string') {
      throw new Error('The exact target Service is missing.');
    }
    const slices = kubectlJson(serviceNamespace, [
      'get', 'endpointslices.discovery.k8s.io',
      '--selector', `kubernetes.io/service-name=${serviceName}`,
    ], 'EndpointSlice list');
    const endpoints = selectReadyEndpoints(slices);
    const servicePorts = (service.spec?.ports ?? [])
      .filter(port => (port?.protocol ?? 'TCP') === 'TCP' &&
        Number.isSafeInteger(port.port) && port.port > 0)
      .map(port => port.port);
    if (endpoints.addresses.size === 0 || endpoints.ports.size === 0 || servicePorts.length === 0) {
      throw new Error('The target Service has no ready TCP endpoints.');
    }
    const startedAt = now();
    const host = `${serviceName}.${serviceNamespace}.svc`;
    const statuses = [];
    for (const port of servicePorts) {
      const probe = invoke('kubectl', kubectlArgs(namespace, [
        'exec', sourcePod.metadata.name, '--container', 'agenthost', '--',
        'timeout', '3', 'bash', '-c', TCP_CONNECT_SCRIPT, 'bash', host, String(port),
      ]), 5_000);
      statuses.push(probe.status === 0);
    }
    const hubble = invoke('hubble', [
      'observe',
      '--server', HUBBLE_SERVER,
      '--last', '100',
      '--from-pod', `${namespace}/${sourcePod.metadata.name}`,
      '--to-service', `${serviceNamespace}/${serviceName}`,
      '--protocol', 'TCP',
      '--output', 'json',
    ], 10_000);
    if (hubble.status !== 0) throw new Error('Hubble could not return current Cilium datapath evidence.');
    const nowMs = now();
    const targetPorts = endpoints.ports;
    const flows = parseHubbleFlows(hubble.stdout)
      .map(flowFields)
      .filter(flow => flow.sourceNamespace === namespace &&
        flow.sourcePodName === sourcePod.metadata.name &&
        endpoints.addresses.has(flow.destinationAddress) &&
        targetPorts.has(flow.destinationPort) &&
        flow.isReply !== true &&
        Number.isFinite(Date.parse(flow.time)) &&
        Date.parse(flow.time) >= startedAt &&
        Date.parse(flow.time) <= nowMs &&
        ['FORWARDED', 'DROPPED'].includes(flow.verdict));
    const verdicts = new Set(flows.map(flow => flow.verdict));
    if (flows.length === 0 || verdicts.size !== 1 ||
        new Set(statuses).size !== 1 || statuses[0] !== (verdicts.has('FORWARDED'))) {
      throw new Error('The current source-to-Service probes lack one consistent Cilium verdict.');
    }
    const currentPod = readPod(namespace, sourcePod.metadata.name);
    if (currentPod.metadata.uid !== sourcePodUid) {
      throw new Error('The source Pod changed during the Cilium datapath probe.');
    }
    const verdict = verdicts.has('FORWARDED') ? 'allowed' : 'denied';
    return {
      clusterIdentity: expectedClusterIdentity,
      sourcePodUid,
      serviceIdentity: `${serviceNamespace}/${serviceName}`,
      policyUid,
      policyGeneration,
      targetServiceUid: service.metadata.uid,
      readyEndpointCount: endpoints.addresses.size,
      probedPorts: servicePorts,
      datapathVerdict: verdict,
      connected: verdict === 'allowed',
    };
  };

  return Object.freeze({
    async run(request) {
      if (!isRecord(request) || !SAFE_RECIPES.has(request.recipeId) || !isRecord(request.args)) {
        throw new Error('Only source-owned P1 native recipes are supported.');
      }
      if (!isRecord(request.args.expectedClusterIdentity) ||
          request.args.expectedClusterIdentity.uid !== expectedClusterIdentity.uid) {
        throw new Error('The native recipe target does not match the configured cluster identity.');
      }
      verifyTarget();
      const args = request.args;
      let result;
      switch (request.recipeId) {
        case 'observe-cluster': {
          const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
          const observed = kubectlJson(namespace, ['get', 'namespace', namespace], 'Namespace');
          if (observed.apiVersion !== 'v1' || observed.kind !== 'Namespace' ||
              observed.metadata?.name !== namespace || typeof observed.metadata?.uid !== 'string') {
            throw new Error('The pinned Kubernetes namespace is not current.');
          }
          result = { namespace: { name: namespace, uid: observed.metadata.uid } };
          break;
        }
        case 'verify-hubble':
          result = verifyHubble();
          break;
        case 'observe-storage':
          result = observeStorage(args);
          break;
        case 'observe-sandbox':
          result = observeSandbox(args);
          break;
        case 'observe-network-policy':
          result = observeNetworkPolicy(args);
          break;
        case 'probe-service':
          result = probeService(args);
          break;
        case 'observe-owned-resource':
          result = observeOwnedResource(args);
          break;
        case 'mount-file':
          result = readMountFile(args);
          break;
        default:
          throw new Error('Only source-owned P1 native recipes are supported.');
      }
      return { clusterIdentity: expectedClusterIdentity, ...result };
    },
  });
}
