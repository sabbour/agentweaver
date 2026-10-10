const SNAPSHOTS = ['readRunStatus', 'readSessionStatus', 'readDecisions'];
const JOURNEYS = {
  'gate-answer': { operations: [...SNAPSHOTS, 'answerGate'], uiMethod: 'gate' },
  'gate-approve': { operations: [...SNAPSHOTS, 'approveGate'], uiMethod: 'gate' },
  'gate-reject': { operations: [...SNAPSHOTS, 'rejectGate'], uiMethod: 'gate' },
  message: { operations: ['readRunStatus', 'readSessionTree', 'sendMessage'], uiMethod: 'sendMessage' },
  journal: { operations: ['replayRunEvents'], uiMethod: 'replayJournal' },
};

export function requireJourneyCapabilities({ api, mcp, ui }, journey) {
  const required = Object.hasOwn(JOURNEYS, journey) ? JOURNEYS[journey] : null;
  if (!required) throw new Error('Unknown bounded P1 journey.');
  if (!(api?.operations instanceof Map) || !(mcp?.tools instanceof Map)) {
    throw new Error('Discover the actual API and MCP menus before checking journey capabilities.');
  }
  const apiOperations = journey === 'journal' ? [...required.operations, 'streamRunEvents'] : required.operations;
  const mcpTools = required.operations.map(operation => `agentweaver_${operation}`);
  const missing = [
    ...apiOperations.filter(operation => !api.operations.has(operation)).map(operation => `api:${operation}`),
    ...mcpTools.filter(tool => !mcp.tools.has(tool)).map(tool => `mcp:${tool}`),
    ...(typeof ui?.[required.uiMethod] === 'function' ? [] : [`ui:${required.uiMethod}`]),
  ];
  if (missing.length) throw new Error(`Required P1 journey capabilities are missing: ${missing.join(', ')}`);
  return { journey, apiOperations: [...apiOperations], mcpTools, uiMethod: required.uiMethod };
}
