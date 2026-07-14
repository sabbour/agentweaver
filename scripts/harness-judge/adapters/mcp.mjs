import { delimitUntrusted } from '../../mcp-harness/mcp-client/prompt-safety.mjs';

function evidence(value) {
  return delimitUntrusted('mcp_evidence', value);
}

function facts(exchange) {
  const mcp = exchange.mcp ?? exchange;
  return {
    isError: mcp.isError ?? exchange.isError ?? false,
    protocolErrorCode: mcp.protocolErrorCode ?? exchange.errorCode ?? null,
    latencyMs: exchange.latencyMs ?? null,
    requestId: mcp.requestId ?? exchange.requestId ?? null,
    traceId: exchange.traceId ?? null,
  };
}

/** Normalize a lossless MCP transcript or exchanges list for the shared judge. */
export function adaptMcpEvidence(raw = {}) {
  const exchanges = Array.isArray(raw.turns) ? raw.turns : (Array.isArray(raw.exchanges) ? raw.exchanges : []);
  return {
    metadata: { ...raw.metadata, surface: 'mcp' },
    persona: raw.persona ?? {},
    turns: exchanges.map((exchange, index) => ({
      id: exchange.n ?? exchange.id ?? index + 1,
      intent: exchange.thought ?? exchange.intent ?? null,
      action: exchange.toolName ?? null,
      objectiveFacts: facts(exchange),
      evidence: [
        { kind: 'request', evidence: evidence(exchange.toolArguments ?? exchange.request ?? null) },
        { kind: 'response', evidence: evidence(exchange.mcp?.structuredContent ?? exchange.response ?? null) },
        { kind: 'raw-content', evidence: evidence(exchange.mcp?.rawContent ?? null) },
        { kind: 'error', evidence: evidence(exchange.mcp?.error ?? exchange.errorMessage ?? null) },
      ],
      frustrationSignals: Array.isArray(exchange.frustrationSignals) ? exchange.frustrationSignals : [],
    })),
    findingsContext: raw.findingsContext ?? [],
    attachments: raw.attachments ?? [],
    rawSummary: raw.summary ?? null,
  };
}
