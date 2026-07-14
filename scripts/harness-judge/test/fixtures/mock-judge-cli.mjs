const mode = process.argv[2] ?? 'success';

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString('utf8');
}

await readStdin();

if (mode === 'invalid-json') {
  process.stdout.write('not valid json');
  process.exit(0);
}

if (mode === 'nonzero') {
  process.stderr.write('judge failed hard');
  process.exit(7);
}

if (mode === 'timeout') {
  await new Promise((resolve) => setTimeout(resolve, 250));
  process.stdout.write('{"schema":"late"}');
  process.exit(0);
}

process.stdout.write(JSON.stringify({
  schema: 'agentweaver.persona-judge-verdict/v1',
  persona: 'jordan',
  batchId: 'batch-1',
  scenarioId: 'scenario-a',
  inputSeed: 'seed-1',
  adapterVersion: 'ui@1',
  personaCoreVersion: 'jordan@2',
  targetRevision: 'agentweaver@rev-a',
  surface: 'ui',
  runId: 'run-1',
  timestamp: '2026-07-14T19:00:00Z',
  p0: { verdict: 'PASS', evidence: 'ok' },
  p1: { verdict: 'PASS', evidence: 'ok', criteriaCoverage: [] },
  frustration: { level: 'none', score: 0, signals: [], rationale: 'none observed' },
  pushback: { count: 2, requirementMet: true, each: [] },
  cannotDetermine: [],
  findings: [],
}));
