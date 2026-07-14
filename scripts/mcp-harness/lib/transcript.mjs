import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';

export const MCP_TRANSCRIPT_SCHEMA = 'agentweaver.mcp-transcript/v1';

export function createTranscript(metadata = {}) {
  return { schema: MCP_TRANSCRIPT_SCHEMA, sessionId: metadata.sessionId ?? `mcp-${randomUUID()}`, startedAt: new Date().toISOString(), metadata, turns: [] };
}

export function appendExchange(transcript, exchange) {
  const turn = {
    n: transcript.turns.length + 1, at: new Date().toISOString(), sessionId: transcript.sessionId,
    actor: exchange.actor ?? 'persona', thought: exchange.thought ?? null, toolName: exchange.toolName ?? null,
    toolArguments: exchange.toolArguments ?? null, traceId: exchange.traceId ?? null,
    mcp: { requestId: exchange.requestId ?? null, isError: exchange.isError ?? false, protocolErrorCode: exchange.protocolErrorCode ?? null, structuredContent: exchange.structuredContent ?? null, rawContent: exchange.rawContent ?? null },
    latencyMs: exchange.latencyMs ?? null,
    outcome: { ok: exchange.ok ?? !exchange.isError, isError: exchange.isError ?? false, protocolErrorCode: exchange.protocolErrorCode ?? null },
    note: exchange.note ?? null,
  };
  transcript.turns.push(turn);
  return turn;
}

export async function writeTranscript(transcript, directory) {
  await mkdir(directory, { recursive: true });
  const file = path.join(directory, `${transcript.sessionId}.json`);
  await writeFile(file, `${JSON.stringify(transcript, null, 2)}\n`, 'utf8');
  return file;
}
