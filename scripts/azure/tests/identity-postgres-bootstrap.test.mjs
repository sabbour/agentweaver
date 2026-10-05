import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { PassThrough } from 'node:stream';
import { dirname, join } from 'node:path';
import {
  bootstrapIdentityPostgres,
  startIdentityPostgresPortForward,
} from '../lib/identity-postgres-bootstrap.mjs';

const options = {
  repoRoot: 'C:\\agentweaver',
  resourceGroup: 'aw-v1-p0',
  subscriptionId: '11111111-1111-4111-8111-111111111111',
  tenantId: '22222222-2222-4222-8222-222222222222',
  clusterName: 'aw-v1-p0-aks',
  postgresHost: 'aw-v1-p0-pg.postgres.database.azure.com',
  adminUsername: 'admin@example.test',
  runtimePrincipalObjectId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  migrationPrincipalObjectId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
};
const privilegeReadback =
  'IDENTITY_POSTGRES_PRIVILEGES database=agentweaver schemaOwner=aw-v1-p0-id-identity-broker-migration';
const ok = stdout => ({ status: 0, stdout: stdout ?? '', stderr: '' });

test('Probe principal registration and validation use postgres before the transactional agentweaver fixture', () => {
  const source = readFileSync('scripts/azure/lib/bootstrap-probe-postgres.cs', 'utf8');
  assert.match(source, /principalSettings[\s\S]*Database = "postgres"/);
  assert.match(source, /current_database\(\) = 'postgres'[\s\S]*principalConnection/);
  assert.match(source, /pgaadauth_list_principals\(false\)[\s\S]*principalConnection/);
  assert.match(source, /pgaadauth_create_principal_with_oid[\s\S]*principalConnection, principalTransaction/);
  assert.match(source, /principalTransaction\.CommitAsync/);
  assert.match(source, /schemaExists && !roleExists/);
  assert.match(source, /Existing Probe role does not map to the exact approved principal/);
  assert.match(source, /NOT rolcreatedb AND NOT rolcreaterole[\s\S]*NOT rolreplication AND NOT rolinherit/);
  assert.match(source, /if \(!schemaExists\)[\s\S]*CREATE SCHEMA foundation_probe/);
});

