import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { execFile as execFileCallback } from 'node:child_process';
import { promisify } from 'node:util';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import path from 'node:path';

import {
  parseArgs,
  resolveTransport,
  checkTargetAllowed,
  buildSessionId,
  buildTranscriptPath,
  buildDispatchPrompt,
  buildVerdictMetadata,
  parseTranscriptJsonl,
  runCapabilityCheck,
  prepareJudgeEvidence,
  finalizeVerdict,
} from '../run-persona.mjs';
import { serializeTranscriptLine } from '../lib/transcript.mjs';

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url));
const execFile = promisify(execFileCallback);
// Scratch outputs live under the harness's gitignored verdicts/ dir (never /tmp), and are
// removed after each use.
async function scratchDir() {
  const dir = path.join(TEST_DIR, '..', 'verdicts', `test-${randomUUID().slice(0, 8)}`);
  await mkdir(dir, { recursive: true });
  return dir;
}

test('parseArgs maps the api-parity CLI shape', () => {
  const args = parseArgs([
    '--scenario', 'priya', '--target', 'http://localhost:5000/mcp',
    '--project-id', 'proj-1', '--batch-id', 'b1', '--seed', 's1', '--out', 'v.json',
    '--transcript', 'tr.jsonl', '--dump-evidence', 'evidence.json', '--prompt-out', 'prompt.txt',
    '--server-command', 'dotnet', '--server-args', '["run"]',
  ]);
  assert.equal(args.scenario, 'priya');
  assert.equal(args.target, 'http://localhost:5000/mcp');
  assert.equal(args.projectId, 'proj-1');
  assert.equal(args.batchId, 'b1');
  assert.equal(args.seed, 's1');
  assert.equal(args.out, 'v.json');
  assert.equal(args.transcript, 'tr.jsonl');
  assert.equal(args.dumpEvidence, 'evidence.json');
  assert.equal(args.promptOut, 'prompt.txt');
  assert.equal(args.serverCommand, 'dotnet');
  assert.equal(args.serverArgs, '["run"]');
});

test('parseArgs accepts --persona and --base-url aliases', () => {
  const args = parseArgs(['--persona', 'jordan', '--base-url', 'stdio']);
  assert.equal(args.scenario, 'jordan');
  assert.equal(args.target, 'stdio');
});

test('retired credential argv options are rejected without echoing their values', () => {
  const canary = 'secret-canary-argv-55';
  const retiredOption = `--${'to'}${'ken'}`;
  assert.throws(
    () => parseArgs([`${retiredOption}=${canary}`]),
    (error) => error.message.includes(retiredOption) && !error.message.includes(canary),
  );
});

test('resolveTransport treats stdio as a sentinel and any URL as http', () => {
  assert.deepEqual(resolveTransport({ target: 'stdio' }), { mode: 'stdio', target: 'stdio' });
  assert.deepEqual(resolveTransport({}), { mode: 'stdio', target: 'stdio' });
  assert.deepEqual(resolveTransport({ target: 'http://localhost:5000/mcp' }), { mode: 'http', target: 'http://localhost:5000/mcp' });
});

test('checkTargetAllowed exempts stdio and validates transport and exact MCP path for http', () => {
  assert.equal(checkTargetAllowed({ mode: 'stdio', target: 'stdio' }, {}), null);
  assert.equal(checkTargetAllowed({ mode: 'http', target: 'http://localhost:5000/mcp' }, {}), null);
  assert.equal(checkTargetAllowed({ mode: 'http', target: 'https://arbitrary.example.test/mcp' }, {}), null);
  assert.match(checkTargetAllowed({ mode: 'http', target: 'http://remote.example.test/mcp' }, {}), /HTTPS/);
  assert.match(checkTargetAllowed({ mode: 'http', target: 'https://remote.example.test/mcp/' }, {}), /exactly/);
});

test('buildSessionId and buildTranscriptPath are deterministic for a fixed clock', () => {
  const now = new Date('2026-07-15T03:20:11.500Z');
  const sessionId = buildSessionId({ scenario: 'priya', now });
  assert.equal(sessionId, 'priya-live-2026-07-15T03-20-11-500Z');
  const transcriptPath = buildTranscriptPath({ sessionId, dir: path.join(TEST_DIR, 'transcripts') });
  assert.equal(path.basename(transcriptPath), 'priya-live-2026-07-15T03-20-11-500Z.jsonl');
});

