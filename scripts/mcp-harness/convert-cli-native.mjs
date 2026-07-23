// One-off converter: turn a real `copilot -p ... --output-format json` event stream
// (captured from a genuine Copilot CLI subprocess with the Agentweaver MCP server
// registered via --additional-mcp-config) into the harness's transcript JSONL shape
// consumed by run-persona.mjs finalize (`{turn, ts, thought, request, response}` per line).
//
// This is NOT part of the harness's normal flow (which dispatches a sub-agent using a
// hand-rolled MCP client). It exists specifically to prove Copilot CLI's OWN native MCP
// client integration works end-to-end, by capturing a real persona run driven through it
// and judging that evidence with the harness's existing judge pipeline.
import { readFile, writeFile } from 'node:fs/promises';

const [, , inPath, outPath, prefix = 'aw-staging-'] = process.argv;
if (!inPath || !outPath) {
  console.error('usage: node convert-cli-native.mjs <raw-jsonl> <out-jsonl> [toolPrefix]');
  process.exit(2);
}

const raw = await readFile(inPath, 'utf8');
const lines = raw.split(/\r?\n/).filter(Boolean);
const events = lines.map((l) => {
  try { return JSON.parse(l); } catch { return null; }
}).filter(Boolean);

const starts = new Map(); // toolCallId -> start event
const reasoningByTurn = new Map(); // turnId -> last reasoning text seen before the call

let lastReasoning = null;
for (const ev of events) {
  if (ev.type === 'assistant.reasoning' && ev.data?.content) {
    lastReasoning = ev.data.content;
  }
  if (ev.type === 'tool.execution_start' && String(ev.data?.toolName ?? '').startsWith(prefix)) {
    starts.set(ev.data.toolCallId, { ev, thought: lastReasoning });
  }
}

const outLines = [];
let turn = 0;
for (const ev of events) {
  if (ev.type !== 'tool.execution_complete') continue;
  const startEntry = starts.get(ev.data?.toolCallId);
  if (!startEntry) continue; // not an aw-staging-* tool call
  turn += 1;
  const toolName = startEntry.ev.data.toolName.slice(prefix.length);
  const args = startEntry.ev.data.arguments ?? {};
  const result = ev.data?.result ?? {};
  const isError = ev.data?.success === false;
  // On success, the real MCP JSON payload lives in result.content (or result.detailedContent);
  // parse it into structuredContent when it looks like JSON so the judge sees real data, not
  // an opaque string. On failure, capture the real MCP server error message/hint verbatim.
  let structuredContent = null;
  const contentStr = typeof result.content === 'string' ? result.content : null;
  if (!isError && contentStr) {
    try { structuredContent = JSON.parse(contentStr); } catch { structuredContent = contentStr; }
  }
  outLines.push(JSON.stringify({
    turn,
    ts: ev.timestamp,
    thought: startEntry.thought,
    request: { tool: toolName, arguments: args },
    response: {
      isError,
      protocolErrorCode: null,
      structuredContent,
      rawContent: contentStr ?? result.detailedContent ?? null,
      error: isError ? (ev.data?.error?.message ?? 'tool execution failed') : null,
    },
  }));
}

await writeFile(outPath, `${outLines.join('\n')}\n`, 'utf8');
console.log(`Wrote ${outLines.length} turns to ${outPath}`);
