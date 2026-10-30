import { createHash } from 'node:crypto';
import { appendRedactedJsonLine } from './safe-jsonl.mjs';
import { redact } from './redaction.mjs';

/** Construct and persist the final required-probe result for an API, MCP, or UI P1 run. */
/** Probe names that require native owner evidence beyond the admitted API, MCP, and UI harnesses. */
const REQUIRED_NATIVE_PROBES = [
  {
    id: 'storage-owner-isolation',
    reason: 'The existing Storage owner lifecycle is a direct Environment contract; no admitted surface runner observes a mounted cross-owner denial and retention receipt.',
  },
  {
    id: 'network-policy-datapath',
    reason: 'Cilium policy readback does not prove datapath enforcement, and the source adapter reports DatapathEnforcementVerified=false.',
  },
  {
    id: 'sandbox-owner-lifecycle',
    reason: 'The provider-owned Environment lifecycle and exact native absence/reclaim receipts are not exposed by the admitted Gateway/MCP clients.',
  },
  {
    id: 'agenthost-image-readiness',
    reason: 'No exact provider-owned Host Pod, readiness-phase receipt, or installed positive phase-budget set is available through the admitted surface clients.',
  },
  {
    id: 'numeric-copilot-dispatch-boundaries',
    reason: 'A usage read is not an at/beyond-boundary dispatch probe; Gateway has no dispatch route for the admitted harness client, and no live boundary receipt exists.',
  },
  {
    id: 'checkpoint-restart-duplicate-fence',
    reason: 'The duplicate-fence probe needs a Host replacement with a fresh lease and retained workspace; no verified old/new lease and duplicate-fence receipts exist.',
  },
];

const NATIVE_ENVELOPES = new Map([
  ['p1-native-acceptance', new Set([
    'storage-owner-isolation',
    'network-policy-datapath',
    'sandbox-owner-lifecycle',
    'agenthost-image-readiness',
  ])],
  ['p1-dispatch-recovery-acceptance', new Set([
    'numeric-copilot-dispatch-boundaries',
    'checkpoint-restart-duplicate-fence',
  ])],
]);
const ACCEPTED_SURFACE_STATUSES = new Set(['passed', 'failed', 'blocked']);

