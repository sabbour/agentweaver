/**
 * Establishes the guarded PostgreSQL access needed to bootstrap identity service schemas.
 * The flow uses a temporary local port forward and removes its transient process and files afterward.
 */
import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { isAbsolute, join, relative, resolve, sep } from 'node:path';
import { run, runAz, redact } from './exec.mjs';
import { assertP0KubernetesPermissions } from './namespace-bootstrap.mjs';
import { p1PostgresPlanHash } from './p1-postgres-plan.mjs';

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const NAMESPACE = 'agentweaver';
const KUBERNETES_PERMISSIONS = [
  ['create', 'configmaps', NAMESPACE],
  ['get', 'configmaps', NAMESPACE],
  ['delete', 'configmaps', NAMESPACE],
  ['create', 'pods', NAMESPACE],
  ['get', 'pods', NAMESPACE],
  ['list', 'pods', NAMESPACE],
  ['watch', 'pods', NAMESPACE],
  ['delete', 'pods', NAMESPACE],
  ['create', 'pods', NAMESPACE, 'portforward'],
];
const OWNER_LABEL = 'agentweaver.io/task-owner';
const RUN_LABEL = 'agentweaver.io/task-run';
const OWNER = 'identity-postgres-bootstrap';
const LOCAL_PORT = 15432;
const POSTGRES_PORT = 5432;
const PROXY_IMAGE =
  'mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4';
const BROKER_PROJECT = join('services', 'identity', 'Agentweaver.Identity.Broker', 'Agentweaver.Identity.Broker.csproj');
const PROXY_PROJECT = join('tools', 'IdentityPostgresTcpProxy', 'IdentityPostgresTcpProxy.csproj');
const SUCCESS_MARKERS = {
  bootstrap: 'IDENTITY_POSTGRES_BOOTSTRAP_OK database=agentweaver schema=identity_broker',
  verify: 'IDENTITY_POSTGRES_BOOTSTRAP_VERIFIED database=agentweaver schema=identity_broker',
};

function diagnostic(result) {
  const output = [result.stderr, result.stdout].filter(Boolean).join('\n').trim();
  return redact(output || `process exited with status ${result.status}`);
}

function assertCommand(result, action) {
  if (result.status !== 0) throw new Error(`${action}: ${diagnostic(result)}`);
}

function parseJson(result, action) {
  assertCommand(result, action);
  try {
    return JSON.parse(result.stdout);
  } catch {
    throw new Error(`${action} returned malformed JSON.`);
  }
}

function assertLoopbackPortAvailable(port) {
  return new Promise((resolve, reject) => {
    const server = createServer();
    server.once('error', () => reject(new Error(`Local PostgreSQL tunnel port ${port} is already in use.`)));
    server.listen(port, '127.0.0.1', () => server.close(error =>
      error ? reject(new Error(`Could not release local PostgreSQL tunnel port ${port}.`)) : resolve()));
  });
}

async function stopProcess(child) {
  if (child.exitCode !== null || child.signalCode !== null) return;
  if (child.pid === undefined || !child.kill()) {
    if (child.exitCode !== null || child.signalCode !== null) return;
    throw new Error('Could not stop the owned PostgreSQL port-forward process.');
  }

  if (child.exitCode !== null || child.signalCode !== null) return;
  let timer;
  const stopped = await Promise.race([
    once(child, 'exit').then(() => true),
    new Promise(resolve => {
      timer = setTimeout(() => resolve(false), 10_000);
      timer.unref?.();
    }),
  ]);
  clearTimeout(timer);
  if (!stopped) {
    if (!child.kill('SIGKILL')) throw new Error('Could not terminate the owned PostgreSQL port-forward process.');
    await once(child, 'exit');
  }
}

/**
 * Starts a local forward to the selected PostgreSQL pod and waits until its port accepts connections.
 * If startup fails or times out, the child process is stopped and an error is thrown.
 */
