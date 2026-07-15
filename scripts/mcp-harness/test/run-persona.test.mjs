import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, readFile, rm } from 'node:fs/promises';
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
  finalizeVerdict,
} from '../run-persona.mjs';

const TEST_DIR = path.dirname(fileURLToPath(import.meta.url));
// Scratch outputs live under the harness's gitignored verdicts/ dir (never /tmp), and are
// removed after each use.
async function scratchDir() {
  const dir = path.join(TEST_DIR, '..', 'verdicts', `test-${randomUUID().slice(0, 8)}`);
  await mkdir(dir, { recursive: true });
  return dir;
}

test('parseArgs maps the api-parity CLI shape', () => {
  const args = parseArgs([
    '--scenario', 'priya', '--target', 'http://localhost:5000/mcp', '--token', 't',
    '--project-id', 'proj-1', '--batch-id', 'b1', '--seed', 's1', '--out', 'v.json',
    '--transcript', 'tr.jsonl', '--server-command', 'dotnet', '--server-args', '["run"]',
    '--allow-prod', '--i-understand-prod',
  ]);
  assert.equal(args.scenario, 'priya');
  assert.equal(args.target, 'http://localhost:5000/mcp');
  assert.equal(args.token, 't');
  assert.equal(args.projectId, 'proj-1');
  assert.equal(args.batchId, 'b1');
  assert.equal(args.seed, 's1');
  assert.equal(args.out, 'v.json');
  assert.equal(args.transcript, 'tr.jsonl');
  assert.equal(args.serverCommand, 'dotnet');
  assert.equal(args.serverArgs, '["run"]');
  assert.equal(args.allowProd, true);
  assert.equal(args.confirmProduction, true);
});

test('parseArgs accepts --persona and --base-url aliases', () => {
  const args = parseArgs(['--persona', 'jordan', '--base-url', 'stdio']);
  assert.equal(args.scenario, 'jordan');
  assert.equal(args.target, 'stdio');
});

test('resolveTransport treats stdio as a sentinel and any URL as http', () => {
  assert.deepEqual(resolveTransport({ target: 'stdio' }), { mode: 'stdio', target: 'stdio' });
  assert.deepEqual(resolveTransport({}), { mode: 'stdio', target: 'stdio' });
  assert.deepEqual(resolveTransport({ target: 'http://localhost:5000/mcp' }), { mode: 'http', target: 'http://localhost:5000/mcp' });
});

test('checkTargetAllowed exempts stdio and enforces the guard for http', () => {
  assert.equal(checkTargetAllowed({ mode: 'stdio', target: 'stdio' }, {}), null);
  assert.equal(checkTargetAllowed({ mode: 'http', target: 'http://localhost:5000/mcp' }, {}), null);
  assert.equal(checkTargetAllowed({ mode: 'http', target: 'https://mcp.staging.example.test/mcp' }, {}), null);
  const prod = checkTargetAllowed({ mode: 'http', target: 'https://prod.example.test/mcp' }, {});
  assert.match(prod, /--allow-prod/);
  assert.equal(
    checkTargetAllowed({ mode: 'http', target: 'https://prod.example.test/mcp' }, { allowProd: true, confirmProduction: true }),
    null,
  );
});

test('buildSessionId and buildTranscriptPath are deterministic for a fixed clock', () => {
  const now = new Date('2026-07-15T03:20:11.500Z');
  const sessionId = buildSessionId({ scenario: 'priya', now });
  assert.equal(sessionId, 'priya-live-2026-07-15T03-20-11-500Z');
  const transcriptPath = buildTranscriptPath({ sessionId, dir: '/tmp/transcripts' });
  assert.equal(path.basename(transcriptPath), 'priya-live-2026-07-15T03-20-11-500Z.jsonl');
});

test('buildDispatchPrompt embeds the charter, brief, target and transcript path, but never a fixed tool sequence', () => {
  const persona = { id: 'priya', name: 'Priya Nair', text: 'CORE + ADAPTER BRIEF TEXT' };
  const prompt = buildDispatchPrompt({
    persona,
    transport: { mode: 'http', target: 'https://mcp.staging.example.test/mcp' },
    token: 'secret-token',
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

test('runCapabilityCheck fails closed without a client and runs the contract with one', async () => {
  const none = await runCapabilityCheck({});
  assert.equal(none.available, false);

  const fakeClient = {
    discoverTools: async () => ([
      { name: 'run_submit', inputSchema: { type: 'object', required: ['project_id', 'task'], properties: { project_id: { type: 'string' }, task: { type: 'string' } } }, outputSchema: { type: 'object', required: ['run_id', 'status'], properties: { run_id: { type: 'string' }, status: { type: 'string' } } } },
      { name: 'run_status', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } }, outputSchema: { type: 'object', required: ['status'], properties: { status: { type: 'string' } } } },
      { name: 'run_show_artifacts', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } }, outputSchema: { type: 'object', required: ['artifacts'], properties: { artifacts: { type: 'array' } } } },
      { name: 'run_archive', inputSchema: { type: 'object', required: ['run_id'], properties: { run_id: { type: 'string' } } } },
      { name: 'github_status' }, { name: 'github_signin' }, { name: 'diagnostics_get' },
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