// Sort object keys so an envelope checksum does not depend on property insertion order.
function stableJson(value) {
  if (Array.isArray(value)) return `[${value.map(stableJson).join(',')}]`;
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).sort().map(key =>
      `${JSON.stringify(key)}:${stableJson(value[key])}`).join(',')}}`;
  }
  return JSON.stringify(value);
}

/** Add a deterministic checksum so accidental changes to a saved probe envelope are rejected. */
export function sealP1NativeProbeEnvelope(envelope) {
  const { receiptSha256: _receiptSha256, ...payload } = envelope;
  return {
    ...payload,
    receiptSha256: createHash('sha256').update(stableJson(payload)).digest('hex'),
  };
}

/** Validate exact probe output envelopes and fill each absent required probe as blocked. */
function nativeProbeRows(envelopes) {
  if (!Array.isArray(envelopes)) throw new Error('Native P1 probe results must be an array of exact owner envelopes.');
  if (envelopes.length === 0) {
    return REQUIRED_NATIVE_PROBES.map(probe => ({ ...probe, required: true, status: 'blocked' }));
  }
  if (envelopes.length > NATIVE_ENVELOPES.size) {
    throw new Error('Too many P1 native probe envelopes were provided.');
  }

  const rows = [];
  const seenEnvelopes = new Set();
  for (const envelope of envelopes) {
    const expectedNames = NATIVE_ENVELOPES.get(envelope?.kind);
    const envelopeKeys = [
      'kind', 'schemaVersion', 'overallStatus', 'startedAt', 'completedAt', 'probes', 'receiptSha256',
    ];
    if (!expectedNames || seenEnvelopes.has(envelope.kind) || envelope.schemaVersion !== 1
      || !ACCEPTED_SURFACE_STATUSES.has(envelope.overallStatus) || !Array.isArray(envelope.probes)
      || Object.keys(envelope).some(key => !envelopeKeys.includes(key)
        && key !== 'ownerObservation'
        && !(envelope.kind === 'p1-dispatch-recovery-acceptance' && key === 'liveObservations'))
      || envelopeKeys.some(key => !Object.hasOwn(envelope, key))
      || ['startedAt', 'completedAt'].some(key => typeof envelope[key] !== 'string')
      || !Number.isFinite(Date.parse(envelope.startedAt))
      || !Number.isFinite(Date.parse(envelope.completedAt))
      || Date.parse(envelope.completedAt) < Date.parse(envelope.startedAt)) {
      throw new Error('A native P1 probe envelope is invalid or duplicated.');
    }
    if (!/^[a-f0-9]{64}$/.test(envelope.receiptSha256)
      || sealP1NativeProbeEnvelope(envelope).receiptSha256 !== envelope.receiptSha256) {
      throw new Error('A native P1 probe envelope checksum does not match its contents.');
    }
    if (Object.hasOwn(envelope, 'ownerObservation')
      && (!envelope.ownerObservation || typeof envelope.ownerObservation !== 'object'
        || Array.isArray(envelope.ownerObservation))) {
      throw new Error('A native P1 owner observation is invalid.');
    }
    if (Object.hasOwn(envelope, 'liveObservations')
      && (!envelope.liveObservations || typeof envelope.liveObservations !== 'object'
        || Array.isArray(envelope.liveObservations))) {
      throw new Error('Native P1 live observations are invalid.');
    }
    seenEnvelopes.add(envelope.kind);
    const names = new Set();
    for (const probe of envelope.probes) {
      const probeKeys = ['name', 'status', 'evidence', 'cleanup'];
      if (!probe || !expectedNames.has(probe.name) || names.has(probe.name)
        || !ACCEPTED_SURFACE_STATUSES.has(probe.status)
        || !probe.evidence || typeof probe.evidence !== 'object' || Array.isArray(probe.evidence)
        || !probe.cleanup || typeof probe.cleanup !== 'object' || Array.isArray(probe.cleanup)
        || Object.keys(probe).some(key => !probeKeys.includes(key) && key !== 'reason' && key !== 'code')
        || probeKeys.some(key => !Object.hasOwn(probe, key))
        || typeof probe.cleanup.status !== 'string'
        || (envelope.kind === 'p1-native-acceptance'
          ? !Array.isArray(probe.cleanup.actions) || Object.hasOwn(probe.cleanup, 'createdResourceIds')
          : !Array.isArray(probe.cleanup.createdResourceIds) || Object.hasOwn(probe.cleanup, 'actions'))) {
        throw new Error('A native P1 probe result is invalid or duplicated.');
      }
      // Passing requires retained evidence and a cleanup state that proves no work remains.
      if (probe.status === 'passed') {
        const cleanupPassed = envelope.kind === 'p1-native-acceptance'
          ? ['complete', 'not-required'].includes(probe.cleanup.status)
            && (probe.cleanup.status !== 'not-required' || probe.cleanup.actions.length === 0)
            && probe.cleanup.actions.every(action => action?.status === 'complete')
          : probe.cleanup.status === 'not-started' && probe.cleanup.createdResourceIds.length === 0;
        if (Object.keys(probe.evidence).length === 0 || !cleanupPassed) {
          throw new Error('A passed native P1 probe lacks evidence or confirmed cleanup.');
        }
      }
      names.add(probe.name);
      rows.push({
        id: probe.name,
        required: true,
        status: probe.status,
        ...(envelope.startedAt === undefined ? {} : { startedAt: envelope.startedAt }),
        ...(envelope.completedAt === undefined ? {} : { completedAt: envelope.completedAt }),
        ...(probe.reason === undefined ? {} : { reason: probe.reason }),
        ...(probe.code === undefined ? {} : { code: probe.code }),
        evidence: probe.evidence,
        cleanup: probe.cleanup,
      });
    }
    if (names.size !== expectedNames.size || [...expectedNames].some(name => !names.has(name))) {
      throw new Error(`The ${envelope.kind} envelope is missing a required P1 probe.`);
    }
    const computedStatus = rows.slice(-names.size).some(probe => probe.status === 'failed')
      ? 'failed'
      : rows.slice(-names.size).some(probe => probe.status === 'blocked') ? 'blocked' : 'passed';
    if (computedStatus !== envelope.overallStatus) {
      throw new Error(`The ${envelope.kind} overall status does not match its probe results.`);
    }
  }
  return REQUIRED_NATIVE_PROBES.map(required =>
    rows.find(probe => probe.id === required.id)
    ?? { ...required, required: true, status: 'blocked' });
}

/** Check supplied envelopes without treating absent envelope kinds as valid evidence. */
export function validateP1NativeProbeResults(envelopes) {
  nativeProbeRows(envelopes);
  return envelopes;
}

/**
 * Combine surface execution with optional native probe results into a redacted P1 receipt.
 * Required failed probes fail the overall result; missing or blocked probes prevent a pass.
 */
export function createP1AcceptanceResult({
  surface,
  surfaceStatus,
  steps = [],
  nativeProbeResults = [],
  observations = {},
  failure,
}) {
  if (!['api', 'mcp', 'ui'].includes(surface) || !ACCEPTED_SURFACE_STATUSES.has(surfaceStatus)) {
    throw new Error('A known P1 surface and explicit surface result are required.');
  }
  const probes = nativeProbeRows(nativeProbeResults);
  const fullP1Status = surfaceStatus === 'failed' || probes.some(probe => probe.required && probe.status === 'failed')
    ? 'failed'
    : probes.some(probe => probe.required && probe.status !== 'passed') || surfaceStatus === 'blocked'
      ? 'blocked'
      : 'passed';
  return redact({
    kind: 'p1-acceptance-result',
    schemaVersion: 1,
    surface,
    surfaceStatus,
    fullP1Status,
    exitCode: fullP1Status === 'failed' ? 1 : fullP1Status === 'blocked' ? 2 : 0,
    steps,
    observations,
    probes,
    ...(failure ? { failure: redact(failure) } : {}),
  });
}

/** Persist transcripts, probe dispositions, and the final result as redacted JSON Lines records. */
export async function persistP1AcceptanceResult(file, result, transcripts = []) {
  if (typeof file !== 'string' || !file || result?.kind !== 'p1-acceptance-result') {
    throw new Error('A durable evidence path and P1 result are required.');
  }
  for (const transcript of transcripts) {
    await appendRedactedJsonLine(file, { kind: 'p1-transcript', ...transcript });
  }
  for (const probe of result.probes) {
    await appendRedactedJsonLine(file, {
      kind: 'p1-probe-disposition',
      surface: result.surface,
      fullP1Status: result.fullP1Status,
      ...probe,
    });
  }
  await appendRedactedJsonLine(file, result);
  return result;
}
