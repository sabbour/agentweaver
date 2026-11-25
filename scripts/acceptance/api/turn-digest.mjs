export const TURN_READ_OPERATION = 'readSessionTurnContent';
export const TURN_READ_ROUTE = '/api/v1/projects/{projectId}/runs/{runId}/sessions/{sessionId}/turns/{eventId}';
const TURN_DIGEST_FLAG = '--turn-content-sha256';

export function hasTurnDigestHelp(stdout) {
  const usage = `agentweaver api invoke ${TURN_READ_OPERATION} ${TURN_DIGEST_FLAG}`;
  return stdout.split(/\r?\n/).some(line => line.trim() === usage);
}

export function supportsTurnDigest(operation) {
  const expected = new Set(['projectId', 'runId', 'sessionId', 'eventId']);
  return operation?.operationId === TURN_READ_OPERATION
    && operation.method === 'GET' && operation.path === TURN_READ_ROUTE
    && operation.requestBody?.required !== true
    && Array.isArray(operation.parameters)
    && operation.responses?.['200']?.content?.['application/json']?.schema?.$ref
      === '#/components/schemas/SessionTurnContent'
    && !operation.parameters.some(parameter => {
      if (parameter?.in === 'path') {
        return parameter.required !== true || !expected.delete(parameter.name)
          || parameter.schema?.type !== 'string'
          || (parameter.name === 'eventId' && parameter.schema.format !== 'uuid');
      }
      return parameter?.required === true
        && !(parameter.in === 'header' && typeof parameter.name === 'string'
          && parameter.name.toLowerCase() === 'x-agentweaver-tenant');
    })
    && expected.size === 0;
}

export function buildTurnDigestCommand(identityArgs, { projectId, runId, sessionId, eventId }) {
  return {
    id: 'api-invoke-turn-digest',
    args: [
      'api', 'invoke', TURN_READ_OPERATION, TURN_DIGEST_FLAG,
      ...identityArgs,
      '--path', `projectId=${projectId}`,
      '--path', `runId=${runId}`,
      '--path', `sessionId=${sessionId}`,
      '--path', `eventId=${eventId}`,
    ],
  };
}
