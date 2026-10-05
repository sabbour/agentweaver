import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { run, runAz, redact } from './exec.mjs';

const GUID_PATTERN = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const NAMESPACE = 'agentweaver-v1-p0';
const OWNER_LABEL = 'agentweaver.io/task-owner';
const RUN_LABEL = 'agentweaver.io/task-run';
const OWNER = 'identity-postgres-bootstrap';
const LOCAL_PORT = 15432;
const POSTGRES_PORT = 5432;
const BROKER_IMAGE =
  'ghcr.io/sabbour/agentweaver.identity.broker@sha256:c3758e2be89891728dcdb7c0f7704dc2b77737f846a42f0d58191848a31537f1';
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

export function startIdentityPostgresPortForward({ kubeconfig, podName, localPort, namespace }, {
  spawnProcess = spawn,
  startupTimeoutMs = 30_000,
} = {}) {
  const child = spawnProcess('kubectl', [
    '--kubeconfig', kubeconfig,
    'port-forward',
    '--address', '127.0.0.1',
    `pod/${podName}`,
    `${localPort}:${POSTGRES_PORT}`,
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
      if (!settled && output.includes(`Forwarding from 127.0.0.1:${localPort} -> ${POSTGRES_PORT}`)) {
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
    '--output', outputDirectory, '--no-self-contained',
  ], { check: false, cwd: repoRoot, timeout: 180_000 });
  assertCommand(result, 'Could not build the temporary PostgreSQL TCP proxy');
}

export async function bootstrapIdentityPostgres({
  repoRoot = process.cwd(), resourceGroup, subscriptionId, tenantId, clusterName,
  postgresHost, adminUsername, runtimePrincipalObjectId, migrationPrincipalObjectId,
  verifyOnly = false,
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
          image: BROKER_IMAGE,
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
    if (!podReadback || podReadback.spec?.containers?.[0]?.image !== BROKER_IMAGE)
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
    };
    const mode = verifyOnly ? 'verify' : 'bootstrap';
    const result = execDotnet([
      'run', '--project', join(repoRoot, BROKER_PROJECT), '--no-launch-profile', '--',
      verifyOnly ? '--verify-identity-postgres-bootstrap' : '--bootstrap-identity-postgres',
    ], { check: false, cwd: repoRoot, env, timeout: 300_000 });
    const output = [result.stderr, result.stdout].filter(Boolean).join('\n').trim();
    if (result.status !== 0 || !result.stdout.includes(SUCCESS_MARKERS[mode]))
      throw new Error(`Identity PostgreSQL ${mode} failed: ${redact(output || `dotnet exited with status ${result.status}`)}`);
    const privilegeReadback = result.stdout.split(/\r?\n/)
      .find(line => line.startsWith('IDENTITY_POSTGRES_PRIVILEGES '));
    if (!privilegeReadback)
      throw new Error('Identity PostgreSQL verification omitted the required privilege readback.');

    bootstrapReceipt = {
      database: 'agentweaver',
      schema: 'identity_broker',
      verification: verifyOnly ? 'read-only' : 'bootstrap',
      privilegeReadback,
      proxyPod: podName,
      proxyConfigMap: configMapName,
      portForwardPid: portForward.pid,
    };
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
