import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { PassThrough } from 'node:stream';
import { dirname, join, resolve } from 'node:path';
import {
  bootstrapIdentityPostgres,
  startIdentityPostgresPortForward,
} from '../lib/identity-postgres-bootstrap.mjs';
import { p1PostgresPlanHash } from '../lib/p1-postgres-plan.mjs';

const options = {
  repoRoot: resolve('agentweaver-test-root'),
  resourceGroup: 'agentweaver',
  subscriptionId: '11111111-1111-4111-8111-111111111111',
  tenantId: '22222222-2222-4222-8222-222222222222',
  clusterName: 'agentweaver-aks',
  postgresHost: 'agentweaver-pg.postgres.database.azure.com',
  adminUsername: 'admin@example.test',
  runtimePrincipalObjectId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
  migrationPrincipalObjectId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb',
};
const privilegeReadback =
  'IDENTITY_POSTGRES_PRIVILEGES database=agentweaver schemaOwner=agentweaver-id-identity-broker-migration';
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

test('TCP proxy preserves PostgreSQL defaults and admits only the exact configured Key Vault target', () => {
  const source = readFileSync('tools/IdentityPostgresTcpProxy/Program.cs', 'utf8');
  assert.match(source, /const int postgresPort = 5432/);
  assert.match(source, /IdentityBroker__SecretRedemption__VaultUri/);
  assert.match(source, /string\.Equals\(args\[1\], configuredVaultHost, StringComparison\.OrdinalIgnoreCase\)/);
  assert.match(source, /GetConfiguredVaultHost/);
  assert.match(source, /EndsWith\("\.postgres\.database\.azure\.com"/);
  assert.match(source, /targetPort = keyVault \? 443 : postgresPort/);
  assert.match(source, /listenPort = keyVault \? 8443 : postgresPort/);
  assert.match(source, /ConnectAsync\(targetHost, targetPort\)/);
});

test('Key Vault loopback forwarding rejects wrong ports, namespaces, and implicit modes before spawning', () => {
  const options = { kubeconfig: 'owned', podName: 'owned', localPort: 15443,
    namespace: 'agentweaver', keyVault: true };
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
    namespace: 'agentweaver', keyVault: true,
  }, { spawnProcess: (_command, args) => { invocation = args; return child; } });
  child.stderr.write('Forwarding from 127.0.0.1:15443 -> 8443\n');
  const forward = await pending;
  assert.ok(invocation.includes('15443:8443'));
  assert.ok(invocation.includes('127.0.0.1'));
  assert.equal(forward.pid, 9877);
  await forward.stop();
  assert.equal(child.exitCode, 0);
});

function dependencies({
  dotnetResult, p1DotnetResult, alterPodOwner = false, assertTunnelActiveForP1 = false,
  authorization = () => ok('yes\n'),
} = {}) {
  const calls = [];
  const resources = new Map();
  const pathNames = [];
  const dotnetInvocations = [];
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
    if (action === 'auth') return authorization(args.slice(commandIndex + 1), calls);
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
    dotnetInvocations.push(dotnetInvocation);
    if (args.includes('--initialize') || args.includes('--inspect') || args.includes('--apply-pending-migrations')) {
      if (assertTunnelActiveForP1) assert.equal(portForwardStopped, false);
      const marker = args.includes('--inspect') ? 'P1_POSTGRES_INSPECTION_FINISHED writes=false'
        : args.includes('--dry-run') ? 'P1_POSTGRES_PENDING_MIGRATIONS_DRY_RUN writes=false'
          : args.includes('--apply-pending-migrations')
            ? 'P1_POSTGRES_PENDING_MIGRATIONS_APPLIED runtimeIdentityProof=false deploymentProof=false'
            : 'P1_POSTGRES_OPERATOR_INITIALIZATION_FINISHED runtimeIdentityProof=false deploymentProof=false';
      return p1DotnetResult ?? ok(`{"report":true}\n${marker}\n`);
    }
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
    dotnetInvocations,
    get dotnetInvocation() { return dotnetInvocation; },
    get portForwardOptions() { return portForwardOptions; },
    get portForwardStopped() { return portForwardStopped; },
    deps: { execAz, execKubelogin, execKubectl, buildProxy, execDotnet, startPortForward },
  };
}

