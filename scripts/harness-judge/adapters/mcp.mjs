/**
 * MCP evidence adapter contract.
 *
 * Expected future raw input:
 * {
 *   metadata: { ...required join-key fields..., persona? },
 *   persona: { ... },
 *   exchanges: [{
 *     id?, intent?, toolName?, request?, response?, isError?, errorCode?, errorMessage?, frustrationSignals?
 *   }]
 * }
 */
export function adaptMcpEvidence(raw = {}) {
  const exchanges = Array.isArray(raw.exchanges) ? raw.exchanges : [];
  return {
    metadata: { ...raw.metadata, surface: 'mcp' },
    persona: raw.persona ?? {},
    turns: exchanges.map((exchange, index) => ({
      id: exchange.id ?? index + 1,
      intent: exchange.intent ?? null,
      action: exchange.toolName ?? null,
      objectiveFacts: {
        isError: exchange.isError ?? false,
        errorCode: exchange.errorCode ?? null,
      },
      evidence: [
        { kind: 'request', evidence: JSON.stringify(exchange.request ?? null) },
        { kind: 'response', evidence: JSON.stringify(exchange.response ?? null) },
        { kind: 'error', evidence: exchange.errorMessage ?? '' },
      ].filter((item) => item.evidence !== ''),
      frustrationSignals: Array.isArray(exchange.frustrationSignals) ? exchange.frustrationSignals : [],
    })),
    findingsContext: raw.findingsContext ?? [],
    attachments: raw.attachments ?? [],
    rawSummary: raw.summary ?? null,
  };
}
