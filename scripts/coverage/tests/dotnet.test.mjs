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