test('Identity PostgreSQL bootstrap fails before proxy build or writes when access is denied', async () => {
  const fakes = dependencies({
    authorization: check => check[1] === 'create' && check[2] === 'pods' ? ok('no\n') : ok('yes\n'),
  });
  await assert.rejects(bootstrapIdentityPostgres(options, fakes.deps),
    /Kubernetes API access is unavailable or insufficient for Identity PostgreSQL bootstrap: required permission "create pods" in namespace agentweaver/);
  assert.equal(fakes.dotnetInvocation, undefined);
  assert.equal(fakes.resources.size, 0);
  assert.equal(fakes.portForwardOptions, undefined);
  assert.equal(fakes.calls.filter(call => call.kind === 'kubectl' &&
    call.args[call.args.indexOf('--kubeconfig') + 2] === 'create').length, 0);
});

test('Identity PostgreSQL bootstrap preflights the port-forward subresource before building or writing', async () => {
  const fakes = dependencies({
    authorization: check => check.includes('--subresource=portforward') ? ok('no\n') : ok('yes\n'),
  });
  await assert.rejects(bootstrapIdentityPostgres(options, fakes.deps),
    /required permission "create pods \(subresource portforward\)" in namespace agentweaver/);
  assert.equal(fakes.dotnetInvocation, undefined);
  assert.equal(fakes.resources.size, 0);
  assert.equal(fakes.portForwardOptions, undefined);
});

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
  assert.equal(fakes.portForwardOptions.namespace, 'agentweaver');

  const getCredentials = fakes.calls.find(call => call.kind === 'az' && call.args[0] === 'aks').args;
  assert.ok(!getCredentials.includes('--admin'));
  assert.equal(getCredentials[getCredentials.indexOf('--resource-group') + 1], options.resourceGroup);
  assert.equal(getCredentials[getCredentials.indexOf('--subscription') + 1], options.subscriptionId);
  const kubelogin = fakes.calls.find(call => call.kind === 'kubelogin').args;
  assert.deepEqual(kubelogin.slice(0, 3), ['convert-kubeconfig', '--login', 'azurecli']);

  const createCalls = fakes.calls.filter(call => call.kind === 'kubectl' &&
    call.args[call.args.indexOf('--kubeconfig') + 2] === 'create');
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

/** Both database stages use the same ready loopback forward, which stays open until P1 completes. */
test('combined Identity and P1 PostgreSQL bootstrap keeps the tunnel through P1 initialization', async () => {
  const fakes = dependencies({ assertTunnelActiveForP1: true });
  const p1Receipt = join(options.repoRoot, 'Temp', 'p1-postgres-123456789abc.jsonl');
  const receipt = await bootstrapIdentityPostgres({
    ...options,
    p1Initializer: {
      sourceSha: 'a'.repeat(40),
      coreMergeSha: 'b'.repeat(40),
      planSha256: p1PostgresPlanHash(),
      receiptPath: p1Receipt,
    },
  }, fakes.deps);

  assert.equal(fakes.dotnetInvocations.length, 2);
  assert.ok(fakes.dotnetInvocations[0].args.includes('--bootstrap-identity-postgres'));
  assert.deepEqual(fakes.dotnetInvocations[1].args.slice(-9), [
    '--initialize',
    '--source-sha', 'a'.repeat(40),
    '--core-merge-sha', 'b'.repeat(40),
    '--plan-sha256', p1PostgresPlanHash(),
    '--receipt', p1Receipt,
  ]);
  assert.equal(receipt.p1Initializer.receiptPath, p1Receipt);
  assert.equal(receipt.p1Initializer.planSha256, p1PostgresPlanHash());
  assert.equal(receipt.cleanup.portForwardStopped, true);
  assert.deepEqual(fakes.resources, new Map());
});

