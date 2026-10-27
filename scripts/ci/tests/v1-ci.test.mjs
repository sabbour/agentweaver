import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import { classify } from '../v1-changes.mjs';
import { brokerShardCount, environmentP1RequiredMethods, loadBrokerWeights, measureWeights, partitionMethods, testMatrix, verifyRequiredMethods, verifyTrx } from '../v1-test-plan.mjs';
import { suites } from '../../coverage/dotnet.mjs';

const workflow = readFileSync(new URL('../../../.github/workflows/v1-ci.yml', import.meta.url), 'utf8');

test('metadata-only and unrelated workflows skip expensive validation; unknown inputs fail safe', () => {
  assert.deepEqual(classify(['docs/guide/testing.md', 'README.md']), { dotnet: false, web: false, node: false, images: false });
  assert.deepEqual(classify(['.github/workflows/other.yml']), classify([]));
  for (const path of ['.github/workflows/v1-ci.yml', 'scripts/ci/v1-changes.mjs', 'package-lock.json', 'new-input']) {
    assert.ok(Object.values(classify([path])).every(Boolean), path);
  }
  assert.ok(Object.values(classify([], true)).every(Boolean));
});

test('source, lockfile, Docker, browser and harness changes select all dependent validation', () => {
  for (const path of ['services/gateway/File.cs', 'tests/Broker/Test.cs', 'Directory.Build.props',
    'global.json', 'NuGet.Config', 'packages/Library/packages.lock.json',
    'tools/P1PostgresInitializer/packages.lock.json', 'deploy/k8s/test.yaml']) {
    const result = classify([path]);
    assert.equal(result.dotnet, true, path);
    assert.equal(result.web, true, path);
    assert.equal(result.images, true, path);
  }
  assert.equal(classify(['apps/web/src/v1/test.ts']).dotnet, true);
  assert.equal(classify(['apps/web/src/v1/test.ts']).node, true);
  assert.equal(classify(['services/agenthost/Agentweaver.AgentHost/BuildTestOutputCollectorProgram.cs']).node, true);
  assert.equal(classify(['Directory.Build.props']).node, true);
  assert.equal(classify(['scripts/harness-shared/run-p1.mjs']).node, true);
  assert.equal(classify(['scripts/release/validate.mjs']).images, true);
  assert.equal(classify(['deploy/k8s/profiles/events-and-sessions/render.mjs']).node, true);
  assert.equal(classify(['scripts/release/validate.mjs']).web, true);
});

