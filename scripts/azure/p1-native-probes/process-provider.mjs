/**
 * Supplies the allowlisted local process recipes used by P1 native recovery probes.
 * Each recipe invokes a fixed command with structured arguments rather than accepting shell text.
 */
import { createHash, X509Certificate } from 'node:crypto';
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
const CLUSTER_IDENTITY_PREFIX = 'agentweaver.cluster-identity.v1\0cluster-ca\0';
const CILIUM_GENERATION = 'agentweaver.dev/egress-generation';
const CILIUM_HASH = 'agentweaver.dev/egress-intent-sha256';
const CILIUM_REVOKED = 'agentweaver.dev/egress-revoked';
const CLAIM_UID = 'agents.x-k8s.io/claim-uid';
const MANAGED = 'agentweaver.dev/managed-sandbox';
const OWNER = 'agentweaver.dev/sandbox-owner';
const SANDBOX_LIFECYCLE = 'agentweaver.dev/sandbox-lifecycle';
const SANDBOX_GENERATION = 'agentweaver.dev/sandbox-generation';
const SANDBOX_FENCE = 'agentweaver.dev/sandbox-fence';
const SANDBOX_OPERATION = 'agentweaver.dev/sandbox-operation';
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

function sameRecord(left, right) {
  if (!isRecord(left) || !isRecord(right)) return false;
  const leftKeys = Object.keys(left).sort();
  const rightKeys = Object.keys(right).sort();
  return leftKeys.length === rightKeys.length &&
    leftKeys.every((key, index) => key === rightKeys[index] && left[key] === right[key]);
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

/** Parses kubectl jsonpath-as-json output, which wraps selected fields in an array. */
function parseJsonArray(text, description) {
  if (typeof text !== 'string' || text.length === 0 || text.length > 1024 * 1024) {
    throw new Error(`${description} JSON output is missing or too large.`);
  }
  try {
    const value = JSON.parse(text);
    if (!Array.isArray(value)) throw new Error('not an array');
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

/** Requires HTTPS and rejects kubeconfig entries that disable CA verification. */
function verifyKubernetesServer(cluster) {
  let parsed;
  try {
    parsed = new URL(cluster.server);
  } catch {
    throw new Error('The selected Kubernetes context has no valid API server URL.');
  }
  if (parsed.protocol !== 'https:' || parsed.username || parsed.password ||
      parsed.search || parsed.hash || parsed.pathname !== '/' ||
      parsed.port && parsed.port !== '443' ||
      cluster['insecure-skip-tls-verify'] === true) {
    throw new Error('The selected Kubernetes context does not use HTTPS with CA validation.');
  }
}

/** Hashes sorted CA certificate DER bytes so PEM formatting and bundle order do not change the pin. */
function clusterIdentityFromCaPem(pem) {
  if (typeof pem !== 'string' || pem.trim().length === 0) {
    throw new Error('The Kubernetes cluster CA certificate is unavailable.');
  }
  const blockPattern = /-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----/g;
  const blocks = [...pem.matchAll(blockPattern)];
  const remainder = pem.replace(blockPattern, '');
  if (blocks.length === 0 ||
      remainder.split(/\r?\n/).some(line => line.trim() && !line.trim().startsWith('#'))) {
    throw new Error('The Kubernetes cluster CA bundle is invalid.');
  }
  const certificates = [...new Map(blocks.map(([, body]) => {
    const encoded = body.replace(/\s/g, '');
    const der = Buffer.from(encoded, 'base64');
    if (der.length === 0 || der.toString('base64') !== encoded) {
      throw new Error('The Kubernetes cluster CA bundle is invalid.');
    }
    try {
      new X509Certificate(der);
    } catch {
      throw new Error('The Kubernetes cluster CA bundle is invalid.');
    }
    return [der.toString('hex'), der];
  })).values()].sort(Buffer.compare);

  const hash = createHash('sha256').update(CLUSTER_IDENTITY_PREFIX, 'utf8');
  const length = Buffer.alloc(4);
  length.writeUInt32BE(certificates.length);
  hash.update(length);
  for (const certificate of certificates) {
    length.writeUInt32BE(certificate.length);
    hash.update(length);
    hash.update(certificate);
  }
  return hash.digest('hex');
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

function selectReadyEndpointTargets(slices, servicePorts) {
  if (!Array.isArray(slices?.items)) throw new Error('The current EndpointSlice list is invalid.');
  const targets = new Map();
  for (const servicePort of servicePorts) {
    const requestedTarget = servicePort.targetPort ?? servicePort.port;
    const servicePortName = servicePort.name ?? null;
    if ((!Number.isSafeInteger(requestedTarget) || requestedTarget <= 0) &&
        (typeof requestedTarget !== 'string' || requestedTarget.length === 0 || !servicePortName)) {
      throw new Error('The exact TCP Service target port is invalid or ambiguous.');
    }
    const addressesByPort = new Map();
    for (const slice of slices.items) {
      const matchingPorts = (Array.isArray(slice?.ports) ? slice.ports : [])
        .filter(port =>
          (port?.protocol ?? 'TCP') === 'TCP' &&
          (servicePortName === null ? port?.name == null : port?.name === servicePortName) &&
          (typeof requestedTarget !== 'number' || port?.port === requestedTarget) &&
          Number.isSafeInteger(port?.port) && port.port > 0);
      for (const port of matchingPorts) {
        const readyAddresses = (Array.isArray(slice?.endpoints) ? slice.endpoints : [])
          .filter(endpoint => endpoint?.conditions?.ready === true && Array.isArray(endpoint.addresses))
          .flatMap(endpoint => endpoint.addresses)
          .filter(address => typeof address === 'string' && address.length > 0);
        if (readyAddresses.length === 0) continue;
        if (!addressesByPort.has(port.port)) addressesByPort.set(port.port, new Set());
        readyAddresses.forEach(address => addressesByPort.get(port.port).add(address));
      }
    }
    if (addressesByPort.size !== 1) {
      throw new Error('The Service port does not map to one exact ready EndpointSlice target port.');
    }
    const [targetPort, addresses] = addressesByPort.entries().next().value;
    if (addresses.size === 0) throw new Error('The target Service has no ready TCP endpoints.');
    targets.set(servicePort.port, { targetPort, addresses });
  }
  return targets;
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

/**
 * Creates a provider that executes approved probe recipes and records bounded process evidence.
 * Unknown recipes or invalid inputs are rejected before any process is started.
 */
export function createP1NativeProcessProvider({
  kubeContext,
  clusterIdentity,
  execute = run,
  now = Date.now,
} = {}) {
  requireString(kubeContext, 'Kubernetes context');
  if (!isRecord(clusterIdentity) || !SHA256.test(clusterIdentity.sha256 ?? '')) {
    throw new Error('A SHA-256 Kubernetes cluster identity is required.');
  }
  if (typeof execute !== 'function' || typeof now !== 'function') {
    throw new Error('The fixed native probe requires process and clock functions.');
  }
  const expectedClusterIdentity = Object.freeze({ sha256: clusterIdentity.sha256 });

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
    // Flatten resolves file-based CA data; JSONPath keeps user credentials out of captured output.
    const result = invoke('kubectl', kubectlArgs(null, [
      'config', 'view', '--minify', '--flatten', '--raw',
      '-o=jsonpath-as-json={.clusters[0].cluster}',
    ]));
    if (result.status !== 0) {
      throw new Error('Kubernetes context observation failed.');
    }
    const clusters = parseJsonArray(result.stdout, 'Kubernetes cluster trust');
    if (!Array.isArray(clusters) || clusters.length !== 1 ||
        !isRecord(clusters[0]) || typeof clusters[0].server !== 'string') {
      throw new Error('The selected Kubernetes context is ambiguous.');
    }
    const cluster = clusters[0];
    verifyKubernetesServer(cluster);
    const encodedCa = cluster['certificate-authority-data'];
    if (typeof encodedCa !== 'string' || encodedCa.length === 0) {
      throw new Error('The selected Kubernetes context has no cluster CA certificate.');
    }
    const caData = Buffer.from(encodedCa, 'base64');
    if (caData.length === 0 || caData.toString('base64') !== encodedCa ||
        clusterIdentityFromCaPem(caData.toString('utf8')) !== expectedClusterIdentity.sha256) {
      throw new Error('The selected Kubernetes cluster does not match the pinned cluster identity.');
    }
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
    const candidates = pods.items.filter(pod =>
      pod.metadata?.namespace === namespace &&
      Object.entries(labels).every(([key, value]) => pod.metadata?.labels?.[key] === value) &&
      (!expectedUid || pod.metadata?.uid === expectedUid));
    if (candidates.some(pod => pod.apiVersion !== 'v1' || pod.kind !== 'Pod')) {
      throw new Error('The exact current Pod list contains a non-Pod object.');
    }
    const matches = candidates;
    if (matches.length !== 1) throw new Error('The exact current owner Pod is missing or ambiguous.');
    return matches[0];
  };

  const observeSandbox = (args) => {
    const namespace = requireString(args.namespace, 'namespace', DNS_LABEL);
    const owner = args.owner;
    const sandboxClaimUid = requireString(args.sandboxClaimUid, 'SandboxClaim UID');
    const workspaceClaimUid = requireString(args.workspaceClaimUid, 'workspace PVC claim UID');
    const placement = args.placement;
    const placementClaimUid = requireString(placement?.resource?.resourceId, 'placed SandboxClaim UID');
    if (placementClaimUid !== sandboxClaimUid) {
      throw new Error('The current placement must identify the exact SandboxClaim UID.');
    }
    const expectedOperationId = requireString(args.expectedOperationId, 'Sandbox creation operation ID');
    const expectedLabels = {
      [MANAGED]: 'true',
      [OWNER]: ownerFingerprint(owner),
      [SANDBOX_LIFECYCLE]: String(requirePositive(
        placement.lifecycleGeneration, 'Sandbox lifecycle generation')),
      [SANDBOX_GENERATION]: String(requirePositive(
        placement.resource.generation, 'Sandbox resource generation')),
      [SANDBOX_FENCE]: String(requirePositive(
        placement.providerFencingGeneration, 'Sandbox provider fencing generation')),
      [SANDBOX_OPERATION]: expectedOperationId.replaceAll('-', '').toLowerCase(),
      [CLAIM_UID]: sandboxClaimUid,
    };
    if (!expectedLabels[SANDBOX_OPERATION] ||
        !sameRecord(args.expectedLabels, expectedLabels)) {
      throw new Error('The native Sandbox selector must match the exact placement, operation, owner, and claim UID.');
    }
    const pod = findPod(namespace, expectedLabels, args.expectedPodUid);
    const mounts = mountReceipt(pod, namespace, workspaceClaimUid,
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
    const resourceId = requireString(args.resourceId, 'resource ID');
    if (resourceId !== expectedUid) {
      throw new Error('The SandboxClaim UID must match its provider resource ID.');
    }
    const allowEmptyPodUidInventory = args.allowEmptyPodUidInventory === true;
    if ((args.allowEmptyPodUidInventory !== undefined &&
         typeof args.allowEmptyPodUidInventory !== 'boolean') ||
        !Array.isArray(args.expectedPodUids) ||
        (args.expectedPodUids.length === 0 && !allowEmptyPodUidInventory) ||
        (args.expectedPodUids.length > 0 && allowEmptyPodUidInventory)) {
      throw new Error('A nonempty previously observed AgentHost Pod UID inventory is required, except for explicit late-resource discovery.');
    }
    const expectedPodUids = args.expectedPodUids.map(uid =>
      requireString(uid, 'previously observed Sandbox Pod UID'));
    const expectedPodUidSet = new Set(expectedPodUids);
    if (expectedPodUidSet.size !== expectedPodUids.length) {
      throw new Error('Previously observed Sandbox Pod UIDs must be unique.');
    }
    const fingerprint = ownerFingerprint(args.owner);
    const items = kubectlJson(namespace, [
      'get', 'sandboxclaims.extensions.agents.x-k8s.io',
    ], 'Sandbox claim list');
    if (items.apiVersion !== 'v1' || items.kind !== 'List' || !Array.isArray(items.items)) {
      throw new Error('The current Sandbox claim list is invalid.');
    }
    const matches = items.items.filter(item => item.metadata?.uid === expectedUid);
    if (matches.length > 1) throw new Error('The invocation-owned Sandbox claim UID is ambiguous.');
    if (matches.length === 1) {
      const claim = matches[0];
      if (claim.metadata?.namespace !== namespace ||
          claim.apiVersion !== 'extensions.agents.x-k8s.io/v1beta1' ||
          claim.kind !== 'SandboxClaim') {
        throw new Error('The exact resource UID is not a SandboxClaim in the pinned namespace.');
      }
      if (claim.metadata?.labels?.[MANAGED] !== 'true' ||
          claim.metadata?.labels?.[OWNER] !== fingerprint) {
        throw new Error('The exact SandboxClaim UID is present but its owner labels do not match.');
      }
    }
    const pods = kubectlJson(namespace, [
      'get', 'pods',
    ], 'Sandbox claim Pod list');
    if (pods.apiVersion !== 'v1' || pods.kind !== 'List' || !Array.isArray(pods.items)) {
      throw new Error('The current Sandbox claim Pod list is invalid.');
    }
    const dependentPods = new Map();
    for (const pod of pods.items) {
      const podUid = pod.metadata?.uid;
      const knownPod = expectedPodUidSet.has(podUid);
      const claimLinkedPod = pod.metadata?.labels?.[CLAIM_UID] === expectedUid;
      if (!knownPod && !claimLinkedPod) continue;
      if (pod.metadata?.namespace !== namespace ||
          pod.apiVersion !== 'v1' || pod.kind !== 'Pod') {
        throw new Error('A known Sandbox Pod UID is not a Pod in the pinned namespace.');
      }
      const labels = pod.metadata?.labels;
      if (labels?.[MANAGED] !== 'true' || labels?.[OWNER] !== fingerprint) {
        throw new Error('A known Sandbox Pod UID has mismatched owner labels.');
      }
      if (knownPod && labels?.[CLAIM_UID] !== expectedUid) {
        throw new Error('A known Sandbox Pod UID has a mismatched SandboxClaim UID label.');
      }
      const uid = requireString(podUid, 'Sandbox claim Pod UID');
      if (dependentPods.has(uid)) throw new Error('The Sandbox claim Pod UID is ambiguous.');
      dependentPods.set(uid, {
        name: requireString(pod.metadata?.name, 'Sandbox claim Pod name', DNS_LABEL),
        uid,
      });
    }
    return {
      clusterIdentity: expectedClusterIdentity,
      exists: matches.length === 1,
      dependentPods: [...dependentPods.values()],
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
    if (!((policies.apiVersion === 'cilium.io/v2' && policies.kind === 'CiliumNetworkPolicyList') ||
          (policies.apiVersion === 'v1' && policies.kind === 'List')) ||
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
    const servicePorts = (service.spec?.ports ?? [])
      .filter(port => (port?.protocol ?? 'TCP') === 'TCP' &&
        Number.isSafeInteger(port.port) && port.port > 0)
      .map(port => ({
        name: typeof port.name === 'string' && port.name.length > 0 ? port.name : null,
        port: port.port,
        targetPort: port.targetPort,
      }));
    if (servicePorts.length === 0) {
      throw new Error('The target Service has no ready TCP endpoints.');
    }
    const endpointTargets = selectReadyEndpointTargets(slices, servicePorts);
    const host = `${serviceName}.${serviceNamespace}.svc`;
    const attempts = [];
    for (const port of servicePorts) {
      const startedAt = now();
      const probe = invoke('kubectl', kubectlArgs(namespace, [
        'exec', sourcePod.metadata.name, '--container', 'agenthost', '--',
        'timeout', '3', 'bash', '-c', TCP_CONNECT_SCRIPT, 'bash', host, String(port.port),
      ]), 5_000);
      attempts.push({
        servicePort: port.port,
        connected: probe.status === 0,
        startedAt,
      });
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
    const flows = parseHubbleFlows(hubble.stdout)
      .map(flowFields)
      .filter(flow => flow.sourceNamespace === namespace &&
        flow.sourcePodName === sourcePod.metadata.name &&
        flow.isReply !== true &&
        Number.isFinite(Date.parse(flow.time)) &&
        Date.parse(flow.time) <= nowMs &&
        ['FORWARDED', 'DROPPED'].includes(flow.verdict));
    const usedFlows = new Set();
    const portVerdicts = [];
    for (const attempt of attempts) {
      const target = endpointTargets.get(attempt.servicePort);
      const matches = flows.flatMap((flow, index) =>
        target?.targetPort === flow.destinationPort &&
          target.addresses.has(flow.destinationAddress) &&
          Date.parse(flow.time) >= attempt.startedAt && !usedFlows.has(index)
          ? [{ flow, index }]
          : []);
      const verdicts = new Set(matches.map(({ flow }) => flow.verdict));
      if (matches.length === 0 || verdicts.size !== 1) {
        throw new Error('Each probed Service port requires a fresh matching Cilium datapath flow.');
      }
      const match = matches[0];
      usedFlows.add(match.index);
      const connected = match.flow.verdict === 'FORWARDED';
      if (connected !== attempt.connected) {
        throw new Error('The TCP probe result contradicts its exact Cilium datapath flow.');
      }
      portVerdicts.push({
        servicePort: attempt.servicePort,
        targetPort: target.targetPort,
        verdict: match.flow.verdict,
        connected,
      });
    }
    const verdicts = new Set(portVerdicts.map(item => item.verdict));
    if (verdicts.size !== 1) {
      throw new Error('The current source-to-Service probes lack one consistent Cilium verdict.');
    }
    const currentPod = readPod(namespace, sourcePod.metadata.name);
    if (currentPod.metadata.uid !== sourcePodUid) {
      throw new Error('The source Pod changed during the Cilium datapath probe.');
    }
    const currentPolicy = observeNetworkPolicy({
      namespace,
      resourceId: networkPolicyResourceId,
      expectedUid: policyUid,
    });
    if (currentPolicy.uid !== policy.uid ||
        currentPolicy.appliedIntentGeneration !== policy.appliedIntentGeneration ||
        currentPolicy.intentHash !== policy.intentHash ||
        currentPolicy.revoked !== policy.revoked ||
        !sameRecord(currentPolicy.selectorLabels, policy.selectorLabels)) {
      throw new Error('The Cilium policy changed during the datapath probe.');
    }
    const currentService = kubectlJson(serviceNamespace,
      ['get', 'service', serviceName], 'Service');
    if (currentService.apiVersion !== 'v1' || currentService.kind !== 'Service' ||
        currentService.metadata?.namespace !== serviceNamespace ||
        currentService.metadata?.name !== serviceName ||
        currentService.metadata?.uid !== service.metadata.uid) {
      throw new Error('The target Service changed during the datapath probe.');
    }
    const verdict = verdicts.has('FORWARDED') ? 'allowed' : 'denied';
    const readyEndpointCount = new Set(
      [...endpointTargets.values()].flatMap(target => [...target.addresses]),
    ).size;
    return {
      clusterIdentity: expectedClusterIdentity,
      sourcePodUid,
      serviceIdentity: `${serviceNamespace}/${serviceName}`,
      policyUid,
      policyGeneration,
      targetServiceUid: service.metadata.uid,
      readyEndpointCount,
      probedPorts: servicePorts.map(port => port.port),
      portVerdicts,
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
          request.args.expectedClusterIdentity.sha256 !== expectedClusterIdentity.sha256) {
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