test('buildDispatchPrompt embeds the charter, brief, target and transcript path, but never a fixed tool sequence', () => {
  const persona = { id: 'priya', name: 'Priya Nair', text: 'CORE + ADAPTER BRIEF TEXT' };
  const prompt = buildDispatchPrompt({
    persona,
    transport: { mode: 'http', target: 'https://mcp.staging.example.test/mcp' },
    tokenAvailable: true,
    transcriptPath: '/repo/scripts/mcp-harness/transcripts/priya-live-x.jsonl',
    projectId: 'proj-1',
    goal: 'inspect a triage plan and push back twice',
    charterText: 'CHARTER BODY',
  });
  assert.match(prompt, /CHARTER BODY/);
  assert.match(prompt, /CORE \+ ADAPTER BRIEF TEXT/);
  assert.match(prompt, /mcp\.staging\.example\.test\/mcp/);
  assert.match(prompt, /priya-live-x\.jsonl/);
  assert.match(prompt, /push back at least TWICE/i);
  assert.match(prompt, /tools\/list/);
  // The token value itself must not be echoed into the dispatch prompt.
  assert.doesNotMatch(prompt, /secret-token/);
  assert.match(prompt, /AGENTWEAVER_TOKEN/);
});

test('buildDispatchPrompt tells a stdio driver no token is needed', () => {
  const prompt = buildDispatchPrompt({
    persona: { id: 'priya', name: 'Priya', text: 'brief' },
    transport: { mode: 'stdio', target: 'stdio' },
    token: null,
    transcriptPath: '/repo/scripts/mcp-harness/transcripts/priya.jsonl',
  });
  assert.match(prompt, /stdio/);
  assert.match(prompt, /no network target, no token needed/);
});

test('buildVerdictMetadata carries the required join-key fields with surface mcp', () => {
  const now = new Date('2026-07-15T03:20:11.500Z');
  const persona = { name: 'Priya Nair', version: 'priya@abc', adapter: { version: 'priya.mcp@def' } };
  const meta = buildVerdictMetadata({
    args: { scenario: 'priya', seed: 'priya', batchId: 'b1', targetRevision: 'rev-9' },
    persona, transport: { mode: 'http', target: 'https://mcp.staging.example.test/mcp' },
    sessionId: 'priya-live-x', runId: 'run-42', now,
  });
  assert.equal(meta.surface, 'mcp');
  assert.equal(meta.batchId, 'b1');
  assert.equal(meta.scenarioId, 'priya');
  assert.equal(meta.inputSeed, 'priya');
  assert.equal(meta.adapterVersion, 'priya.mcp@def');
  assert.equal(meta.personaCoreVersion, 'priya@abc');
  assert.equal(meta.targetRevision, 'rev-9');
  assert.equal(meta.runId, 'run-42');
  assert.equal(meta.timestamp, now.toISOString());
});

test('buildVerdictMetadata defaults batchId, seed, and targetRevision when omitted', () => {
  const meta = buildVerdictMetadata({
    args: { scenario: 'jordan' }, persona: null,
    transport: { mode: 'stdio', target: 'stdio' }, sessionId: 'jordan-live-x',
  });
  assert.match(meta.batchId, /^mcp-/);
  assert.equal(meta.inputSeed, 'jordan');
  assert.equal(meta.targetRevision, 'stdio');
  assert.equal(meta.runId, 'jordan-live-x');
  assert.equal(meta.adapterVersion, 'unknown');
  assert.equal(meta.personaCoreVersion, 'unknown');
});

