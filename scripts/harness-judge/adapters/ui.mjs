/**
 * UI evidence adapter contract.
 *
 * Expected future raw input:
 * {
 *   metadata: { ...required join-key fields..., persona? },
 *   persona: { ... },
 *   steps: [{
 *     id?, intent?, action?, url?, domSnapshot?, screenshotPath?, console?, network?, frustrationSignals?
 *   }],
 *   attachments?: [{ kind:'screenshot'|'dom'|'trace', path:string }]
 * }
 */
export function adaptUiEvidence(raw = {}) {
  const steps = Array.isArray(raw.steps) ? raw.steps : [];
  return {
    metadata: { ...raw.metadata, surface: 'ui' },
    persona: raw.persona ?? {},
    turns: steps.map((step, index) => ({
      id: step.id ?? index + 1,
      intent: step.intent ?? null,
      action: step.action ?? null,
      objectiveFacts: {
        url: step.url ?? null,
        consoleCount: Array.isArray(step.console) ? step.console.length : 0,
        networkCount: Array.isArray(step.network) ? step.network.length : 0,
      },
      evidence: [
        { kind: 'dom', evidence: typeof step.domSnapshot === 'string' ? step.domSnapshot : JSON.stringify(step.domSnapshot ?? null) },
        { kind: 'screenshot', evidence: step.screenshotPath ?? '(no screenshot)' },
      ],
      frustrationSignals: Array.isArray(step.frustrationSignals) ? step.frustrationSignals : [],
    })),
    findingsContext: raw.findingsContext ?? [],
    attachments: raw.attachments ?? [],
    rawSummary: raw.summary ?? null,
  };
}