/** Initialized-database modes verify Identity read-only, then run the bounded P1 mode on the same tunnel. */
test('inspection and pending continuation never re-bootstrap Identity and pass explicit maxima', async () => {
  const sourceSha = 'a'.repeat(40);
  const coreMergeSha = 'b'.repeat(40);
  const planSha256 = p1PostgresPlanHash();
  const receiptPath = join(options.repoRoot, 'Temp', 'p1-postgres-pending.jsonl');
  const cases = [
    [{ mode: 'inspect', sourceSha, planSha256 },
      ['--inspect', '--source-sha', sourceSha, '--plan-sha256', planSha256], 'read-only-inspection'],
    [{ mode: 'apply-pending-dry-run', sourceSha, coreMergeSha, planSha256, maxCoreVersion: 23, maxEventsVersion: 9 },
      ['--apply-pending-migrations', '--source-sha', sourceSha, '--core-merge-sha', coreMergeSha,
        '--plan-sha256', planSha256, '--max-core-version', '23', '--max-events-version', '9', '--dry-run'],
      'read-only-pending-migration-dry-run'],
    [{ mode: 'apply-pending', sourceSha, coreMergeSha, planSha256, maxCoreVersion: 23, maxEventsVersion: 9, receiptPath },
      ['--apply-pending-migrations', '--source-sha', sourceSha, '--core-merge-sha', coreMergeSha,
        '--plan-sha256', planSha256, '--max-core-version', '23', '--max-events-version', '9', '--receipt', receiptPath],
      'pending-migrations-applied'],
  ];
  for (const [p1Initializer, expected, verification] of cases) {
    const fakes = dependencies({ assertTunnelActiveForP1: true });
    const receipt = await bootstrapIdentityPostgres({ ...options, verifyOnly: true, p1Initializer }, fakes.deps);
    assert.equal(fakes.dotnetInvocations.length, 2);
    assert.ok(fakes.dotnetInvocations[0].args.includes('--verify-identity-postgres-bootstrap'));
    assert.ok(!fakes.dotnetInvocations.some(call => call.args.includes('--bootstrap-identity-postgres')));
    assert.deepEqual(fakes.dotnetInvocations[1].args.slice(-expected.length), expected);
    assert.equal(receipt.p1Initializer.verification, verification);
    assert.equal(receipt.p1Initializer.report, '{"report":true}');
    assert.deepEqual(fakes.resources, new Map());
  }
});

test('initialized-database modes reject Identity bootstrap, missing maxima, and stray receipts before transport', async () => {
  const base = { sourceSha: 'a'.repeat(40), coreMergeSha: 'b'.repeat(40), planSha256: p1PostgresPlanHash() };
  const invalid = [
    [false, { ...base, mode: 'apply-pending-dry-run', maxCoreVersion: 23, maxEventsVersion: 9 }],
    [true, { ...base, mode: 'apply-pending-dry-run', maxCoreVersion: 23 }],
    [true, { ...base, mode: 'apply-pending-dry-run', maxCoreVersion: 23, maxEventsVersion: 0 }],
    [true, { ...base, mode: 'apply-pending', maxCoreVersion: 23, maxEventsVersion: 9 }],
    [true, { ...base, mode: 'inspect' }],
    [true, { sourceSha: base.sourceSha, planSha256: base.planSha256, mode: 'inspect',
      receiptPath: join(options.repoRoot, 'Temp', 'p1-inspect.jsonl') }],
    [true, { ...base, mode: 'reset' }],
  ];
  for (const [verifyOnly, p1Initializer] of invalid) {
    const fakes = dependencies();
    await assert.rejects(bootstrapIdentityPostgres({ ...options, verifyOnly, p1Initializer }, fakes.deps),
      /Combined PostgreSQL initialization requires|The P1 initializer mode must be/);
    assert.equal(fakes.calls.length, 0);
  }
});

test('combined PostgreSQL bootstrap rejects a plan hash that differs from the local reviewed plan before transport setup', async () => {
  const fakes = dependencies();
  await assert.rejects(bootstrapIdentityPostgres({
    ...options,
    p1Initializer: {
      sourceSha: 'a'.repeat(40),
      coreMergeSha: 'b'.repeat(40),
      planSha256: 'c'.repeat(64),
      receiptPath: join(options.repoRoot, 'Temp', 'p1-postgres-123456789abc.jsonl'),
    },
  }, fakes.deps), /Combined PostgreSQL initialization requires exact lowercase source hashes/);
  assert.equal(fakes.calls.length, 0);
  assert.equal(fakes.resources.size, 0);
  assert.equal(fakes.portForwardOptions, undefined);
});

