#!/usr/bin/env node
// Reproducible AKS/Kata evidence collector for the AgentHost executor sidecar (#476).
//
// Stands up a NON-PRODUCTION copy of k8s/base/sandbox-template-agenthost.yaml (renamed,
// pinned to an explicit image ref), waits for the warm pool, drives the real executor
// protocol from inside the pod via scripts/validation/kata-sidecar-probe.mjs, proves the
// fail-closed behaviour by running the same template WITHOUT the sidecar, prints a
// labelled transcript, and deletes everything it created.
//
// Production objects are never touched: every object it applies is prefixed and every one
// of them is deleted in the finally block.
//
//   node scripts/validation/collect-kata-evidence.mjs \
//     --image <registry>/agentweaver-agent-host:<tag> \
//     [--namespace agentweaver] [--name agentweaver-katafix] [--keep]
//
// Requires: kubectl context already pointing at the target cluster.

import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..');

function arg(name, fallback) {
  const index = process.argv.indexOf(`--${name}`);
  return index >= 0 && process.argv[index + 1] ? process.argv[index + 1] : fallback;
}

const NAMESPACE = arg('namespace', 'agentweaver');
const NAME = arg('name', 'agentweaver-katafix');
const NOSIDECAR = `${NAME}-nosidecar`;
const IMAGE = arg('image', null);
const KEEP = process.argv.includes('--keep');
// evidence = the isolation proofs; capability = the developer workloads the executor must
// support (npm/NuGet/apt/BuildKit/preview); all = both, which is what a full review needs.
const PHASE = arg('phase', 'all');
const REUSE = arg('reuse', null);

if (!IMAGE) {
  console.error('--image <registry>/agentweaver-agent-host:<tag> is required.');
  process.exit(2);
}
if (!['evidence', 'capability', 'all'].includes(PHASE)) {
  console.error(`--phase must be evidence|capability|all (got ${PHASE}).`);
  process.exit(2);
}