test('parseTranscriptJsonl normalizes turns and tolerates blank lines and bad JSON', () => {
  const jsonl = [
    '{"turn":1,"ts":"2026-07-15T03:20:11Z","thought":"discover","request":{"tool":"run_submit","arguments":{"project_id":"p","task":"t"}},"response":{"isError":false,"structuredContent":{"run_id":"r1","status":"queued"}}}',
    '',
    'not json',
    '{"turn":2,"thought":"push back","note":"pushback: 1","request":{"tool":"run_status","arguments":{"run_id":"r1"}},"response":{"isError":true,"protocolErrorCode":-32000,"rawContent":"boom"}}',
  ].join('\n');
  const { turns, parseErrors } = parseTranscriptJsonl(jsonl);
  assert.equal(turns.length, 2);
  assert.equal(parseErrors.length, 1);
  assert.equal(parseErrors[0].line, 3);
  assert.equal(turns[0].toolName, 'run_submit');
  assert.deepEqual(turns[0].toolArguments, { project_id: 'p', task: 't' });
  assert.equal(turns[0].mcp.isError, false);
  assert.deepEqual(turns[0].mcp.structuredContent, { run_id: 'r1', status: 'queued' });
  assert.equal(turns[0].outcome.ok, true);
  assert.equal(turns[1].toolName, 'run_status');
  assert.equal(turns[1].mcp.isError, true);
  assert.equal(turns[1].mcp.protocolErrorCode, -32000);
  assert.equal(turns[1].mcp.rawContent, 'boom');
  assert.equal(turns[1].note, 'pushback: 1');
  assert.equal(turns[1].outcome.ok, false);
});

test('dispatch and normalized transcript artifacts never contain bearer or query canaries', () => {
  const canary = 'ghp_abcdefghijklmnopqrstuvwxyz0123456789';
  const prompt = buildDispatchPrompt({
    persona: { id: 'priya', name: 'Priya', text: 'brief' },
    transport: { mode: 'http', target: 'https://example.test/mcp' },
    tokenAvailable: true,
    transcriptPath: '/repo/transcript.jsonl',
  });
  const parsed = parseTranscriptJsonl(JSON.stringify({
    turn: 1,
    thought: `Bearer ${canary}`,
    request: {
      tool: 'run_status',
      arguments: { url: `https://example.test/path?${canary}=${canary}` },
    },
    response: { rawContent: `Bearer ${canary}`, error: { token: canary } },
  }));
  assert.doesNotMatch(prompt, new RegExp(canary));
  assert.doesNotMatch(JSON.stringify(parsed), new RegExp(canary));
});

test('MCP prompt, JSONL, normalized evidence, and Judge input recursively redact secrets', () => {
  const canary = 'secret-canary-nested-42';
  const secretUrl = `https://user:${canary}@example.test/path?key=${canary}#${canary}`;
  const exchange = {
    thought: `observed Bearer ${canary}`,
    request: {
      headers: { Authorization: `Bearer ${canary}` },
      arguments: { callbackUrl: secretUrl },
    },
    response: {
      structuredContent: { nested: [{ token: canary }, { url: secretUrl }] },
      rawContent: `token=${canary}; request failed at ${secretUrl}`,
      error: new Error(`Bearer ${canary}`),
    },
  };
  const line = serializeTranscriptLine(exchange);
  const driverPrompt = buildDispatchPrompt({
    persona: { id: 'priya', name: 'Priya', text: 'brief' },
    transport: { mode: 'http', target: secretUrl },
    tokenAvailable: true,
    transcriptPath: '/repo/transcript.jsonl',
  });
  const prepared = prepareJudgeEvidence({
    transcriptText: line,
    persona: { name: 'Priya', text: 'brief', adapter: { content: 'adapter' }, content: 'core' },
    metadata: {
      batchId: 'b1', scenarioId: 'priya', inputSeed: 's1', adapterVersion: 'a1',
      personaCoreVersion: 'p1', targetRevision: secretUrl, surface: 'mcp',
      runId: 'r1', timestamp: '2026-09-03T00:00:00.000Z',
    },
  });
  for (const artifact of [line, driverPrompt, JSON.stringify(prepared.normalized), prepared.prompt]) {
    assert.doesNotMatch(artifact, new RegExp(canary));
    assert.doesNotMatch(artifact, /[?#]secret-canary/i);
  }
  assert.match(line, /https:\/\/example\.test\/path/);
});

test('runCapabilityCheck fails closed without a client and runs the contract with one', async () => {
  const none = await runCapabilityCheck({});
  assert.equal(none.available, false);

  const fakeClient = {
    discoverTools: async () => ([
      { name: 'run_submit', inputSchema: { type: 'object', required: ['project_id', 'task'], properties: { project_id: { type: 'string' }, task: { type: 'string' } } }, outputSchema: { type: 'object', required: ['run_id', 'status'], properties: { run_id: { type: 'string' }, status: { type: 'string' } } } },
      { name: 'run_status', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } }, outputSchema: { type: 'object', required: ['status'], properties: { status: { type: 'string' } } } },
      { name: 'run_show_artifacts', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } }, outputSchema: { type: 'object', required: ['artifacts'], properties: { artifacts: { type: 'array' } } } },
      { name: 'run_archive', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } } },
      { name: 'github_repo_app_connect' },
      { name: 'github_repo_app_authorization_status' },
      { name: 'project_copilot_app_connect' },
      { name: 'project_copilot_app_authorization_status' },
      { name: 'project_github_capability_status' },
      { name: 'project_list' },
      { name: 'project_create', inputSchema: { type: 'object', required: ['name', 'working_directory'], properties: { name: { type: 'string' }, working_directory: { type: 'string' } } } },
      { name: 'project_delete', inputSchema: { type: 'object', required: ['project_id'], properties: { project_id: { type: 'string' } } } },
      { name: 'diagnostics_get' },
      { name: 'coordinator_outcome_spec_confirm', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } } },
    ]),
  };
  const ok = await runCapabilityCheck({ client: fakeClient });
  assert.equal(ok.available, true);
  assert.equal(ok.report.ok, true);
});