export function startIdentityPostgresPortForward({ kubeconfig, podName, localPort, namespace, keyVault = false }, {
  spawnProcess = spawn,
  startupTimeoutMs = 30_000,
} = {}) {
  if (keyVault !== false && keyVault !== true)
    throw new Error('Key Vault port-forward mode must be an explicit boolean.');
  if (keyVault && (localPort !== 15443 || namespace !== NAMESPACE))
    throw new Error('Key Vault port-forward requires the exact P0 namespace and loopback port 15443.');
  const remotePort = keyVault ? 8443 : POSTGRES_PORT;
  const child = spawnProcess('kubectl', [
    '--kubeconfig', kubeconfig,
    'port-forward',
    '--address', '127.0.0.1',
    `pod/${podName}`,
    `${localPort}:${remotePort}`,
    '--namespace', namespace,
  ], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });

  return new Promise((resolve, reject) => {
    let output = '';
    let settled = false;
    const timer = setTimeout(() => fail(new Error('PostgreSQL loopback port-forward did not become ready.')),
      startupTimeoutMs);
    const fail = async error => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      try {
        await stopProcess(child);
        reject(new Error(`${error.message} ${redact(output).trim()}`.trim()));
      } catch (stopError) {
        reject(new Error(`${error.message}; port-forward cleanup failed: ${stopError.message}`));
      }
    };
    const onOutput = chunk => {
      output = (output + chunk.toString()).slice(-2_000);
      if (!settled && output.includes(`Forwarding from 127.0.0.1:${localPort} -> ${remotePort}`)) {
        if (typeof child.pid !== 'number') {
          void fail(new Error('PostgreSQL port-forward did not expose an owned process ID.'));
          return;
        }
        settled = true;
        clearTimeout(timer);
        resolve({ pid: child.pid, stop: () => stopProcess(child) });
      }
    };
    child.stdout?.on('data', onOutput);
    child.stderr?.on('data', onOutput);
    child.once('error', error => void fail(new Error(`Could not start kubectl port-forward: ${error.message}`)));
    child.once('exit', code => {
      if (!settled) void fail(new Error(`kubectl port-forward exited before readiness with status ${code ?? 'unknown'}.`));
    });
  });
}

function ownedObject(execKubectl, kubeconfig, kind, name, runId) {
  const result = execKubectl([
    '--kubeconfig', kubeconfig, 'get', kind, name, '--namespace', NAMESPACE, '-o', 'json',
  ], { check: false, timeout: 15_000 });
  if (result.status !== 0) {
    if (/not found/i.test(result.stderr ?? '')) return undefined;
    throw new Error(`Could not read back exact temporary ${kind}: ${diagnostic(result)}`);
  }

  const object = parseJson(result, `Temporary ${kind} readback`);
  if (object.metadata?.name !== name || object.metadata?.namespace !== NAMESPACE ||
      object.metadata?.labels?.[OWNER_LABEL] !== OWNER || object.metadata?.labels?.[RUN_LABEL] !== runId) {
    throw new Error(`Temporary ${kind} ownership readback did not match this bootstrap run.`);
  }
  return object;
}

function removeOwnedObject(execKubectl, kubeconfig, kind, name, runId) {
  const existing = ownedObject(execKubectl, kubeconfig, kind, name, runId);
  if (!existing) return false;

  const result = execKubectl([
    '--kubeconfig', kubeconfig, 'delete', kind, name, '--namespace', NAMESPACE,
    '--wait=true', '--timeout=60s',
  ], { check: false, timeout: 70_000 });
  assertCommand(result, `Could not delete owned temporary ${kind}`);
  if (ownedObject(execKubectl, kubeconfig, kind, name, runId))
    throw new Error(`Temporary ${kind} still exists after exact-name deletion.`);
  return true;
}

function buildProxy(repoRoot, outputDirectory, {
  execDotnet = (args, options) => run('dotnet', args, options),
} = {}) {
  const result = execDotnet([
    'publish', join(repoRoot, PROXY_PROJECT), '--configuration', 'Release',
    '--output', outputDirectory, '--no-self-contained', '--property:RestoreLockedMode=true',
  ], { check: false, cwd: repoRoot, timeout: 180_000 });
  assertCommand(result, 'Could not build the temporary PostgreSQL TCP proxy');
}

