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
  const untrusted = (kind, value) => ({
    kind,
    evidence: `--- BEGIN UNTRUSTED_UI_DATA (${kind}) ---\n${typeof value === 'string' ? value : JSON.stringify(value ?? null)}\n--- END UNTRUSTED_UI_DATA ---`,
  });
  return {
    metadata: { ...raw.metadata, surface: 'ui' },
    persona: raw.persona ?? {},
    turns: steps.map((step, index) => ({
      id: step.id ?? index + 1,
      intent: step.intent ?? null,
      action: step.action ?? null,
      objectiveFacts: {
        url: step.url ?? null,
        target: step.target ?? null,
        outcome: step.outcome ?? null,
        consoleCount: Array.isArray(step.console) ? step.console.length : 0,
        networkCount: Array.isArray(step.network) ? step.network.length : 0,
        assertions: step.assertions ?? [],
      },
      evidence: [
        untrusted('dom', step.domSnapshot),
        untrusted('screenshot-reference', { path: step.screenshotPath ?? null, hash: step.screenshotHash ?? null }),
        untrusted('console', step.console ?? []),
        untrusted('network', step.network ?? []),
        untrusted('cross-reference', step.crossReference ?? null),
        untrusted('persona-thought', step.intent ?? null),
        untrusted('action-error', step.error ?? null),
      ],
      frustrationSignals: Array.isArray(step.frustrationSignals) ? step.frustrationSignals : [],
    })),
    findingsContext: raw.findingsContext ?? [],
    attachments: raw.attachments ?? [],
    rawSummary: raw.summary ?? null,
  };
}