test('finalizeVerdict writes a schema-valid verdict from a transcript using an injected judge', async () => {
  const dir = await scratchDir();
  const transcriptText = [
    '{"turn":1,"thought":"discover then submit","request":{"tool":"run_submit","arguments":{"project_id":"p","task":"triage"}},"response":{"isError":false,"structuredContent":{"run_id":"r1","status":"completed"}}}',
    '{"turn":2,"note":"pushback: 1","thought":"revise: the plan misses the escalation path","request":{"tool":"run_status","arguments":{"run_id":"r1"}},"response":{"isError":false,"structuredContent":{"status":"completed"}}}',
  ].join('\n');

  const metadata = {
    batchId: 'b1', scenarioId: 'priya', inputSeed: 'priya', adapterVersion: 'priya.mcp@def',
    personaCoreVersion: 'priya@abc', targetRevision: 'stdio', surface: 'mcp', runId: 'run-1',
    timestamp: '2026-07-15T03:20:11.500Z', persona: 'Priya Nair',
  };

  // Injected judge returns a schema-valid verdict, echoing the join-key metadata.
  const judge = async ({ metadata: meta }) => ({
    ok: true,
    verdict: {
      schema: 'agentweaver.persona-judge-verdict/v1',
      persona: meta.persona ?? null,
      batchId: meta.batchId, scenarioId: meta.scenarioId, inputSeed: meta.inputSeed,
      adapterVersion: meta.adapterVersion, personaCoreVersion: meta.personaCoreVersion,
      targetRevision: meta.targetRevision, surface: meta.surface, runId: meta.runId, timestamp: meta.timestamp,
      p0: { verdict: 'PASS', evidence: 'all calls succeeded' },
      p1: { verdict: 'PASS', evidence: 'plan met criteria', criteriaCoverage: [] },
      frustration: { level: 'none', score: 0, signals: [], rationale: 'no friction observed' },
      pushback: { count: 1, requirementMet: true, each: [] },
      cannotDetermine: [],
      findings: [],
    },
  });

  const outPath = path.join(dir, 'verdict.json');
  const result = await finalizeVerdict({
    transcriptText,
    persona: { name: 'Priya Nair', text: 'brief', adapter: { content: 'adapter' }, content: 'core' },
    metadata,
    capability: { available: true, report: { ok: true, results: [] } },
    outPath, judge,
  });

  assert.equal(result.verdictPath, outPath);
  assert.equal(result.verdict.surface, 'mcp');
  assert.equal(result.verdict.p0.verdict, 'PASS');
  const written = JSON.parse(await readFile(outPath, 'utf8'));
  assert.equal(written.scenarioId, 'priya');
  assert.equal(written.batchId, 'b1');
  await rm(dir, { recursive: true, force: true });
});

