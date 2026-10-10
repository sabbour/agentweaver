export const REMOTE_MCP_OAUTH_CALLBACK_PATH = '/auth/remote-mcp/oauth/callback';
export const REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE = 'agentweaver.remote-mcp.oauth.callback';
export const REMOTE_MCP_OAUTH_STATE_PATTERN = /^[A-Za-z0-9_-]{43}$/;
export const REMOTE_MCP_OAUTH_CODE_LIMIT = 4096;

const CALLBACK_PARAMETER_NAMES = new Set(['state', 'code']);

export interface RemoteMcpOAuthCallbackMessage {
  type: typeof REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE;
  state: string;
  code: string;
}

function isSafeValue(value: unknown, limit: number): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= limit &&
    !value.split('').some((character) => {
      const code = character.charCodeAt(0);
      return code <= 0x1f || code === 0x7f;
    });
}

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

export function parseRemoteMcpOAuthCallbackParameters(
  search: string,
): RemoteMcpOAuthCallbackMessage | null {
  const parameters = new URLSearchParams(search);
  if ([...parameters.keys()].some((key) => !CALLBACK_PARAMETER_NAMES.has(key))) return null;

  const states = parameters.getAll('state');
  const codes = parameters.getAll('code');
  if (states.length !== 1 ||
      !REMOTE_MCP_OAUTH_STATE_PATTERN.test(states[0]) ||
      codes.length !== 1 ||
      !isSafeValue(codes[0], REMOTE_MCP_OAUTH_CODE_LIMIT))
    return null;

  return {
    type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
    state: states[0],
    code: codes[0],
  };
}

export function isRemoteMcpOAuthCallbackMessage(
  value: unknown,
): value is RemoteMcpOAuthCallbackMessage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const message = value as Record<string, unknown>;
  return hasExactKeys(message, ['type', 'state', 'code']) &&
    message.type === REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE &&
    typeof message.state === 'string' &&
    REMOTE_MCP_OAUTH_STATE_PATTERN.test(message.state) &&
    isSafeValue(message.code, REMOTE_MCP_OAUTH_CODE_LIMIT);
}