const SECRET_PATTERNS = [
  // Redact anything token-shaped so a transcript can be pasted into a public PR. Image
  // digests are deliberately NOT redacted — a reviewer must be able to pin the artifact to
  // an exact image — so the hex rule skips anything introduced by `sha256:`.
  [/\b(gh[pousr]_[A-Za-z0-9]{10,})\b/g, '<redacted-github-token>'],
  [/\b(ey[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b/g, '<redacted-jwt>'],
  [/(?<!sha256:)\b([A-Fa-f0-9]{64})\b/g, '<redacted-64-hex>'],
];

function redact(text) {
  return SECRET_PATTERNS.reduce((acc, [pattern, replacement]) => acc.replace(pattern, replacement), text ?? '');
}

function run(command, args, { input, check = false, quiet = false } = {}) {
  const result = spawnSync(command, args, { input, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
  const output = redact(`${result.stdout ?? ''}${result.stderr ?? ''}`);
  if (!quiet && output.trim()) console.log(output.trimEnd());
  if (check && result.status !== 0) throw new Error(`${command} ${args.join(' ')} exited ${result.status}`);
  return { status: result.status, output };
}

const kubectl = (args, options) => run('kubectl', ['-n', NAMESPACE, ...args], options);

function section(title) {
  console.log(`\n${'='.repeat(78)}\n== ${title}\n${'='.repeat(78)}`);
}

// Rename + pin the shipped template so the evidence is collected against the manifest that
// is actually under review, not a hand-written copy of it.
function renderTemplate({ withSidecar }) {
  const source = path.join(repoRoot, 'k8s', 'base', 'sandbox-template-agenthost.yaml');
  const document = fs.readFileSync(source, 'utf8');
  const objects = document.split(/^---$/m).map((part) => part.trim()).filter(Boolean);
  const template = objects[objects.length - 1]
    .replace(/^(metadata:\n(?:.*\n)*?  name: )agentweaver-agent-host$/m, `$1${withSidecar ? NAME : NOSIDECAR}`)
    .replace(/image: \S+agentweaver-agent-host:\S+/g, `image: ${IMAGE}`);
  if (withSidecar) return template;

  // For the fail-closed case, remove ONLY the executor sidecar container. Doing it on the
  // server-rendered JSON (not by editing YAML text) keeps every other field byte-identical
  // to the manifest under review, including the fail-closed startup probe.
  const asJson = run('kubectl', ['create', '--dry-run=client', '-o', 'json', '-f', '-'],
    { input: template, check: true, quiet: true });
  const object = JSON.parse(asJson.output);
  const containers = object.spec.podTemplate.spec.containers;
  const remaining = containers.filter((container) => container.name !== 'agentweaver-exec');
  if (remaining.length !== containers.length - 1)
    throw new Error('expected exactly one agentweaver-exec container to remove');
  object.spec.podTemplate.spec.containers = remaining;
  return JSON.stringify(object);
}

function renderWarmPool(name, replicas) {
  return [
    'apiVersion: extensions.agents.x-k8s.io/v1beta1',
    'kind: SandboxWarmPool',
    'metadata:',
    `  name: ${name}`,
    `  namespace: ${NAMESPACE}`,
    'spec:',
    '  sandboxTemplateRef:',
    `    name: ${name}`,
    `  replicas: ${replicas}`,
    '  updateStrategy:',
    '    type: Recreate',
  ].join('\n');
}

function podsFor(name) {
  const { output } = kubectl(
    ['get', 'pods', '--no-headers', '-o',
      'custom-columns=N:.metadata.name,READY:.status.containerStatuses[*].ready,PHASE:.status.phase,RESTARTS:.status.containerStatuses[*].restartCount'],
    { quiet: true });
  return output.split('\n').filter((line) => line.startsWith(name));
}

function waitForReady(name, expected, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let last = [];
  while (Date.now() < deadline) {
    last = podsFor(name);
    const ready = last.filter((line) => /\btrue,true\b/.test(line) && /Running/.test(line));
    if (ready.length >= expected) return { ready: true, lines: last };
    spawnSync(process.platform === 'win32' ? 'powershell' : 'sh',
      process.platform === 'win32' ? ['-c', 'Start-Sleep -Seconds 10'] : ['-c', 'sleep 10']);
  }
  return { ready: false, lines: last };
}

function firstPod(name) {
  const line = podsFor(name).find((entry) => /Running/.test(entry));
  return line ? line.split(/\s+/)[0] : null;
}

const created = [];

const CAPABILITY_CASES = arg('cases',
  'npm,nuget,apt,buildkit,preview,nested-userns').split(',').map((entry) => entry.trim()).filter(Boolean);

// Streams a probe script into the pod and refuses to run a truncated copy.
function copyScript(pod, fileName) {
  const script = path.join(here, fileName);
  const expectedBytes = fs.statSync(script).size;
  kubectl(['exec', pod, '-c', 'agentweaver-agent-host', '--', 'mkdir', '-p', '/tmp/awx-evidence'], { check: true });
  // `kubectl cp` cannot take a Windows absolute path (the drive colon is parsed as a pod
  // separator), so stream the bytes through stdin instead — portable on every platform.
  run('kubectl', ['-n', NAMESPACE, 'exec', '-i', pod, '-c', 'agentweaver-agent-host', '--',
    'sh', '-c', `cat > /tmp/awx-evidence/${fileName}`],
  { input: fs.readFileSync(script), check: true });
  const copied = kubectl(['exec', pod, '-c', 'agentweaver-agent-host', '--',
    'wc', '-c', `/tmp/awx-evidence/${fileName}`], { check: true });
  const copiedBytes = Number.parseInt(copied.output.trim().split(/\s+/)[0], 10);
  if (!Number.isFinite(copiedBytes) || Math.abs(copiedBytes - expectedBytes) > 64)
    throw new Error(`${fileName} copy is not intact: ${copiedBytes} bytes in pod vs ${expectedBytes} locally`);
}

function runProbe(pod, fileName, probeCase) {
  return kubectl(['exec', pod, '-c', 'agentweaver-agent-host', '--',
    'node', `/tmp/awx-evidence/${fileName}`, probeCase]);
}

function apply(manifest, kind, name) {
  run('kubectl', ['-n', NAMESPACE, 'apply', '-f', '-'], { input: manifest, check: true });
  created.push([kind, name]);
}

try {
  section('0. Inputs');
  console.log(`namespace   : ${NAMESPACE}`);
  console.log(`image       : ${IMAGE}`);
  console.log(`phase       : ${PHASE}`);
  kubectl(['get', 'nodes', '-o', 'custom-columns=NAME:.metadata.name,RUNTIME:.status.nodeInfo.containerRuntimeVersion,HANDLER:.metadata.labels.kubernetes\\.azure\\.com/kata-mshv-vm-isolation']);

  let pod = REUSE;
  if (REUSE) {
    section('1. Reusing an existing validation pod');
    console.log(`pod         : ${REUSE}`);
  } else {
    section('1. Apply the reviewed manifest under a validation name');
    apply(renderTemplate({ withSidecar: true }), 'sandboxtemplate', NAME);
    apply(renderWarmPool(NAME, 2), 'sandboxwarmpool', NAME);

    section('2. Warm-pool readiness (expect 2 pods, both containers ready)');
    const pool = waitForReady(NAME, 2, 15 * 60 * 1000);
    console.log(pool.lines.join('\n'));
    if (!pool.ready) throw new Error('validation warm pool did not reach 2/2 ready');
    pod = firstPod(NAME);
  }

  section(`3. Resolved image digest actually running in ${pod}`);
  kubectl(['get', 'pod', pod, '-o',
    'jsonpath={range .status.containerStatuses[*]}{.name}{"  "}{.image}{"  "}{.imageID}{"\\n"}{end}']);

  section('4. Effective pod security context as admitted by the API server');
  kubectl(['get', 'pod', pod, '-o',
    'jsonpath=hostPID={.spec.hostPID} hostNetwork={.spec.hostNetwork} hostIPC={.spec.hostIPC} shareProcessNamespace={.spec.shareProcessNamespace} hostPathVolumes={.spec.volumes[*].hostPath}{"\\n"}']);
  kubectl(['get', 'pod', pod, '-o',
    'jsonpath={range .spec.containers[*]}{.name}: privileged={.securityContext.privileged} allowPrivilegeEscalation={.securityContext.allowPrivilegeEscalation} runAsNonRoot={.securityContext.runAsNonRoot} seccomp={.securityContext.seccompProfile.type} caps={.securityContext.capabilities}{"\\n"}{end}']);

  if (PHASE === 'evidence' || PHASE === 'all') {
    section('5. Executor protocol evidence, collected from inside the pod');
    copyScript(pod, 'kata-sidecar-probe.mjs');
    for (const evidenceCase of ['probe', 'coreclr', 'process', 'mount', 'procfs', 'argv', 'stdout', 'termination', 'limits']) {
      section(`5.${evidenceCase}`);
      runProbe(pod, 'kata-sidecar-probe.mjs', evidenceCase);
    }
  }

  if (PHASE === 'capability' || PHASE === 'all') {
    section('9. Developer-workload capability evidence (real network, real toolchains)');
    copyScript(pod, 'kata-capability-probe.mjs');
    for (const capabilityCase of CAPABILITY_CASES) {
      section(`9.${capabilityCase}`);
      runProbe(pod, 'kata-capability-probe.mjs', capabilityCase);
    }
  }

  if (PHASE === 'evidence' || PHASE === 'all') {
    section('6. Fail-closed: the same manifest without the executor sidecar');
    apply(renderTemplate({ withSidecar: false }), 'sandboxtemplate', NOSIDECAR);
    apply(renderWarmPool(NOSIDECAR, 1), 'sandboxwarmpool', NOSIDECAR);
    const failClosed = waitForReady(NOSIDECAR, 1, 4 * 60 * 1000);
    console.log(failClosed.lines.join('\n'));
    if (failClosed.ready) throw new Error('SECURITY REGRESSION: AgentHost became ready without the executor sidecar');
    const failedPod = (podsFor(NOSIDECAR)[0] || '').split(/\s+/)[0];
    if (failedPod) {
      kubectl(['logs', failedPod, '--tail=8', '--all-containers'], { quiet: false });
      kubectl(['get', 'pod', failedPod, '-o',
        'jsonpath={range .status.containerStatuses[*]}{.name} exitCode={.state.terminated.exitCode} reason={.state.terminated.reason}{"\\n"}{end}']);
    }
  }

  section('7. Pod health after the evidence run');
  console.log(podsFor(REUSE ? pod : NAME).join('\n'));
} finally {
  if (KEEP || REUSE) {
    console.log('\n--keep/--reuse set: leaving validation objects in place. Delete them manually.');
  } else {
    section('8. Cleanup (validation objects only)');
    for (const [kind, name] of created.reverse()) {
      kubectl(['delete', kind, name, '--ignore-not-found']);
    }
  }
}
