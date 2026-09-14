import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '../../../..');
const plan = JSON.parse(await fs.readFile(path.join(repo, '.github/skills/docs-diagram-audit/reports/plan-deep-dive-core.json')));
const retired = ['00-system-overview-fig7', '00-system-overview-fig8', 'api-core-fig7'];
const alt = {
  '00-system-overview-fig1': 'Intent, control and execution planes: separate MCP broker boundary, API and worker roles, AgentHost, durable state and workspace',
  '00-system-overview-fig2': 'Representative full single-agent workflow, distinct from public coordinator submission and trimmed child graphs',
  '00-system-overview-fig5': 'Run-provenanced observations, governed promotion, accepted memory, export and filtered future context',
  'agent-definition-fig1': 'MCP source generates five outputs; the embedded definition is materialized without overwriting an existing project file',
  'agent-framework-fig1': 'Typed MAF review and merge adapters carry explicit contracts and persisted workflow state',
  'agent-framework-fig2': 'Coordinator spec and confirmation hand off from MAF to durable service-driven dispatch and collective assembly',
  'agent-framework-fig3': 'Live correlated responses, deferred delivery and process checkpoint restoration are distinct paths',
  'api-core-fig4': 'Endpoint-classified integrity, authentication and authorization precede handler resource-role checks',
  'api-core-fig6': 'Diagnostics query real system checks and return observed status rather than a readiness guarantee',
  'canonical-api-host': 'Startup composition is separate from per-request policy, endpoint roles, domain services, stores and external adapters',
  'canonical-testing-boundary': 'Real host, stores, Git and validators versus explicit deterministic model and network test seams',
  'data-persistence-fig1': 'Production Postgres EF stores, shared events and checkpoints versus local SQLite stores and file checkpoints',
  'frontend-fig1': 'App routes and shell providers consume API snapshots and streams to produce browser-only projections',
  'frontend-fig2': 'Explicit App.tsx routes distinguish global destinations, project-scoped paths and redirects',
  'frontend-fig3': 'Static SPA hosting, origin-only runtime API configuration and external docs redirects',
  'frontend-fig6': 'Entra sign-in, one-time session exchange, per-tab bearer storage and authenticated API requests',
  'frontend-fig7': 'Authenticated fetch-based SSE, cursor replay, bounded buffering and deterministic UI projections',
  'mcp-server-fig2': 'MCP discovery, exact-resource consent and PKCE, API-issued broker tokens, MCP validation and API authorization',
  'project-generation-model-settings-fig1': 'Three project model preferences feed generation consumers without replacing provider admission',
  'project-skills-fig1': 'Skill acquisition and assignment lead to successful materialization before pointers, or inline delivery when unavailable',
  'projects-fig1': 'Local caller-path and persistent project-ID workspace provisioning with real write probes',
  'projects-fig2': 'Project defaults and base files relate to independent run worktrees, team context and sandbox execution',
  'repo-blueprint-suggestions-fig1': 'Anonymous repository metadata feeds deterministic catalog suggestions; caller cancellation propagates',
  'testing-strategy-fig1': 'Independent unit, API, workflow, frontend, PostgreSQL, MCP-process and deployed assurance boundaries',
  'testing-strategy-fig4': 'A test configures and substitutes explicit seams before exercising the real in-process API host',
};
const writes = [];
for (const relative of plan.document_paths) {
  const file = path.join(repo, relative);
  const before = await fs.readFile(file, 'utf8');
  let after = before.replace(/!\[[^\]]*\]\(\.\.\/diagrams\/([a-z0-9-]+)\.png\)/g,
    (match, name) => alt[name] ? `![${alt[name]}](../diagrams/${name}.png)` : match);
  after = after.replace(/<!--[\s\S]*?-->/g, comment => {
    const name = /diagrams\/src\/([a-z0-9-]+)\.json/.exec(comment)?.[1];
    if (!name || !alt[name]) return comment;
    return `<!-- Editable source: ../diagrams/src/${name}.drawio.\n     Export with pinned draw.io Desktop 31.4.5 using --spec ${name}.\n     Review lineage: ../diagrams/reviews/${name}/iteration-manifest.json. -->`;
  });
  if (relative.endsWith('/testing-strategy.md')) {
    after = after.replace(/^- \*\*Auth is explicit\.\*\*.*$/m,
      '- **Auth is explicit.** Entra/broker credentials and endpoint/project roles are checked, OAuth redirects are constrained, S256 PKCE is required, and production bypasses are guarded.');
  }
  if (after !== before) {
    await fs.writeFile(file, after);
    writes.push(relative);
  }
}
for (const name of retired) {
  for (const relative of plan.document_paths) {
    const content = await fs.readFile(path.join(repo, relative), 'utf8');
    if (content.includes(`../diagrams/${name}.png`)) throw new Error(`Unmigrated consumer: ${relative} ${name}`);
  }
  const review = path.join(repo, `docs/diagrams/reviews/${name}`);
  await fs.mkdir(review, { recursive: true });
  for (const relative of [
    `docs/diagrams/src/${name}.json`,
    `docs/diagrams/src/${name}.drawio`,
    `docs/diagrams/drawio/generated/${name}.drawio`,
    `docs/diagrams/${name}.png`,
    `docs/diagrams/${name}.hash.txt`,
  ]) {
    if (!plan.exclusive_asset_paths.includes(relative)) throw new Error(`Out-of-scope removal: ${relative}`);
    const file = path.join(repo, relative);
    try { await fs.access(file); } catch (error) { if (error.code === 'ENOENT') continue; throw error; }
    const basename = path.basename(file);
    const prefix = relative.includes('/generated/') ? 'legacy-generated-' : 'legacy-';
    await fs.copyFile(file, path.join(review, prefix + basename));
    await fs.unlink(file);
    writes.push(relative);
  }
}
await fs.writeFile(path.join(here, 'consumer-changes.json'), JSON.stringify({ writes, retired }, null, 2) + '\n');
console.log(`Updated ${writes.length} owned document/asset paths; all three redundant consumers migrated.`);