test('TCP proxy preserves PostgreSQL defaults and admits only the explicit fixed Key Vault target', () => {
  const source = readFileSync('tools/IdentityPostgresTcpProxy/Program.cs', 'utf8');
  assert.match(source, /const int postgresPort = 5432/);
  assert.match(source, /const string vaultHost = "aw-v1-p0-kv\.vault\.azure\.net"/);
  assert.match(source, /args\.Length == 2 && args\[0\] == "--key-vault" && args\[1\] == vaultHost/);
  assert.match(source, /EndsWith\("\.postgres\.database\.azure\.com"/);
  assert.match(source, /targetPort = keyVault \? 443 : postgresPort/);
  assert.match(source, /listenPort = keyVault \? 8443 : postgresPort/);
  assert.match(source, /ConnectAsync\(targetHost, targetPort\)/);
});

test('Key Vault loopback forwarding rejects wrong ports, namespaces, and implicit modes before spawning', () => {
  const options = { kubeconfig: 'owned', podName: 'owned', localPort: 15443,
    namespace: 'agentweaver-v1-p0', keyVault: true };
  const deps = { spawnProcess() { assert.fail('Invalid route must not spawn.'); } };
  for (const invalid of [
    { localPort: 15432 }, { localPort: '15443' }, { namespace: 'foreign' },
    { keyVault: 'true' }, { keyVault: null },
  ]) assert.throws(() => startIdentityPostgresPortForward({ ...options, ...invalid }, deps), /Key Vault/);
});

test('Key Vault loopback forwarding uses only 15443:8443 and stops its owned process', async () => {
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  child.pid = 9877;
  child.exitCode = null;
  child.signalCode = null;
  child.kill = () => { child.exitCode = 0; child.emit('exit', 0); return true; };
  let invocation;
  const pending = startIdentityPostgresPortForward({
    kubeconfig: 'owned', podName: 'owned-kv', localPort: 15443,
    namespace: 'agentweaver-v1-p0', keyVault: true,
  }, { spawnProcess: (_command, args) => { invocation = args; return child; } });
  child.stderr.write('Forwarding from 127.0.0.1:15443 -> 8443\n');
  const forward = await pending;
  assert.ok(invocation.includes('15443:8443'));
  assert.ok(invocation.includes('127.0.0.1'));
  assert.equal(forward.pid, 9877);
  await forward.stop();
  assert.equal(child.exitCode, 0);
});

function dependencies({ dotnetResult, alterPodOwner = false } = {}) {
  const calls = [];
  const resources = new Map();
  const pathNames = [];
  let portForwardStopped = false;
  let dotnetInvocation;
  let portForwardOptions;
  const execAz = (args, settings) => {
    calls.push({ kind: 'az', args, settings });
    if (args[0] === 'account') return ok(JSON.stringify({
      id: options.subscriptionId,
      tenantId: options.tenantId,
      state: 'Enabled',
    }));
    if (args[0] === 'aks' && args[1] === 'get-credentials') {
      const path = args[args.indexOf('--file') + 1];
      pathNames.push(path);
      writeFileSync(path, 'temporary normal-user kubeconfig');
      return ok();
    }
    throw new Error(`Unexpected az command: ${args.join(' ')}`);
  };
  const execKubelogin = (args, settings) => {
    calls.push({ kind: 'kubelogin', args, settings });
    return ok();
  };
  const execKubectl = (args, settings = {}) => {
    calls.push({ kind: 'kubectl', args, settings });
    const commandIndex = args.indexOf('--kubeconfig') + 2;
    const action = args[commandIndex];
    if (action === 'create') {
      const object = JSON.parse(settings.input);
      resources.set(`${object.kind.toLowerCase()}/${object.metadata.name}`, object);
      return ok(`${object.kind.toLowerCase()}/${object.metadata.name} created`);
    }
    if (action === 'wait') return ok('condition met');
    const kind = args[commandIndex + 1];
    const name = args[commandIndex + 2];
    const key = `${kind}/${name}`;
    if (action === 'get') {
      const object = resources.get(key);
      return object
        ? ok(JSON.stringify(object))
        : { status: 1, stdout: '', stderr: `Error from server (NotFound): ${key} not found` };
    }
    if (action === 'delete') {
      resources.delete(key);
      return ok(`${key} deleted`);
    }
    throw new Error(`Unexpected kubectl command: ${args.join(' ')}`);
  };
  const buildProxy = (_root, outputDirectory) => {
    mkdirSync(outputDirectory, { recursive: true });
    for (const file of ['Relay.dll', 'Relay.deps.json', 'Relay.runtimeconfig.json'])
      writeFileSync(join(outputDirectory, file), file);
  };
  const execDotnet = (args, settings) => {
    dotnetInvocation = { args, settings };
    if (alterPodOwner) {
      const pod = [...resources.values()].find(value => value.kind === 'Pod');
      pod.metadata.labels['agentweaver.io/task-owner'] = 'different-task';
    }
    const marker = args.includes('--verify-identity-postgres-bootstrap')
      ? 'IDENTITY_POSTGRES_BOOTSTRAP_VERIFIED'
      : 'IDENTITY_POSTGRES_BOOTSTRAP_OK';
    return dotnetResult ?? ok(`${privilegeReadback}\n${marker} database=agentweaver schema=identity_broker\n`);
  };
  const startPortForward = value => {
    portForwardOptions = value;
    return Promise.resolve({
      pid: 12345,
      stop: async () => { portForwardStopped = true; },
    });
  };
  return {
    calls,
    resources,
    pathNames,
    get dotnetInvocation() { return dotnetInvocation; },
    get portForwardOptions() { return portForwardOptions; },
    get portForwardStopped() { return portForwardStopped; },
    deps: { execAz, execKubelogin, execKubectl, buildProxy, execDotnet, startPortForward },
  };
}

test('bootstraps through the owned pod and loopback tunnel with the Entra token kept local', async () => {
  const fakes = dependencies();
  const receipt = await bootstrapIdentityPostgres(options, fakes.deps);

  assert.equal(receipt.database, 'agentweaver');
  assert.equal(receipt.schema, 'identity_broker');
  assert.match(receipt.privilegeReadback, /IDENTITY_POSTGRES_PRIVILEGES/);
  assert.equal(receipt.portForwardPid, 12345);
  assert.deepEqual(receipt.cleanup, {
    portForwardStopped: true,
    podRemoved: true,
    configMapRemoved: true,
    kubeconfigRemoved: true,
    proxyFilesRemoved: true,
  });
  assert.deepEqual(fakes.resources, new Map());
  assert.equal(fakes.portForwardStopped, true);
  assert.equal(fakes.portForwardOptions.localPort, 15432);
  assert.equal(fakes.portForwardOptions.namespace, 'agentweaver-v1-p0');

  const getCredentials = fakes.calls.find(call => call.kind === 'az' && call.args[0] === 'aks').args;
  assert.ok(!getCredentials.includes('--admin'));
  assert.equal(getCredentials[getCredentials.indexOf('--resource-group') + 1], options.resourceGroup);
  assert.equal(getCredentials[getCredentials.indexOf('--subscription') + 1], options.subscriptionId);
  const kubelogin = fakes.calls.find(call => call.kind === 'kubelogin').args;
  assert.deepEqual(kubelogin.slice(0, 3), ['convert-kubeconfig', '--login', 'azurecli']);

  const createCalls = fakes.calls.filter(call => call.kind === 'kubectl' && call.args.includes('create'));
  assert.equal(createCalls.length, 2);
  const configMap = JSON.parse(createCalls[0].settings.input);
  const pod = JSON.parse(createCalls[1].settings.input);
  assert.equal(configMap.kind, 'ConfigMap');
  assert.equal(configMap.metadata.labels['agentweaver.io/task-owner'], 'identity-postgres-bootstrap');
  assert.equal(configMap.metadata.labels['agentweaver.io/task-run'], pod.metadata.labels['agentweaver.io/task-run']);
  assert.deepEqual(Object.keys(configMap.binaryData).sort(),
    ['Relay.deps.json', 'Relay.dll', 'Relay.runtimeconfig.json'].sort());
  assert.equal(pod.spec.automountServiceAccountToken, false);
  assert.equal(pod.spec.containers[0].args[0], options.postgresHost);
  assert.equal(pod.spec.containers[0].ports[0].containerPort, 5432);
  assert.equal(pod.spec.containers[0].readinessProbe.tcpSocket.port, 5432);
  assert.equal(pod.spec.containers[0].image,
    'mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4');

  const forwardArgs = fakes.calls.find(call =>
    call.kind === 'kubectl' && call.args.includes('delete'))?.args;
  assert.ok(forwardArgs);
  const dotnet = fakes.dotnetInvocation;
  assert.ok(dotnet.args.includes('--bootstrap-identity-postgres'));
  assert.equal(dotnet.settings.env.ConnectionStrings__IdentityBrokerBootstrap,
    'Host=127.0.0.1;Port=15432;Database=postgres;SSL Mode=VerifyFull');
  assert.equal(dotnet.settings.env.IdentityBroker__Bootstrap__PostgresHost, options.postgresHost);
  assert.ok(!dotnet.settings.env.ConnectionStrings__IdentityBrokerBootstrap.includes('Password='));
  assert.ok(!dotnet.settings.env.IdentityBroker__Bootstrap__PostgresHost.includes('token'));
  assert.equal(existsSync(dirname(fakes.pathNames[0])), false);
});

test('read-only verification uses only the verify command and cleans its exact transport', async () => {
  const fakes = dependencies();
  const receipt = await bootstrapIdentityPostgres({ ...options, verifyOnly: true }, fakes.deps);

  assert.equal(receipt.verification, 'read-only');
  assert.ok(fakes.dotnetInvocation.args.includes('--verify-identity-postgres-bootstrap'));
  assert.ok(!fakes.dotnetInvocation.args.includes('--bootstrap-identity-postgres'));
  assert.deepEqual(fakes.resources, new Map());
  assert.equal(receipt.cleanup.portForwardStopped, true);
  assert.equal(receipt.cleanup.podRemoved, true);
  assert.equal(receipt.cleanup.configMapRemoved, true);
});

test('Probe bootstrap reuses the owned transport without executing the Broker bootstrap', async () => {
  const principal = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
  const fakes = dependencies({
    dotnetResult: ok(
      'FOUNDATION_PROBE_POSTGRES_PRIVILEGES database=agentweaver schema=foundation_probe\n' +
      'FOUNDATION_PROBE_POSTGRES_BOOTSTRAP_OK database=agentweaver schema=foundation_probe\n'),
  });
  const receipt = await bootstrapIdentityPostgres({
    ...options, foundationProbePrincipalObjectId: principal,
  }, fakes.deps);

  assert.equal(receipt.database, 'agentweaver');
  assert.equal(receipt.schema, 'foundation_probe');
  assert.equal(receipt.verification, 'bootstrap');
  assert.match(receipt.privilegeReadback, /^FOUNDATION_PROBE_POSTGRES_PRIVILEGES /);
  assert.deepEqual(receipt.cleanup, {
    portForwardStopped: true,
    podRemoved: true,
    configMapRemoved: true,
    kubeconfigRemoved: true,
    proxyFilesRemoved: true,
  });
  assert.deepEqual(fakes.resources, new Map());
  const { args, settings } = fakes.dotnetInvocation;
  assert.ok(args.includes('--file'));
  assert.ok(args.some(value => /bootstrap-probe-postgres\.cs$/.test(value)));
  assert.ok(args.includes('--execute'));
  assert.ok(!args.includes('--bootstrap-identity-postgres'));
  assert.ok(!args.includes('--verify-identity-postgres-bootstrap'));
  assert.equal(settings.env.FoundationProbe__Bootstrap__PrincipalObjectId, principal);
  assert.equal(settings.env.ConnectionStrings__IdentityBrokerBootstrap,
    'Host=127.0.0.1;Port=15432;Database=postgres;SSL Mode=VerifyFull');
  assert.equal(settings.env.IdentityBroker__Bootstrap__PostgresHost, options.postgresHost);
  assert.equal(fakes.portForwardOptions.localPort, 15432);
  assert.equal(fakes.portForwardStopped, true);
  assert.equal(existsSync(dirname(fakes.pathNames[0])), false);
});

test('Probe bootstrap rejects reused principals and Identity verification before creating transport', async () => {
  for (const input of [
    { foundationProbePrincipalObjectId: options.runtimePrincipalObjectId.toUpperCase() },
    { foundationProbePrincipalObjectId: options.migrationPrincipalObjectId },
    { foundationProbePrincipalObjectId: 'not-a-principal' },
    { foundationProbePrincipalObjectId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', verifyOnly: true },
  ]) {
    const fakes = dependencies();
    await assert.rejects(bootstrapIdentityPostgres({ ...options, ...input }, fakes.deps),
      /distinct exact native principal/);
    assert.equal(fakes.calls.length, 0);
    assert.equal(fakes.dotnetInvocation, undefined);
  }
});

test('Probe bootstrap rejects missing native privilege evidence and cleans transport', async () => {
  const fakes = dependencies({
    dotnetResult: ok('FOUNDATION_PROBE_POSTGRES_BOOTSTRAP_OK database=agentweaver schema=foundation_probe\n'),
  });
  await assert.rejects(bootstrapIdentityPostgres({
    ...options, foundationProbePrincipalObjectId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc',
  }, fakes.deps), /omitted the required privilege readback/);
  assert.deepEqual(fakes.resources, new Map());
  assert.equal(fakes.portForwardStopped, true);
});

test('Probe operator fixture reuses the canonical domain and embedded persistence migrations with DML-only grants', () => {
  const bootstrap = readFileSync(new URL('../lib/bootstrap-probe-postgres.cs', import.meta.url), 'utf8');
  assert.ok(bootstrap.includes('"tools", "Agentweaver.FoundationProbe", "schema", "001_probe_effects.sql"'));
  assert.ok(bootstrap.includes('"001_outbox.sql", "002_consumer_inbox.sql"'));
  assert.ok(bootstrap.includes('SslMode = SslMode.VerifyFull'));
  assert.ok(bootstrap.includes('options.TargetHost = host'));
  assert.ok(bootstrap.includes('pgaadauth_create_principal_with_oid'));
  assert.ok(bootstrap.includes("AND principaltype = 'service' AND isadmin = 0"));
  assert.ok(bootstrap.includes("array_agg(version ORDER BY version) = ARRAY[1, 2]"));
  assert.ok(bootstrap.includes("NOT has_schema_privilege('foundation_probe_runtime', 'identity_broker', 'USAGE')"));
  assert.ok(!bootstrap.includes('CREATE TABLE foundation_probe.probe_effects'));
  assert.ok(!bootstrap.includes('DefaultAzureCredential'));
  const domain = readFileSync(new URL('../../../tools/Agentweaver.FoundationProbe/schema/001_probe_effects.sql',
    import.meta.url), 'utf8');
  for (const column of ['effect_id uuid', 'nonce char(32)', 'source_sha char(40)', 'source_tree char(40)', 'runtime_role name']) {
    assert.ok(domain.includes(column), `canonical domain column ${column}`);
  }
  assert.ok(domain.includes('GRANT SELECT, INSERT'));
  assert.ok(!bootstrap.includes('await Execute(effectsSql);'));
  assert.ok(bootstrap.includes('transactionStatements.Count != 2'));
  assert.ok(bootstrap.includes('await Execute(Regex.Replace(effectsSql'));
  const envelope = domain.match(/^(BEGIN|COMMIT);\r?$/gm);
  assert.deepEqual(envelope?.map(line => line.trim()), ['BEGIN;', 'COMMIT;']);
  const body = domain.replace(/^(BEGIN|COMMIT);\r?$/gm, '');
  assert.ok(!/^\s*(BEGIN|COMMIT|ROLLBACK)\s*;/im.test(body));
  for (const file of ['001_outbox.sql', '002_consumer_inbox.sql']) {
    const embedded = readFileSync(new URL(
      `../../../packages/Agentweaver.Persistence.Postgres/Migrations/${file}`, import.meta.url), 'utf8');
    assert.ok(!/^\s*(BEGIN|COMMIT|ROLLBACK)\s*;/im.test(embedded));
  }
});

test('reports bootstrap failure after stopping the tunnel and cleaning only run-owned resources', async () => {
  const fakes = dependencies({
    dotnetResult: { status: 1, stdout: '', stderr: 'PostgreSQL connection refused' },
  });

  await assert.rejects(
    bootstrapIdentityPostgres(options, fakes.deps),
    /Identity PostgreSQL bootstrap failed: PostgreSQL connection refused/,
  );
  assert.equal(fakes.portForwardStopped, true);
  assert.deepEqual(fakes.resources, new Map());
});

test('does not delete a temporary pod after its owner label changes', async () => {
  const fakes = dependencies({
    dotnetResult: { status: 1, stdout: '', stderr: 'verification failed' },
    alterPodOwner: true,
  });

  await assert.rejects(
    bootstrapIdentityPostgres(options, fakes.deps),
    /owned transport cleanup failed: pod .* ownership readback did not match/,
  );
  assert.equal(fakes.portForwardStopped, true);
  assert.equal([...fakes.resources.values()].filter(value => value.kind === 'Pod').length, 1);
  assert.equal([...fakes.resources.values()].filter(value => value.kind === 'ConfigMap').length, 0);
});

test('port-forward binds only the local loopback interface and exact PostgreSQL pod port', async () => {
  const child = new EventEmitter();
  child.pid = 9876;
  child.exitCode = null;
  child.signalCode = null;
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  const calls = [];
  child.kill = () => {
    child.exitCode = 0;
    return true;
  };

  const forward = startIdentityPostgresPortForward({
    kubeconfig: 'C:\\temp\\kubeconfig',
    podName: 'identity-pg-123456789abc-proxy',
    localPort: 15432,
    namespace: 'agentweaver-v1-p0',
  }, {
    spawnProcess: (command, args, settings) => {
      calls.push({ command, args, settings });
      return child;
    },
  });
  child.stderr.write('Forwarding from 127.0.0.1:15432 -> 5432\n');
  const process = await forward;
  assert.equal(process.pid, 9876);
  assert.deepEqual(calls[0].args.slice(2, 6), [
    'port-forward', '--address', '127.0.0.1', 'pod/identity-pg-123456789abc-proxy',
  ]);
  assert.ok(calls[0].args.includes('15432:5432'));
  await process.stop();
  assert.equal(child.exitCode, 0);
});
