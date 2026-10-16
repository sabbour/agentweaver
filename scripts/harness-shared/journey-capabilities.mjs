const SNAPSHOTS = ['readRunStatus', 'readSessionStatus', 'readDecisions'];
const JOURNEYS = {
  'gate-answer': {
    apiOperations: [...SNAPSHOTS, 'answerGate'],
    mcpTools: [...SNAPSHOTS, 'answerGate'].map(operation => `agentweaver_${operation}`),
    uiMethod: 'gate',
  },
  'gate-approve': {
    apiOperations: [...SNAPSHOTS, 'approveGate'],
    mcpTools: [...SNAPSHOTS, 'approveGate'].map(operation => `agentweaver_${operation}`),
    uiMethod: 'gate',
  },
  'gate-reject': {
    apiOperations: [...SNAPSHOTS, 'rejectGate'],
    mcpTools: [...SNAPSHOTS, 'rejectGate'].map(operation => `agentweaver_${operation}`),
    uiMethod: 'gate',
  },
  message: {
    apiOperations: ['readRunStatus', 'readSessionTree', 'sendMessage'],
    mcpTools: ['readRunStatus', 'readSessionTree', 'sendMessage'].map(operation => `agentweaver_${operation}`),
    uiMethod: 'sendMessage',
  },
  journal: {
    apiOperations: ['replayRunEvents'],
    mcpTools: ['agentweaver_replayRunEvents'],
    uiMethod: 'replayJournal',
  },
};

export function requireJourneyCapabilities({ api, mcp, ui }, journey, surface) {
  const required = Object.hasOwn(JOURNEYS, journey) ? JOURNEYS[journey] : null;
  if (!required) throw new Error('Unknown bounded P1 journey.');
  if (surface !== undefined && !['api', 'mcp', 'ui'].includes(surface)) {
    throw new Error('A known P1 surface is required for capability checks.');
  }
  const checkApi = surface === undefined || surface === 'api' && required.apiOperations.length > 0;
  const checkMcp = surface === undefined || surface === 'mcp' && required.mcpTools.length > 0;
  const checkUi = surface === undefined || surface === 'ui';
  if (checkApi && !(api?.operations instanceof Map)) {
    throw new Error('Discover the actual API menu before checking journey capabilities.');
  }
  if (checkMcp && !(mcp?.tools instanceof Map)) {
    throw new Error('Discover the actual MCP menu before checking journey capabilities.');
  }
  const missing = [
    ...(checkApi
      ? required.apiOperations.filter(operation => !api.operations.has(operation)).map(operation => `api:${operation}`)
      : []),
    ...(checkMcp
      ? required.mcpTools.filter(tool => !mcp.tools.has(tool)).map(tool => `mcp:${tool}`)
      : []),
    ...(checkUi
      ? (typeof ui?.[required.uiMethod] === 'function' ? [] : [`ui:${required.uiMethod}`])
      : []),
  ];
  if (missing.length) throw new Error(`Required P1 journey capabilities are missing: ${missing.join(', ')}`);
  return {
    journey,
    apiOperations: surface === undefined || surface === 'api' ? [...required.apiOperations] : [],
    mcpTools: surface === undefined || surface === 'mcp' ? [...required.mcpTools] : [],
    uiMethod: required.uiMethod,
  };
}
