import { spawnSync } from 'node:child_process';

const PROJECT = 'tests/Agentweaver.Tests/Agentweaver.Tests.csproj';
const COPILOT_PROPERTY = '-p:CopilotSkipCliDownload=true';
const EXCLUDED_CATEGORIES = [
  'Category!=KataRuntime',
  'Category!=PostgresIntegration',
  'Category!=ProcessEnvironment',
].join('&');

function normalFilter(prefixes) {
  return `${EXCLUDED_CATEGORIES}&(${prefixes.map((prefix) => (
    `FullyQualifiedName~Agentweaver.Tests.${prefix}`
  )).join('|')})`;
}

// The regular shards use product namespaces/classes; environment, container, and Kata tests
// are selected only by their explicit categories below.
export const TEST_SHARDS = [
  {
    id: 'orchestration',
    name: 'orchestration',
    filter: normalFilter([
      'Backlog', 'Blueprints', 'Casting', 'Coordinator', 'Graph', 'Notifications',
      'Projects', 'Runs', 'Workflow', 'Workflows',
    ]),
  },
  {
    id: 'application',
    name: 'application and authorization',
    filter: normalFilter([
      'A2ATransport', 'Api', 'ArtifactFiles', 'Assistant', 'Auth', 'CommitEndpoint',
      'Mcp', 'OAuth', 'OpenApi', 'Preview', 'ReviewEndpoint', 'ReviewPolicies', 'Web', 'Webhooks',
    ]),
  },
  {
    id: 'runtime',
    name: 'runtime and sandbox',
    filter: normalFilter([
      'AgentHost', 'Async', 'ClusterDiagnostics', 'Connect', 'Diagnostics', 'Durable', 'Foundry', 'Infrastructure',
      'Kubernetes', 'Memory', 'Metrics', 'Observability', 'RealPath', 'Remote', 'Repository',
      'RunEvent', 'RunOptions', 'RunOrchestrator', 'Runtime', 'Sandbox', 'Shell', 'System',
      'ToolApproval', 'Workspace',
    ]),
  },
  {
    id: 'catalog',
    name: 'catalog and integrations',
    filter: normalFilter([
      'AgentProvider', 'Git', 'Github', 'GlobGrep', 'Question', 'Rai', 'SkillMarketplace',
      'Skills', 'Squad',
    ]),
  },
  {
    id: 'postgres',
    name: 'PostgreSQL Testcontainers',
    filter: 'Category=PostgresIntegration',
  },
  {
    id: 'process-environment',
    name: 'process-global environment',
    filter: 'Category=ProcessEnvironment&Category!=KataRuntime&Category!=PostgresIntegration',
    settings: 'tests/Agentweaver.Tests/process-environment.runsettings',
  },
  {
    id: 'kata-runtime',
    name: 'Kata runtime',
    filter: 'Category=KataRuntime',
    requiresBubblewrap: true,
    minimumTests: 34,
  },
];

function fail(message) {
  throw new Error(`[dotnet-test-shards] ${message}`);
}

export function shardById(id) {
  const shard = TEST_SHARDS.find((candidate) => candidate.id === id);
  if (!shard) {
    fail(`unknown shard "${id}"`);
  }
  return shard;
}

export function parseTestList(output) {
  return output
    .split(/\r?\n/u)
    .map((line) => line.trim())
    .filter((line) => line.startsWith('Agentweaver.Tests.'));
}

export function validatePartition(discovered, selections) {
  const discoveredSet = new Set(discovered);
  if (discoveredSet.size !== discovered.length) {
    fail('unfiltered discovery returned duplicate test names');
  }

  const occurrences = new Map([...discoveredSet].map((test) => [test, []]));
  for (const { id, tests } of selections) {
    for (const test of tests) {
      if (!occurrences.has(test)) {
        fail(`shard "${id}" selected unknown test "${test}"`);
      }
      occurrences.get(test).push(id);
    }
  }

  const gaps = [];
  const overlaps = [];
  for (const [test, shardIds] of occurrences) {
    if (shardIds.length === 0) gaps.push(test);
    if (shardIds.length > 1) overlaps.push(`${test} (${shardIds.join(', ')})`);
  }
  if (gaps.length || overlaps.length) {
    const detail = [
      gaps.length ? `gaps (${gaps.length}): ${gaps.slice(0, 10).join(', ')}` : null,
      overlaps.length ? `overlaps (${overlaps.length}): ${overlaps.slice(0, 10).join(', ')}` : null,
    ].filter(Boolean).join('; ');
    fail(`test partition is not exact: ${detail}`);
  }
}

function listTests({ project, filter }) {
  const args = ['test', project, '--no-build', '--no-restore', COPILOT_PROPERTY, '--list-tests'];
  if (filter) args.push('--filter', filter);

  const result = spawnSync('dotnet', args, {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    process.stderr.write(result.stderr);
    fail(`discovery failed for ${filter ?? 'the unfiltered suite'} (exit ${result.status})`);
  }
  return parseTestList(result.stdout);
}

function matrix() {
  return JSON.stringify({
    include: TEST_SHARDS.map(({ id, name, filter, settings, requiresBubblewrap }) => ({
      id,
      name,
      filter,
      settings: settings ?? '',
      requiresBubblewrap: requiresBubblewrap === true,
    })),
  });
}

function verify(project) {
  const discovered = listTests({ project });
  const selections = TEST_SHARDS.map((shard) => ({
    id: shard.id,
    tests: listTests({ project, filter: shard.filter }),
  }));
  for (const shard of TEST_SHARDS) {
    const selected = selections.find((selection) => selection.id === shard.id).tests.length;
    if (shard.minimumTests && selected < shard.minimumTests) {
      fail(`${shard.id} selected ${selected} tests; expected at least ${shard.minimumTests}`);
    }
    if (selected === 0) {
      fail(`${shard.id} selected no tests`);
    }
    console.log(`${shard.id}: ${selected} tests`);
  }
  validatePartition(discovered, selections);
  console.log(`Exact partition verified: ${discovered.length} discovered tests run once across ${TEST_SHARDS.length} shards.`);
}

function main(argv) {
  const [command, ...arguments_] = argv;
  if (command === 'matrix') {
    process.stdout.write(matrix());
    return;
  }
  if (command === 'verify') {
    const projectIndex = arguments_.indexOf('--project');
    const project = projectIndex === -1 ? PROJECT : arguments_[projectIndex + 1];
    if (!project) fail('--project requires a value');
    verify(project);
    return;
  }
  fail('usage: dotnet-test-shards.mjs <matrix|verify> [--project <path>]');
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  main(process.argv.slice(2));
}