/** P1 failures remain visible and still remove the exact temporary transport resources. */
test('combined PostgreSQL bootstrap reports P1 failure after cleanup', async () => {
  const fakes = dependencies({
    p1DotnetResult: { status: 1, stdout: '', stderr: 'P1 initialization failed' },
  });
  await assert.rejects(bootstrapIdentityPostgres({
    ...options,
    p1Initializer: {
      sourceSha: 'a'.repeat(40),
      coreMergeSha: 'b'.repeat(40),
      planSha256: p1PostgresPlanHash(),
      receiptPath: join(options.repoRoot, 'Temp', 'p1-postgres-123456789abc.jsonl'),
    },
  }, fakes.deps), /P1 PostgreSQL initialization failed; inspect its stage receipt/);
  assert.equal(fakes.portForwardStopped, true);
  assert.deepEqual(fakes.resources, new Map());
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
    namespace: 'agentweaver',
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

/** The ownership handover runs on the same owned tunnel after a read-only Identity check. */
test('P1 ownership handover verifies Identity read-only, runs on the tunnel, and records the readback', async () => {
  const fakes = dependencies();
  const handoverPrincipal = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
  const base = fakes.deps.execDotnet;
  let handoverCall;
  const execDotnet = (args, settings) => {
    if (!args.some(arg => String(arg).endsWith('p1-postgres-ownership-handover.cs'))) return base(args, settings);
    assert.equal(fakes.portForwardStopped, false);
    handoverCall = { args, settings };
    return ok('P1_POSTGRES_OWNERSHIP_HANDOVER_VERIFIED role=agentweaver-id-p1-postgres-migration schemas=environment,orchestrator,events_sessions changed=3\n');
  };
  const receipt = await bootstrapIdentityPostgres({
    ...options, verifyOnly: true, p1OwnershipHandover: { migrationPrincipalObjectId: handoverPrincipal },
  }, { ...fakes.deps, execDotnet });
  assert.ok(fakes.dotnetInvocations[0].args.includes('--verify-identity-postgres-bootstrap'));
  assert.deepEqual(handoverCall.args.slice(-2), ['--', '--execute']);
  assert.equal(handoverCall.settings.env.P1Handover__MigrationRole, 'agentweaver-id-p1-postgres-migration');
  assert.equal(handoverCall.settings.env.P1Handover__MigrationPrincipalObjectId, handoverPrincipal);
  assert.equal(handoverCall.settings.env.P1Handover__LocalPort, '15432');
  assert.match(receipt.p1OwnershipHandover.readback, /changed=3$/);
  assert.equal(receipt.cleanup.podRemoved, true);
  assert.deepEqual(fakes.resources, new Map());
});

test('P1 ownership handover fails closed without its marker and still removes the relay', async () => {
  const fakes = dependencies();
  const base = fakes.deps.execDotnet;
  const execDotnet = (args, settings) => args.some(arg => String(arg).endsWith('p1-postgres-ownership-handover.cs'))
    ? { status: 1, stdout: '', stderr: 'permission denied to grant role' } : base(args, settings);
  await assert.rejects(bootstrapIdentityPostgres({
    ...options, verifyOnly: true,
    p1OwnershipHandover: { migrationPrincipalObjectId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' },
  }, { ...fakes.deps, execDotnet }), /P1 ownership handover failed: permission denied/);
  assert.deepEqual(fakes.resources, new Map());
  assert.equal(fakes.portForwardStopped, true);
});

test('P1 ownership handover rejects a write bootstrap, a combined initializer, or a reused principal', async () => {
  const handover = { migrationPrincipalObjectId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc' };
  for (const invalid of [
    { p1OwnershipHandover: handover },
    { verifyOnly: true, p1OwnershipHandover: { migrationPrincipalObjectId: options.migrationPrincipalObjectId } },
    { verifyOnly: true, p1OwnershipHandover: { migrationPrincipalObjectId: 'not-a-guid' } },
    { verifyOnly: true, p1OwnershipHandover: handover, p1Initializer: { mode: 'inspect' } },
  ]) {
    const fakes = dependencies();
    await assert.rejects(bootstrapIdentityPostgres({ ...options, ...invalid }, fakes.deps),
      /P1 ownership handover requires Identity verification only/);
    assert.equal(fakes.calls.length, 0);
  }
});

test('P1 ownership handover program is transactional, idempotent, and reads back before commit', () => {
  const source = readFileSync('scripts/azure/lib/p1-postgres-ownership-handover.cs', 'utf8');
  assert.match(source, /ExpectedRole = "agentweaver-id-p1-postgres-migration"/);
  assert.match(source, /pgaadauth_create_principal_with_oid\(@role, @principal, 'service', false, false\)/);
  assert.match(source, /BeginTransactionAsync[\s\S]*GRANT \{quotedRole\} TO CURRENT_USER WITH INHERIT FALSE, SET TRUE[\s\S]*REVOKE SET OPTION FOR \{quotedRole\} FROM CURRENT_USER[\s\S]*CommitAsync/);
  assert.match(source, /string\[\] schemas = \["environment", "orchestrator", "events_sessions"\]/);
  assert.match(source, /count\(\*\) = 3 FROM pg_namespace/);
  assert.match(source, /never creates or initializes them/);
  assert.doesNotMatch(source, /\b(DROP|TRUNCATE|DELETE FROM)\b/);
});