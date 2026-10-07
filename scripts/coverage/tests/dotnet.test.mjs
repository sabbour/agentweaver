import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const runner = readFileSync(resolve(root, 'scripts/coverage/dotnet.mjs'), 'utf8');
const runsettings = readFileSync(resolve(root, 'coverage.runsettings'), 'utf8');
const solution = readFileSync(resolve(root, 'Agentweaver.slnx'), 'utf8');
const testProject = readFileSync(resolve(
  root, 'tests', 'Agentweaver.EventsAndSessions.Tests', 'Agentweaver.EventsAndSessions.Tests.csproj'), 'utf8');
const environmentRunner = readFileSync(resolve(root, 'scripts', 'coverage', 'dotnet.mjs'), 'utf8');
const environmentTestProject = readFileSync(resolve(
  root, 'tests', 'Agentweaver.Environment.Tests', 'Agentweaver.Environment.Tests.csproj'), 'utf8');
const environmentProject = readFileSync(resolve(
  root, 'services', 'environment', 'Agentweaver.Environment', 'Agentweaver.Environment.csproj'), 'utf8');
const knowledgeTestProject = readFileSync(resolve(
  root, 'tests', 'Agentweaver.Knowledge.Tests', 'Agentweaver.Knowledge.Tests.csproj'), 'utf8');
const azureFilesTestProject = readFileSync(resolve(
  root, 'tests', 'Agentweaver.Providers.Storage.AzureFiles.Tests',
  'Agentweaver.Providers.Storage.AzureFiles.Tests.csproj'), 'utf8');
const brokerTestProject = readFileSync(resolve(
  root, 'tests', 'Agentweaver.Identity.Broker.Tests', 'Agentweaver.Identity.Broker.Tests.csproj'), 'utf8');

test('the explicit .NET coverage runner covers Agent Runtime through the Broker suite', () => {
  assert.match(runner, /\['identity-broker', 'Agentweaver\.Identity\.Broker\.Tests'\]/);
  assert.match(runner, /'Agentweaver\.AgentRuntime',/);
  assert.match(runsettings, /\[Agentweaver\.AgentRuntime\]\*/);
  assert.match(solution, /packages\\Agentweaver\.AgentRuntime\\Agentweaver\.AgentRuntime\.csproj/);
  assert.match(solution, /tests\\Agentweaver\.Identity\.Broker\.Tests\\Agentweaver\.Identity\.Broker\.Tests\.csproj/);
  assert.match(brokerTestProject, /Agentweaver\.AgentRuntime\.csproj/);
  assert.match(brokerTestProject, /coverlet\.collector/);
});

test('the explicit .NET coverage runner registers the Events & Sessions suite and assembly', () => {
  assert.match(runner, /\['events-and-sessions', 'Agentweaver\.EventsAndSessions\.Tests'\]/);
  assert.match(runner, /'Agentweaver\.EventsAndSessions',/);
  assert.match(runsettings, /\[Agentweaver\.EventsAndSessions\]\*/);
  assert.match(solution, /tests\\Agentweaver\.EventsAndSessions\.Tests\\Agentweaver\.EventsAndSessions\.Tests\.csproj/);
  assert.match(testProject, /coverlet\.collector/);
});

test('the explicit .NET coverage runner registers the Environment egress suite and assembly', () => {
  assert.match(environmentRunner, /\['environment', 'Agentweaver\.Environment\.Tests'\]/);
  assert.match(environmentRunner, /'Agentweaver\.Environment',/);
  assert.match(runsettings, /\[Agentweaver\.Environment\]\*/);
  assert.match(solution, /tests\\Agentweaver\.Environment\.Tests\\Agentweaver\.Environment\.Tests\.csproj/);
  assert.match(environmentTestProject, /coverlet\.collector/);
  assert.match(environmentProject, /Agentweaver\.Abstractions\.csproj/);
  assert.match(environmentProject, /Agentweaver\.Providers\.csproj/);
});

test('the explicit .NET coverage runner registers the Knowledge suite and assembly', () => {
  assert.match(runner, /\['knowledge', 'Agentweaver\.Knowledge\.Tests'\]/);
  assert.match(runner, /'Agentweaver\.Knowledge',/);
  assert.match(runsettings, /\[Agentweaver\.Knowledge\]\*/);
  assert.match(solution, /tests\\Agentweaver\.Knowledge\.Tests\\Agentweaver\.Knowledge\.Tests\.csproj/);
  assert.match(knowledgeTestProject, /coverlet\.collector/);
});

test('the explicit .NET coverage runner registers the Azure Files suite and assembly', () => {
  assert.match(environmentRunner, /\['azure-files', 'Agentweaver\.Providers\.Storage\.AzureFiles\.Tests'\]/);
  assert.match(environmentRunner, /'Agentweaver\.Providers\.Storage\.AzureFiles',/);
  assert.match(runsettings, /\[Agentweaver\.Providers\.Storage\.AzureFiles\]\*/);
  assert.match(solution,
    /tests\\Agentweaver\.Providers\.Storage\.AzureFiles\.Tests\\Agentweaver\.Providers\.Storage\.AzureFiles\.Tests\.csproj/);
  assert.match(azureFilesTestProject, /coverlet\.collector/);
});
