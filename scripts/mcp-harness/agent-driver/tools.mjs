import { appendExchange, createTranscript } from '../lib/transcript.mjs';
import { buildDriverPrompt, safeToolExchange, safeToolMenu } from '../mcp-client/prompt-safety.mjs';

export async function startMcpSession(client, metadata = {}) {
  const tools = await client.discoverTools();
  return { tools, transcript: createTranscript(metadata) };
}

export function buildPersonaTurnPrompt(persona, session) {
  return buildDriverPrompt({
    personaBrief: persona.text ?? persona.content ?? String(persona), tools: session.tools,
    previousExchanges: session.transcript.turns.map((turn) => ({ toolName: turn.toolName, result: turn.mcp.structuredContent ?? turn.mcp.rawContent, isError: turn.mcp.isError, protocolErrorCode: turn.mcp.protocolErrorCode })),
  });
}

export async function callDiscoveredTool(client, session, { toolName, arguments: args = {}, thought = null, note = null }) {
  if (!session.tools.some((tool) => tool.name === toolName)) throw new Error(`Refusing call to "${toolName}": it was not present in this session's live tools/list result`);
  const exchange = await client.callTool(toolName, args);
  appendExchange(session.transcript, { ...exchange, thought, note, ok: !exchange.isError });
  return exchange;
}

export { safeToolMenu, safeToolExchange };
