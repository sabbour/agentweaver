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
      'ToolApproval', 'RunActiveClaimGuard', 'Workspace',
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
  },
];

export function matrix() {
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

function main(argv) {
  const [command] = argv;
  if (command === 'matrix') {
    process.stdout.write(matrix());
    return;
  }
  throw new Error('usage: dotnet-test-shards.mjs matrix');
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  main(process.argv.slice(2));
}