test('matrix includes every suite and exactly four unique Broker shards', () => {
  assert.equal(new Set(testMatrix.map((entry) => entry.name)).size, testMatrix.length);
  assert.deepEqual(new Set(testMatrix.map((entry) => entry.project)), new Set(suites.map(([, project]) => project)));
  assert.equal(testMatrix.filter((entry) => entry.shard).length, brokerShardCount);
  const solution = readFileSync(new URL('../../../Agentweaver.slnx', import.meta.url), 'utf8');
  const solutionTests = [...solution.matchAll(/tests\\([^\\]+)\\[^"]+\.csproj/g)].map((match) => match[1]);
  assert.deepEqual(new Set(solutionTests), new Set(testMatrix.map((entry) => entry.project)));
});

test('discovered methods and theory cases are partitioned once, deterministically, without splitting cases', () => {
  const names = Array.from({ length: 200 }, (_, index) => `Agentweaver.Broker.Tests.Class.Method${index}`);
  const discovery = `Some VSTest banner\n${names.map((name) => `    ${name}(value: "test")\n    ${name}(value: "other")`).join('\n')}`;
  const partitions = partitionMethods(discovery);
  assert.deepEqual(partitionMethods(discovery), partitions);
  assert.deepEqual(partitions.flat().sort(), names.sort());
  assert.ok(partitions.every((partition) => partition.length > 0));
  assert.throws(() => partitionMethods('No tests found'), /no Agentweaver/);
  assert.throws(() => partitionMethods('    Agentweaver.Tests.One.Test'), /empty Broker shard/);
});

test('measured weights spread slow Broker methods across shards and keep every method exactly once', () => {
  const heavy = Array.from({ length: 8 }, (_, index) => `Agentweaver.Broker.Tests.Slow.Method${index}`);
  const light = Array.from({ length: 40 }, (_, index) => `Agentweaver.Broker.Tests.Fast.Method${index}`);
  const discovery = [...heavy, ...light].map((name) => `    ${name}`).join('\n');
  const weights = { defaultSeconds: 0.2, methods: Object.fromEntries(heavy.map((name) => [name, 50])) };
  const partitions = partitionMethods(discovery, weights);
  assert.deepEqual(partitionMethods(discovery, weights), partitions);
  assert.deepEqual(partitions.flat().sort(), [...heavy, ...light].sort());
  assert.ok(partitions.every((partition) => partition.filter((name) => heavy.includes(name)).length === 2));
  const loads = partitions.map((partition) => partition.reduce((sum, name) => sum + (weights.methods[name] ?? 0.2), 0));
  assert.ok(Math.max(...loads) - Math.min(...loads) <= 0.2 + 1e-9);
});

test('weights are measured per method from TRX receipts with theory cases summed', () => {
  const trx = (id, className, name, duration) =>
    `<UnitTest name="${name}" storage="x.dll" id="${id}"><Execution id="e${id}" /><TestMethod codeBase="x.dll" className="${className}" name="${name}" /></UnitTest>` +
    `<UnitTestResult executionId="e${id}" testId="${id}" testName="${name}(case)" duration="${duration}" outcome="Passed" />`;
  const weights = measureWeights([
    trx('a', 'Agentweaver.T.C', 'Slow', '00:00:30.0000000') + trx('b', 'Agentweaver.T.C', 'Slow', '00:01:00.5000000'),
    trx('c', 'Agentweaver.T.C', 'Fast', '00:00:00.3000000'),
  ], { source: 'unit' });
  assert.deepEqual(weights, { source: 'unit', defaultSeconds: 0.3, methods: { 'Agentweaver.T.C.Slow': 90.5 } });
  assert.throws(() => measureWeights(['']), /no test results/);
});

test('committed Broker weights are recorded, positive and scoped to Broker tests', () => {
  const weights = loadBrokerWeights();
  assert.match(weights.source, /run \d+/);
  assert.ok(weights.defaultSeconds > 0);
  assert.ok(Object.keys(weights.methods).length > 0);
  for (const [method, seconds] of Object.entries(weights.methods)) {
    assert.match(method, /^Agentweaver\.Identity\.Broker\.Tests\./);
    assert.ok(Number.isFinite(seconds) && seconds > 0, method);
  }
});

test('TRX receipts reject zero execution, missing counters and failures while preserving explicit opt-in skips', () => {
  verifyTrx('<Counters total="2" executed="2" passed="2" failed="0" />');
  verifyTrx('<Counters total="3" executed="2" passed="2" failed="0" notExecuted="0" /><UnitTestResult outcome="NotExecuted" />');
  for (const xml of ['', '<Counters total="0" executed="0" passed="0" failed="0" />',
    '<Counters total="2" executed="1" passed="1" failed="0" />',
    '<Counters total="2" executed="2" passed="1" failed="1" />']) {
    assert.throws(() => verifyTrx(xml), /TRX receipt/);
  }
});

test('required owner-transaction methods must have passing results for every executed theory case', () => {
  const method = 'Agentweaver.Environment.Tests.P1CreationPostgresTests.OwnerTransaction';
  const definition = '<UnitTest id="a"><TestMethod className="Agentweaver.Environment.Tests.P1CreationPostgresTests" name="OwnerTransaction" /></UnitTest>';
  const result = (outcome, id = 'a') => `<UnitTestResult testId="${id}" outcome="${outcome}" />`;
  verifyRequiredMethods(definition + result('Passed'), [method]);
  verifyRequiredMethods(definition + result('Passed') + result('Passed'), [method]);
  for (const xml of [
    definition,
    definition + result('NotExecuted'),
    definition + result('Failed'),
    definition + result('Passed') + result('NotExecuted'),
    definition + result('Passed', 'foreign'),
    result('Passed'),
  ]) {
    assert.throws(() => verifyRequiredMethods(xml, [method]), /Required test did not execute successfully/);
  }
});

test('Environment P1 owner tests are explicitly enforced after the actual CI test invocation', () => {
  const runner = readFileSync(new URL('../v1-test-plan.mjs', import.meta.url), 'utf8');
  const source = readFileSync(new URL('../../../tests/Agentweaver.Environment.Tests/EnvironmentP1CreationLinkPostgresTests.cs', import.meta.url), 'utf8');
  assert.equal(environmentP1RequiredMethods.length, 4);
  for (const method of environmentP1RequiredMethods) {
    assert.ok(source.includes(`Task ${method.split('.').at(-1)}()`), method);
  }
  assert.match(runner, /run\(args\);[\s\S]*verifyTrx\(trx\);[\s\S]*if \(entry.name === 'environment'\) verifyRequiredMethods\(trx, environmentP1RequiredMethods\)/);
});

test('workflow preserves aggregate name, source provenance, native checks, release validation and P1', async () => {
  const { parse } = await import('yaml');
  const parsed = parse(workflow);
  const jobs = parsed.jobs;
  assert.deepEqual(jobs.foundation.needs, ['changes', 'harness', 'go', 'images', 'node', 'web', 'dotnet', 'coverage']);
  const harnessCommands = jobs.harness.steps.map((step) => step.run ?? '').join('\n');
  assert.match(harnessCommands, /npm run test:harness(?:\s|$)/);
  const packageManifest = JSON.parse(readFileSync(new URL('../../../package.json', import.meta.url), 'utf8'));
  assert.ok(packageManifest.scripts['test:harness'].split(/\s+/)
    .includes('scripts/azure/p1-native-probes/*.test.mjs'));
  assert.match(harnessCommands, /npm run test:harness:signin-browser/);
  assert.doesNotMatch(harnessCommands, /--experimental-test-coverage|--coverage|run coverage/);
  assert.equal(jobs.foundation.if, '${{ always() }}');
  assert.equal(jobs.dotnet.strategy['fail-fast'], false);
  assert.match(parsed.concurrency['cancel-in-progress'], /pull_request/);
  assert.match(parsed.concurrency.group, /github.run_id/);
  assert.equal(parsed.env.COLLECT_COVERAGE, '${{ inputs.collect_coverage == true }}');
  assert.equal(parsed.on.workflow_call.inputs.collect_coverage.default, false);
  assert.equal(parsed.on.workflow_dispatch, null);
  assert.ok(jobs.images.steps.some((step) => step.name === 'Run pinned native prompt-capacity tests in an isolated AMD64 container'));
  assert.ok(jobs.node.steps.some((step) => step.name === 'Validate release composition'));
  assert.match(jobs.node.steps.find((step) => step.name === 'Validate release composition').run,
    /if \[ "\$GITHUB_EVENT_NAME" = workflow_dispatch \]; then[\s\S]*RELEASE_BASE_SHA="\$\(git rev-parse HEAD\^1\)"/);
  assert.ok(jobs.node.steps.some((step) => step.name === 'Test controlled P1 API, MCP, and UI journeys'));
  assert.deepEqual(jobs.node.needs, ['changes', 'web']);
  assert.ok(jobs.node.steps.some((step) => step.with?.name === 'v1-web-dist'));
  assert.ok(jobs.node.steps.some((step) => step.run?.includes('dotnet restore services/agenthost/Agentweaver.AgentHost/Agentweaver.AgentHost.csproj --locked-mode')));
  assert.ok(jobs.node.steps.some((step) => step.run?.includes('dotnet restore apps/web/host/Agentweaver.Web.Host.csproj --locked-mode')));
  // Earlier release validation failures must not skip prerequisites for tests that still execute.
  const prerequisiteCondition = "${{ !cancelled() && needs.changes.outputs.node == 'true' }}";
  for (const prerequisite of jobs.node.steps.filter((step) =>
    step.uses === 'actions/setup-dotnet@v4' ||
    step.name === 'Cache NuGet packages for executable tooling fixtures' ||
    step.name === 'Restore dependencies required by Linux collector and Web image contracts' ||
    step.name === 'Verify locked P1 PostgreSQL initializer dependencies' ||
    step.uses === 'actions/download-artifact@v4' ||
    step.name === 'Preload pinned ASP.NET runtime image for release validator')) {
    assert.equal(prerequisite.if, prerequisiteCondition);
  }
  assert.match(workflow, /org.opencontainers.image.revision/);
  assert.match(workflow, /io.agentweaver.source-tree/);
  assert.ok(jobs.dotnet.steps.some((step) => step.run?.includes('install --with-deps chromium')));
  assert.ok(jobs.dotnet.steps.some((step) => step.with?.path === '~/.cache/ms-playwright'));
  assert.ok(jobs.dotnet.steps.some((step) => step.if === "matrix.shard > 0 && steps.web-modules.outputs.cache-hit != 'true'" &&
    step.run === 'npm ci --prefix apps/web --no-audit --no-fund'));
  assert.match(jobs.coverage.if, /inputs.collect_coverage == true/);
  assert.doesNotMatch(workflow, /github.event_name != 'pull_request'/);
  assert.ok(jobs.coverage.steps.some((step) => step.run === 'node scripts/coverage/dotnet.mjs --combine'));
});

test('initializer lock verification runs in the release contract job for dependency and version changes', async () => {
  const { parse } = await import('yaml');
  const step = parse(workflow).jobs.node.steps.find((entry) =>
    entry.name === 'Verify locked P1 PostgreSQL initializer dependencies');
  assert.equal(step.run, 'dotnet restore tools/P1PostgresInitializer/P1PostgresInitializer.csproj --locked-mode');
  assert.equal(step.if, "${{ !cancelled() && needs.changes.outputs.node == 'true' }}");
  for (const path of [
    'tools/P1PostgresInitializer/packages.lock.json',
    'tools/P1PostgresInitializer/P1PostgresInitializer.csproj',
    'packages/Agentweaver.Abstractions/Agentweaver.Abstractions.csproj',
    'packages/Agentweaver.Identity/Agentweaver.Identity.csproj',
    'services/events-and-sessions/Agentweaver.EventsAndSessions/Agentweaver.EventsAndSessions.csproj',
  ]) {
    assert.equal(classify([path]).node, true, path);
  }
});

test('installed web dependencies are cached only on an exact lockfile and Node.js key', async () => {
  const { parse } = await import('yaml');
  const { jobs } = parse(workflow);
  for (const job of [jobs.web, jobs.dotnet]) {
    const node = job.steps.find((step) => step.id === 'node');
    assert.equal(node.uses, 'actions/setup-node@v4');
    const cache = job.steps.find((step) => step.id === 'web-modules');
    assert.equal(cache.uses, 'actions/cache@v4');
    assert.equal(cache.with['restore-keys'], undefined, 'a partial node_modules restore could hide a lockfile drift');
    assert.equal(cache.with.key, "v1-web-modules-${{ runner.os }}-${{ runner.arch }}-node-${{ steps.node.outputs.node-version }}-${{ hashFiles('apps/web/package-lock.json') }}");
    assert.match(cache.with.path, /^apps\/web\/node_modules$/m);
    assert.match(cache.with.path, /^!apps\/web\/node_modules\/\.vite$/m);
    const install = job.steps.find((step) => step.run === 'npm ci --prefix apps/web --no-audit --no-fund');
    assert.match(install.if, /steps\.web-modules\.outputs\.cache-hit != 'true'/);
    assert.ok(job.steps.indexOf(cache) < job.steps.indexOf(install));
  }
});

test('Broker fixture keeps one PostgreSQL container, lazy shared driver and test-owned browser contexts', () => {
  const fixture = readFileSync(new URL('../../../tests/Agentweaver.Identity.Broker.Tests/PostgresContainerFixture.cs', import.meta.url), 'utf8');
  assert.match(fixture, /ICollectionFixture<PostgresContainerFixture>/);
  assert.match(fixture, /Lazy<Task<IPlaywright>>/);
  assert.match(fixture, /finally[\s\S]*_container.DisposeAsync/);
  assert.match(fixture, /CREATE DATABASE/);
  for (const file of ['ProjectsConfigBrokerAuthorizationTests.cs', 'RepoAppBrowserIntegrationTests.cs']) {
    const source = readFileSync(new URL(`../../../tests/Agentweaver.Identity.Broker.Tests/${file}`, import.meta.url), 'utf8');
    assert.match(source, /var playwright = await postgres.GetPlaywrightAsync/);
    assert.match(source, /await using var browser = await playwright.Chromium.LaunchAsync/);
    assert.match(source, /browser.NewContextAsync/);
    assert.doesNotMatch(source, /using var playwright/);
  }
});

test('the real foundation gate rejects failures, cancellation and unexpected skips while accepting scoped PRs', async () => {
  const { parse } = await import('yaml');
  const gate = parse(workflow).jobs.foundation.steps[0].run.match(/-e '([\s\S]*)'\s*$/)[1];
  function execute(results, collect = 'false') {
    return runInNewContext(gate, { process: { env: { RESULTS: JSON.stringify(results), COLLECT_COVERAGE: collect } } });
  }
  const full = Object.fromEntries(['changes', 'harness', 'go', 'images', 'node', 'web', 'dotnet', 'coverage']
    .map((name) => [name, { result: name === 'coverage' ? 'skipped' : 'success' }]));
  full.changes.outputs = { images: 'true', web: 'true', dotnet: 'true' };
  execute(full);
  for (const name of ['harness', 'images', 'node', 'web', 'dotnet', 'changes']) {
    for (const result of ['failure', 'cancelled', 'skipped']) {
      const broken = structuredClone(full);
      broken[name].result = result;
      assert.throws(() => execute(broken), new RegExp(name));
    }
  }
  assert.throws(() => execute(full, 'true'), /coverage/);
  const weekly = structuredClone(full);
  weekly.coverage.result = 'success';
  execute(weekly, 'true');
  assert.throws(() => execute(weekly), /coverage/);
  const scoped = structuredClone(full);
  scoped.changes.outputs = { images: 'false', web: 'false', dotnet: 'false' };
  for (const name of ['images', 'web', 'dotnet']) scoped[name].result = 'skipped';
  execute(scoped);
});
