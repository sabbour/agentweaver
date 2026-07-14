/**
 * API evidence adapter contract.
 *
 * Input (expected from the future API harness):
 * {
 *   metadata: { batchId, scenarioId, inputSeed, adapterVersion, personaCoreVersion, targetRevision, runId, timestamp, persona? },
 *   persona: { name?, briefText?, authoredCriteriaText?, surfaceAdapterText? },
 *   turns: [{ n?, intent?, action?, request?, response?, latencyMs?, upstreamMs?, outcome?, note? }],
 *   findingsContext?: any[],
 *   attachments?: any[]
 * }
 *
 * Output (shared judge evidence shape consumed by core.mjs):
 * {
 *   metadata,
 *   persona,
 *   turns: [{ id, intent, action, objectiveFacts, evidence, frustrationSignals }],
 *   findingsContext,
 *   attachments,
 *   rawSummary
 * }
 */
export function adaptApiEvidence(raw = {}) {
  const turns = Array.isArray(raw.turns) ? raw.turns : [];
  return {
    metadata: { ...raw.metadata, surface: 'api' },
    persona: raw.persona ?? {},
    turns: turns.map((turn, index) => ({
      id: turn.n ?? index + 1,
      intent: turn.intent ?? turn.thought ?? null,
      action: turn.action ?? null,
      objectiveFacts: {
        httpStatus: turn.response?.status ?? null,
        latencyMs: turn.latencyMs ?? null,
        upstreamMs: turn.upstreamMs ?? null,
        outcome: turn.outcome ?? null,
      },
      evidence: [
        { kind: 'request', evidence: JSON.stringify(turn.request ?? null) },
        { kind: 'response', evidence: JSON.stringify(turn.response ?? null) },
      ],
      frustrationSignals: [],
    })),
    findingsContext: raw.findingsContext ?? [],
    attachments: raw.attachments ?? [],
    rawSummary: raw.summary ?? null,
  };
}