/** Final line of a successful, read-back ownership handover. */
export const P1_OWNERSHIP_HANDOVER_MARKER = 'P1_POSTGRES_OWNERSHIP_HANDOVER_VERIFIED ';

const P1_MODES = new Set([
  'initialize', 'inspect', 'apply-pending', 'apply-pending-dry-run',
  'projects-config-grants', 'projects-config-grants-dry-run',
]);
// Each initializer mode prints its own final marker line. The runner accepts success only when it sees the
// marker for the mode it asked for, so a run that stopped early or ran a different mode cannot pass.
const P1_MARKERS = {
  initialize: 'P1_POSTGRES_OPERATOR_INITIALIZATION_FINISHED runtimeIdentityProof=false deploymentProof=false',
  inspect: 'P1_POSTGRES_INSPECTION_FINISHED writes=false',
  'apply-pending-dry-run': 'P1_POSTGRES_PENDING_MIGRATIONS_DRY_RUN writes=false',
  'apply-pending': 'P1_POSTGRES_PENDING_MIGRATIONS_APPLIED runtimeIdentityProof=false deploymentProof=false',
  'projects-config-grants-dry-run': 'P1_POSTGRES_PROJECTS_CONFIG_GRANTS_DRY_RUN writes=false',
  'projects-config-grants': 'P1_POSTGRES_PROJECTS_CONFIG_GRANTS_APPLIED runtimeIdentityProof=false deploymentProof=false',
};
const P1_VERIFICATION = {
  initialize: 'operator-initialization',
  inspect: 'read-only-inspection',
  'apply-pending-dry-run': 'read-only-pending-migration-dry-run',
  'apply-pending': 'pending-migrations-applied',
  'projects-config-grants-dry-run': 'read-only-projects-config-grants-dry-run',
  'projects-config-grants': 'projects-config-grants-applied',
};

// Explicit approved maxima are small positive integers; the initializer rejects any catalog that ends elsewhere.
function isApprovedMaximum(value) {
  return Number.isSafeInteger(value) && value > 0 && value < 10_000;
}

/** Builds the exact P1 initializer arguments for one validated mode. */
export function p1InitializerArguments(mode, settings) {
  // Inspection needs only the source commit and plan hash. It takes no Core merge, maxima or receipt.
  const source = ['--source-sha', settings.sourceSha];
  if (mode === 'inspect') return ['--inspect', ...source, '--plan-sha256', settings.planSha256];
  // The Projects & Config grant step is bound to the source and plan only. Its dry run gets no receipt.
  if (mode.startsWith('projects-config-grants')) {
    return [
      '--apply-projects-config-grants', ...source, '--plan-sha256', settings.planSha256,
      ...(mode === 'projects-config-grants-dry-run' ? ['--dry-run'] : ['--receipt', settings.receiptPath]),
    ];
  }
  const bound = [...source, '--core-merge-sha', settings.coreMergeSha, '--plan-sha256', settings.planSha256];
  // Fresh initialization keeps its original argument list.
  if (mode === 'initialize') return ['--initialize', ...bound, '--receipt', settings.receiptPath];
  // Pending-migration modes pass the approved maxima. A dry run writes nothing, so it gets no receipt;
  // the real run writes a new receipt file.
  return [
    '--apply-pending-migrations', ...bound,
    '--max-core-version', String(settings.maxCoreVersion),
    '--max-events-version', String(settings.maxEventsVersion),
    ...(mode === 'apply-pending-dry-run' ? ['--dry-run'] : ['--receipt', settings.receiptPath]),
  ];
}

/**
 * Applies the identity PostgreSQL bootstrap through the validated private connection path.
 * An optional P1 initializer must use the locally computed plan hash and a new source-local receipt;
 * it runs over the same ready tunnel before owned transport cleanup.
 * Required Kubernetes access is checked before building the proxy or creating temporary resources.
 */
