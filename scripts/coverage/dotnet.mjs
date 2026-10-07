import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const output = join(root, 'artifacts', 'coverage', 'dotnet');
const manifest = join(root, '.config', 'dotnet-tools.json');
const suites = [
  ['orchestrator-core', 'Agentweaver.Orchestrator.Core.Tests'],
  ['providers', 'Agentweaver.Providers.Tests'],
  ['postgres', 'Agentweaver.Persistence.Postgres.Tests'],
  ['keyvault', 'Agentweaver.Secrets.AzureKeyVault.Tests'],
  ['identity', 'Agentweaver.Identity.Tests'],
  ['identity-broker', 'Agentweaver.Identity.Broker.Tests'],
  ['projects-config', 'Agentweaver.Projects.Config.Tests'],
  ['telemetry', 'Agentweaver.Telemetry.Tests'],
  ['azure-monitor', 'Agentweaver.Telemetry.AzureMonitor.Tests'],
  ['azure-blob', 'Agentweaver.ObjectStore.AzureBlob.Tests'],
  ['foundation-probe', 'Agentweaver.FoundationProbe.Tests'],
  ['events-and-sessions', 'Agentweaver.EventsAndSessions.Tests'],
  ['environment', 'Agentweaver.Environment.Tests'],
  ['knowledge', 'Agentweaver.Knowledge.Tests'],
  ['azure-files', 'Agentweaver.Providers.Storage.AzureFiles.Tests'],
];
const expectedAssemblies = [
  'Agentweaver.Abstractions',
  'Agentweaver.Orchestrator',
  'Agentweaver.Orchestrator.Core',
  'Agentweaver.Providers',
  'Agentweaver.Persistence.Postgres',
  'Agentweaver.Secrets.AzureKeyVault',
  'Agentweaver.Identity',
  'Agentweaver.AgentRuntime',
  'Agentweaver.Identity.Broker',
  'Agentweaver.Projects.Config',
  'Agentweaver.Telemetry',
  'Agentweaver.Telemetry.AzureMonitor',
  'Agentweaver.ObjectStore.AzureBlob',
  'Agentweaver.FoundationProbe',
  'Agentweaver.EventsAndSessions',
  'Agentweaver.Environment',
  'Agentweaver.Knowledge',
  'Agentweaver.Providers.Storage.AzureFiles',
];
const dotnet = process.env.DOTNET_HOST_PATH || 'dotnet';

function run(args) {
  console.log(`dotnet ${args.join(' ')}`);
  const result = spawnSync(dotnet, args, { cwd: root, stdio: 'inherit' });
  if (result.error) {
    console.error(result.error.message);
    return 1;
  }
  return result.status ?? 1;
}

function reportsIn(directory) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const file = join(directory, entry.name);
    return entry.isDirectory() ? reportsIn(file) : entry.name === 'coverage.cobertura.xml' && statSync(file).size > 0 ? [file] : [];
  });
}

rmSync(output, { recursive: true, force: true });
mkdirSync(output, { recursive: true });

if (run(['tool', 'restore', '--tool-manifest', manifest])) process.exit(1);

let failed = false;
const reports = [];
for (const [name, project] of suites) {
  const directory = join(output, name);
  const code = run([
    'test', join('tests', project, `${project}.csproj`),
    '--no-build', '--no-restore', '--configuration', 'Release',
    '--settings', 'coverage.runsettings', '--collect', 'XPlat Code Coverage',
    '--results-directory', directory,
  ]);
  if (code) {
    console.error(`${project} exited with code ${code}`);
    failed = true;
  }
  const found = reportsIn(directory);
  if (found.length !== 1) {
    console.error(`Expected one nonempty Cobertura report for ${project}, found ${found.length}`);
    failed = true;
  }
  reports.push(...found);
}

if (reports.length) {
  const combined = join(output, 'combined');
  if (run([
    'tool', 'run', 'reportgenerator', '--',
    `-reports:${reports.join(';')}`, `-targetdir:${combined}`,
    '-reporttypes:Cobertura;JsonSummary;TextSummary;Html;MarkdownSummaryGithub',
  ])) failed = true;

  for (const name of ['Cobertura.xml', 'Summary.json', 'Summary.txt', 'index.html', 'SummaryGithub.md']) {
    const file = join(combined, name);
    if (!existsSync(file) || !statSync(file).size) {
      console.error(`Missing or empty combined report: ${file}`);
      failed = true;
    }
  }
  const cobertura = join(combined, 'Cobertura.xml');
  if (existsSync(cobertura)) {
    const xml = readFileSync(cobertura, 'utf8');
    const assemblies = new Set([...xml.matchAll(/<package name="([^"]+)"/g)].map((match) => match[1]));
    for (const assembly of expectedAssemblies) {
      if (!assemblies.has(assembly)) {
        console.error(`Missing instrumented production library: ${assembly}`);
        failed = true;
      }
    }
    if (!/<coverage [^>]*lines-valid="[1-9]\d*"/.test(xml)) {
      console.error('Combined report contains no measurable production lines');
      failed = true;
    }
  }
  const summary = join(combined, 'Summary.json');
  if (existsSync(summary)) {
    try {
      const { summary: totals, coverage } = JSON.parse(readFileSync(summary, 'utf8'));
      const assemblies = new Map(coverage.assemblies.map((assembly) => [assembly.name, assembly]));
      if (totals.coverablelines <= 0 || totals.totalmethods <= 0 ||
          totals.assemblies !== expectedAssemblies.length ||
          expectedAssemblies.some((name) => !assemblies.get(name)?.coverablelines)) {
        console.error('Combined summary does not contain all measurable production libraries');
        failed = true;
      }
    } catch (error) {
      console.error(`Invalid combined summary: ${error.message}`);
      failed = true;
    }
  }
}

if (failed) process.exitCode = 1;
else console.log(`Combined .NET coverage: ${join(output, 'combined')}`);