test('prepareJudgeEvidence adapts MCP evidence and builds the shared Judge prompt without judging', () => {
  const metadata = {
    batchId: 'b1', scenarioId: 'priya', inputSeed: 'priya', adapterVersion: 'priya.mcp@def',
    personaCoreVersion: 'priya@abc', targetRevision: 'stdio', surface: 'mcp', runId: 'run-1',
    timestamp: '2026-07-15T03:20:11.500Z', persona: 'Priya Nair',
  };
  const prepared = prepareJudgeEvidence({
    transcriptText: '{"turn":1,"thought":"submit","request":{"tool":"run_submit","arguments":{"project_id":"p","task":"triage"}},"response":{"isError":false,"structuredContent":{"run_id":"r1","status":"completed"}}}',
    persona: { name: 'Priya Nair', text: 'brief', adapter: { content: 'adapter' }, content: 'core' },
    metadata,
    capability: { available: true, report: { ok: true, results: [] } },
  });

  assert.equal(prepared.normalized.metadata.surface, 'mcp');
  assert.equal(prepared.normalized.turns.length, 1);
  assert.match(prepared.prompt, /# TASK: Judge one normalized harness run/);
  assert.match(prepared.prompt, /"scenarioId": "priya"/);
  assert.equal(prepared.p0.failedTurns.length, 0);
});

test('finalize export mode writes MCP-adapted evidence and a shared Judge prompt without a verdict', async () => {
  const dir = await scratchDir();
  const transcript = path.join(dir, 'transcript.jsonl');
  const evidence = path.join(dir, 'evidence.json');
  const prompt = path.join(dir, 'prompt.txt');
  await writeFile(transcript, '{"turn":1,"request":{"tool":"run_submit","arguments":{"project_id":"p","task":"triage"}},"response":{"isError":false,"structuredContent":{"run_id":"r1","status":"completed"}}}\n');

  const { stdout } = await execFile(process.execPath, [
    path.join(TEST_DIR, '..', 'run-persona.mjs'),
    '--scenario', 'priya', '--target', 'stdio', '--no-capability-check',
    '--transcript', transcript, '--dump-evidence', evidence, '--prompt-out', prompt,
  ]);

  assert.match(stdout, /dispatch the Judge custom agent synchronously/i);
  assert.match(stdout, /save-verdict\.mjs/);
  const normalized = JSON.parse(await readFile(evidence, 'utf8'));
  assert.equal(normalized.metadata.surface, 'mcp');
  assert.equal(normalized.turns.length, 1);
  assert.match(await readFile(prompt, 'utf8'), /# TASK: Judge one normalized harness run/);
  await rm(dir, { recursive: true, force: true });
});

test('finalizeVerdict records transcript parse errors as an attachment', async () => {
  const dir = await scratchDir();
  const captured = {};
  const judge = async ({ evidence }) => {
    captured.evidence = evidence;
    return { ok: false, error: { kind: 'missing_command', message: 'no judge configured' } };
  };
  const metadata = {
    batchId: 'b1', scenarioId: 'priya', inputSeed: 'priya', adapterVersion: 'a', personaCoreVersion: 'c',
    targetRevision: 'stdio', surface: 'mcp', runId: 'run-1', timestamp: '2026-07-15T03:20:11.500Z',
  };
  const result = await finalizeVerdict({
    transcriptText: 'garbage-not-json\n{bad',
    persona: { name: 'Priya', text: 'brief' },
    metadata, outPath: path.join(dir, 'v.json'), judge,
  });
  // Judge failed => schema-valid CANNOT_DETERMINE fallback, still written.
  assert.equal(result.verdict.p0.verdict, 'CANNOT_DETERMINE');
  const attachmentKinds = captured.evidence.attachments.map((a) => a.kind);
  assert.ok(attachmentKinds.includes('transcript-parse-errors'));
  await rm(dir, { recursive: true, force: true });
});