export async function bootstrapIdentityPostgres({
  repoRoot = process.cwd(), resourceGroup, subscriptionId, tenantId, clusterName,
  postgresHost, adminUsername, runtimePrincipalObjectId, migrationPrincipalObjectId,
  verifyOnly = false,
  foundationProbePrincipalObjectId,
  p1Initializer,
  p1OwnershipHandover,
}, {
  execAz = runAz,
  execKubelogin = (args, options) => run('kubelogin', args, options),
  execKubectl = (args, options) => run('kubectl', args, options),
  execDotnet = (args, options) => run('dotnet', args, options),
  buildProxy: buildProxyDependency,
  startPortForward: startPortForwardDependency = startIdentityPostgresPortForward,
} = {}) {
  if (!resourceGroup || !subscriptionId || !tenantId || !clusterName ||
      clusterName !== `${resourceGroup}-aks` ||
      postgresHost !== `${resourceGroup}-pg.postgres.database.azure.com` ||
      !adminUsername?.trim() ||
      !GUID_PATTERN.test(runtimePrincipalObjectId ?? '') ||
      !GUID_PATTERN.test(migrationPrincipalObjectId ?? '') ||
      runtimePrincipalObjectId.toLowerCase() === migrationPrincipalObjectId.toLowerCase()) {
    throw new Error('Identity PostgreSQL bootstrap requires the exact P0 AKS/PG target, Entra admin, and distinct workload identities.');
  }
  if (foundationProbePrincipalObjectId !== undefined &&
      (!GUID_PATTERN.test(foundationProbePrincipalObjectId) || verifyOnly ||
       [runtimePrincipalObjectId, migrationPrincipalObjectId].some(value =>
         value.toLowerCase() === foundationProbePrincipalObjectId.toLowerCase()))) {
    throw new Error('Probe PostgreSQL bootstrap requires its distinct exact native principal and cannot run Identity verification.');
  }
  if (p1OwnershipHandover !== undefined) {
    // The handover runs only against an already bootstrapped database, so Identity is verified
    // read-only first. It cannot be combined with the initializer or the Probe bootstrap, and it
    // needs the exact migration principal from the identity deployment.
    if (!verifyOnly || p1Initializer !== undefined || foundationProbePrincipalObjectId !== undefined ||
        !GUID_PATTERN.test(p1OwnershipHandover?.migrationPrincipalObjectId ?? '') ||
        [runtimePrincipalObjectId, migrationPrincipalObjectId].some(value =>
          value.toLowerCase() === p1OwnershipHandover.migrationPrincipalObjectId.toLowerCase())) {
      throw new Error('The P1 ownership handover requires Identity verification only and its own distinct migration principal.');
    }
  }
  if (p1Initializer !== undefined) {
    if (p1Initializer === null || typeof p1Initializer !== 'object') {
      throw new Error('Combined PostgreSQL initialization requires a P1 initializer receipt configuration.');
    }
    // A missing mode means the original fresh-initialize call.
    const p1Mode = p1Initializer.mode ?? 'initialize';
    if (!P1_MODES.has(p1Mode))
      throw new Error('The P1 initializer mode must be initialize, inspect, apply-pending, apply-pending-dry-run, projects-config-grants, or projects-config-grants-dry-run.');
    // Only fresh initialization runs Identity bootstrap; inspection and incremental continuation target an
    // initialized database, so Identity Broker is verified read-only first and never re-bootstrapped.
    const writesReceipt = ['initialize', 'apply-pending', 'projects-config-grants'].includes(p1Mode);
    // Inspection and the Projects & Config grant step never touch Core, so they take no Core merge.
    const needsCoreMerge = p1Mode !== 'inspect' && !p1Mode.startsWith('projects-config-grants');
    const pendingMode = p1Mode.startsWith('apply-pending');
    const receiptPath = typeof p1Initializer.receiptPath === 'string'
      ? resolve(p1Initializer.receiptPath) : '';
    const receiptRelativePath = receiptPath ? relative(resolve(repoRoot, 'Temp'), receiptPath) : '';
    // Validate every setting before opening any tunnel:
    // - verifyOnly must be false only for initialize, so an initialized database is never re-bootstrapped;
    // - the source and plan hashes must be exact, and the plan hash must match this checkout;
    // - the Core merge SHA is required except for inspection and the Projects & Config grant step;
    // - maxima are required for the pending modes and forbidden for the others;
    // - writing modes need a new receipt path inside Temp; read-only modes must not pass one.
    if (verifyOnly !== (p1Mode !== 'initialize') || !GUID_PATTERN.test(runtimePrincipalObjectId ?? '') ||
        !GUID_PATTERN.test(migrationPrincipalObjectId ?? '') ||
        !/^[0-9a-f]{40}$/.test(p1Initializer.sourceSha ?? '') ||
        (needsCoreMerge ? !/^[0-9a-f]{40}$/.test(p1Initializer.coreMergeSha ?? '')
          : p1Initializer.coreMergeSha !== undefined) ||
        !/^[0-9a-f]{64}$/.test(p1Initializer.planSha256 ?? '') ||
        p1Initializer.planSha256 !== p1PostgresPlanHash() ||
        (pendingMode
          ? !isApprovedMaximum(p1Initializer.maxCoreVersion) || !isApprovedMaximum(p1Initializer.maxEventsVersion)
          : p1Initializer.maxCoreVersion !== undefined || p1Initializer.maxEventsVersion !== undefined) ||
        (writesReceipt
          ? !receiptPath || !receiptRelativePath || receiptRelativePath === '..' ||
            receiptRelativePath.startsWith(`..${sep}`) || isAbsolute(receiptRelativePath) || existsSync(receiptPath)
          : p1Initializer.receiptPath !== undefined)) {
      throw new Error('Combined PostgreSQL initialization requires exact lowercase source hashes, explicit maximum versions for continuation, and a new receipt under the source Temp directory only for writing modes.');
    }
  }

  const account = parseJson(
    execAz(['account', 'show', '-o', 'json'], { check: false }),
    'Azure account lookup',
  );
  if (account.id !== subscriptionId || account.tenantId !== tenantId || account.state !== 'Enabled')
    throw new Error('Identity PostgreSQL bootstrap Azure account differs from the approved target.');

  await assertLoopbackPortAvailable(LOCAL_PORT);
  const runId = randomUUID().replaceAll('-', '').slice(0, 12);
  const resourceName = `identity-pg-${runId}`;
  const podName = `${resourceName}-proxy`;
  const configMapName = `${resourceName}-files`;
  const labels = {
    [OWNER_LABEL]: OWNER,
    [RUN_LABEL]: runId,
  };
  const directory = mkdtempSync(join(tmpdir(), 'agentweaver-pg-bootstrap-'));
  const kubeconfig = join(directory, 'kubeconfig');
  const proxyOutput = join(directory, 'proxy');
  let portForward;
  let clusterAccessReady = false;
  let primaryError;
  let bootstrapReceipt;
  const cleanup = {
    portForwardStopped: false,
    podRemoved: false,
    configMapRemoved: false,
    kubeconfigRemoved: false,
    proxyFilesRemoved: false,
  };

  try {
    assertCommand(execAz([
      'aks', 'get-credentials', '--resource-group', resourceGroup, '--name', clusterName,
      '--subscription', subscriptionId, '--file', kubeconfig,
    ], { check: false, timeout: 30_000 }), 'Could not get normal-user AKS credentials');
    assertCommand(execKubelogin([
      'convert-kubeconfig', '--login', 'azurecli', '--kubeconfig', kubeconfig,
    ], { check: false, timeout: 30_000 }), 'Could not configure the normal-user AKS kubeconfig');
    assertP0KubernetesPermissions(['--kubeconfig', kubeconfig], execKubectl, [
      ['get', `namespace/${NAMESPACE}`],
      ...KUBERNETES_PERMISSIONS,
    ], 'Identity PostgreSQL bootstrap');
    clusterAccessReady = true;

    (buildProxyDependency ?? ((root, output) => buildProxy(root, output, { execDotnet })))(repoRoot, proxyOutput);
    const binaryData = Object.fromEntries(['Relay.dll', 'Relay.deps.json', 'Relay.runtimeconfig.json'].map(name =>
      [name, readFileSync(join(proxyOutput, name)).toString('base64')]));
    const configMap = {
      apiVersion: 'v1',
      kind: 'ConfigMap',
      metadata: { name: configMapName, namespace: NAMESPACE, labels },
      binaryData,
    };
    assertCommand(execKubectl([
      '--kubeconfig', kubeconfig, 'create', '--filename', '-',
    ], { check: false, cwd: repoRoot, input: JSON.stringify(configMap), timeout: 30_000 }),
    'Could not create the uniquely owned temporary PostgreSQL proxy ConfigMap');
    if (!ownedObject(execKubectl, kubeconfig, 'configmap', configMapName, runId))
      throw new Error('Temporary PostgreSQL proxy ConfigMap was not present after creation.');

    const pod = {
      apiVersion: 'v1',
      kind: 'Pod',
      metadata: {
        name: podName,
        namespace: NAMESPACE,
        labels: {
          ...labels,
          'app.kubernetes.io/name': 'identity-postgres-bootstrap-proxy',
          'agentweaver.io/workload': 'identity-broker-migration',
        },
      },
      spec: {
        restartPolicy: 'Never',
        automountServiceAccountToken: false,
        securityContext: {
          runAsNonRoot: true,
          runAsUser: 10001,
          runAsGroup: 10001,
          seccompProfile: { type: 'RuntimeDefault' },
        },
        containers: [{
          name: 'tcp-proxy',
          image: PROXY_IMAGE,
          imagePullPolicy: 'IfNotPresent',
          command: ['dotnet', '/proxy/Relay.dll'],
          args: [postgresHost],
          ports: [{ name: 'postgres', containerPort: POSTGRES_PORT, protocol: 'TCP' }],
          readinessProbe: {
            tcpSocket: { port: POSTGRES_PORT },
            initialDelaySeconds: 1,
            periodSeconds: 1,
            timeoutSeconds: 1,
            failureThreshold: 30,
          },
          volumeMounts: [{ name: 'proxy', mountPath: '/proxy', readOnly: true }],
          resources: {
            requests: { cpu: '25m', memory: '64Mi' },
            limits: { cpu: '100m', memory: '128Mi' },
          },
          securityContext: {
            allowPrivilegeEscalation: false,
            capabilities: { drop: ['ALL'] },
            readOnlyRootFilesystem: true,
          },
        }],
        volumes: [{
          name: 'proxy',
          configMap: { name: configMapName, defaultMode: 292 },
        }],
      },
    };
    assertCommand(execKubectl([
      '--kubeconfig', kubeconfig, 'create', '--filename', '-',
    ], { check: false, cwd: repoRoot, input: JSON.stringify(pod), timeout: 30_000 }),
    'Could not create the uniquely owned temporary PostgreSQL proxy Pod');
    const podReadback = ownedObject(execKubectl, kubeconfig, 'pod', podName, runId);
    if (!podReadback || podReadback.spec?.containers?.[0]?.image !== PROXY_IMAGE)
      throw new Error('Temporary PostgreSQL proxy Pod image readback differed from the pinned runtime.');

    assertCommand(execKubectl([
      '--kubeconfig', kubeconfig, 'wait', '--for=condition=Ready', `pod/${podName}`,
      '--namespace', NAMESPACE, '--timeout=180s',
    ], { check: false, timeout: 190_000 }), 'Temporary PostgreSQL proxy Pod did not become ready');
    await assertLoopbackPortAvailable(LOCAL_PORT);
    portForward = await startPortForwardDependency({
      kubeconfig, podName, localPort: LOCAL_PORT, namespace: NAMESPACE,
    });
    if (!Number.isSafeInteger(portForward.pid))
      throw new Error('PostgreSQL port-forward did not return its exact process ID.');

    const env = {
      ...process.env,
      ConnectionStrings__IdentityBrokerBootstrap:
        `Host=127.0.0.1;Port=${LOCAL_PORT};Database=postgres;SSL Mode=VerifyFull`,
      IdentityBroker__Bootstrap__PostgresHost: postgresHost,
      IdentityBroker__Bootstrap__AdminUsername: adminUsername,
      IdentityBroker__Bootstrap__DatabaseName: 'agentweaver',
      IdentityBroker__Bootstrap__RuntimeRole: `${resourceGroup}-id-identity-broker`,
      IdentityBroker__Bootstrap__RuntimePrincipalObjectId: runtimePrincipalObjectId,
      IdentityBroker__Bootstrap__MigrationRole: `${resourceGroup}-id-identity-broker-migration`,
      IdentityBroker__Bootstrap__MigrationPrincipalObjectId: migrationPrincipalObjectId,
      ...(foundationProbePrincipalObjectId
        ? { FoundationProbe__Bootstrap__PrincipalObjectId: foundationProbePrincipalObjectId } : {}),
    };
    const mode = verifyOnly ? 'verify' : 'bootstrap';
    const command = foundationProbePrincipalObjectId ? [
      'run', '--file', join(repoRoot, 'scripts', 'azure', 'lib', 'bootstrap-probe-postgres.cs'),
      '--verbosity', 'quiet', '--', '--execute',
    ] : [
      'run', '--project', join(repoRoot, BROKER_PROJECT), '--no-launch-profile', '--',
      verifyOnly ? '--verify-identity-postgres-bootstrap' : '--bootstrap-identity-postgres',
    ];
    const result = execDotnet(command, { check: false, cwd: repoRoot, env, timeout: 300_000 });
    const output = [result.stderr, result.stdout].filter(Boolean).join('\n').trim();
    const marker = foundationProbePrincipalObjectId
      ? 'FOUNDATION_PROBE_POSTGRES_BOOTSTRAP_OK database=agentweaver schema=foundation_probe' : SUCCESS_MARKERS[mode];
    if (result.status !== 0 || !result.stdout.includes(marker))
      throw new Error(`Identity PostgreSQL ${mode} failed: ${redact(output || `dotnet exited with status ${result.status}`)}`);
    const privilegeReadback = result.stdout.split(/\r?\n/)
      .find(line => line.startsWith(foundationProbePrincipalObjectId
        ? 'FOUNDATION_PROBE_POSTGRES_PRIVILEGES ' : 'IDENTITY_POSTGRES_PRIVILEGES '));
    if (!privilegeReadback)
      throw new Error('Identity PostgreSQL verification omitted the required privilege readback.');

    bootstrapReceipt = {
      database: 'agentweaver',
      schema: foundationProbePrincipalObjectId ? 'foundation_probe' : 'identity_broker',
      verification: verifyOnly ? 'read-only' : 'bootstrap',
      privilegeReadback,
      proxyPod: podName,
      proxyConfigMap: configMapName,
      portForwardPid: portForward.pid,
    };

    if (p1OwnershipHandover) {
      // Hand schema ownership to the P1 migration role through the same tunnel. The program is
      // idempotent and commits only after its own readback, so a rerun is safe.
      const handover = execDotnet([
        'run', '--file', join(repoRoot, 'scripts', 'azure', 'lib', 'p1-postgres-ownership-handover.cs'),
        '--verbosity', 'quiet', '--', '--execute',
      ], {
        check: false,
        cwd: repoRoot,
        timeout: 600_000,
        env: {
          ...process.env,
          P1Handover__PostgresHost: postgresHost,
          P1Handover__AdminUsername: adminUsername,
          P1Handover__MigrationRole: `${resourceGroup}-id-p1-postgres-migration`,
          P1Handover__MigrationPrincipalObjectId: p1OwnershipHandover.migrationPrincipalObjectId,
          P1Handover__LocalPort: String(LOCAL_PORT),
        },
      });
      const handoverLine = (handover.stdout ?? '').split(/\r?\n/)
        .find(line => line.startsWith(P1_OWNERSHIP_HANDOVER_MARKER));
      if (handover.status !== 0 || !handoverLine)
        throw new Error(`P1 ownership handover failed: ${diagnostic(handover)}`);
      bootstrapReceipt.p1OwnershipHandover = { verification: 'ownership-handover', readback: redact(handoverLine) };
    }

    if (p1Initializer) {
      // Run the initializer through the same tunnel used for the Identity Broker step.
      const p1Mode = p1Initializer.mode ?? 'initialize';
      const p1Result = execDotnet([
        'run', '--project', join(repoRoot, 'tools', 'P1PostgresInitializer', 'P1PostgresInitializer.csproj'),
        '--no-launch-profile', '--', ...p1InitializerArguments(p1Mode, p1Initializer),
      ], { check: false, cwd: repoRoot, timeout: 600_000 });
      const p1Output = [p1Result.stderr, p1Result.stdout].filter(Boolean).join('\n').trim();
      const p1Marker = P1_MARKERS[p1Mode];
      // Success needs both a zero exit code and this mode's marker. Error output is redacted before it is shown.
      if (p1Result.status !== 0 || !p1Result.stdout.includes(p1Marker))
        throw new Error(`P1 PostgreSQL ${p1Mode === 'initialize' ? 'initialization' : p1Mode} failed${p1Initializer.receiptPath ? `; inspect its stage receipt ${p1Initializer.receiptPath}` : ' without writing'}: ${redact(p1Output || `dotnet exited with status ${p1Result.status}`)}`);
      bootstrapReceipt.p1Initializer = {
        verification: P1_VERIFICATION[p1Mode],
        planSha256: p1Initializer.planSha256,
        ...(p1Initializer.receiptPath ? { receiptPath: p1Initializer.receiptPath } : {}),
        ...(p1Mode.startsWith('apply-pending') ? {
          maxCoreVersion: p1Initializer.maxCoreVersion,
          maxEventsVersion: p1Initializer.maxEventsVersion,
        } : {}),
        // Inspection and continuation readbacks hold role names, OIDs, grants, and versions; never credentials.
        ...(p1Mode === 'initialize' ? {} : {
          report: redact(p1Result.stdout.slice(0, p1Result.stdout.lastIndexOf(p1Marker)).trim()),
        }),
        runtimeIdentityProof: false,
        deploymentProof: false,
      };
    }
  } catch (error) {
    primaryError = error;
  }

  const cleanupErrors = [];
  if (portForward) {
    try {
      await portForward.stop();
      cleanup.portForwardStopped = true;
    } catch (error) {
      cleanupErrors.push(`port-forward PID ${portForward.pid}: ${error.message}`);
    }
  }
  if (clusterAccessReady) {
    for (const [kind, name, key] of [
      ['pod', podName, 'podRemoved'],
      ['configmap', configMapName, 'configMapRemoved'],
    ]) {
      try {
        cleanup[key] = removeOwnedObject(execKubectl, kubeconfig, kind, name, runId);
      } catch (error) {
        cleanupErrors.push(`${kind} ${name}: ${error.message}`);
      }
    }
  }
  try {
    rmSync(directory, { recursive: true, force: true });
    cleanup.kubeconfigRemoved = true;
    cleanup.proxyFilesRemoved = true;
  } catch (error) {
    cleanupErrors.push(`temporary local files: ${redact(error.message)}`);
  }

  if (primaryError && cleanupErrors.length)
    throw new Error(`${primaryError.message}; owned transport cleanup failed: ${cleanupErrors.join('; ')}`);
  if (primaryError) throw primaryError;
  if (cleanupErrors.length)
    throw new Error(`Identity PostgreSQL bootstrap transport cleanup failed: ${cleanupErrors.join('; ')}`);

  return { ...bootstrapReceipt, cleanup };
}
