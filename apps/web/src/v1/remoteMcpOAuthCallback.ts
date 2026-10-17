/** Browser route that captures the Remote MCP provider's OAuth return. */
export const REMOTE_MCP_OAUTH_CALLBACK_PATH = '/auth/remote-mcp/oauth/callback';
/** Same-origin popup message discriminator for a Remote MCP OAuth response. */
export const REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE = 'agentweaver.remote-mcp.oauth.callback';
/** Exact base64url-like state length required for the callback transaction. */
export const REMOTE_MCP_OAUTH_STATE_PATTERN = /^[A-Za-z0-9_-]{43}$/;
/** Maximum accepted authorization-code length. */
export const REMOTE_MCP_OAUTH_CODE_LIMIT = 4096;

const CALLBACK_PARAMETER_NAMES = new Set(['state', 'code', 'error']);

/** Success or denial payload passed from a remote MCP provider callback popup. */
export type RemoteMcpOAuthCallbackMessage =
  | {
    type: typeof REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE;
    state: string;
    code: string;
    error?: never;
  }
  | {
    type: typeof REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE;
    state: string;
    error: 'access_denied';
    code?: never;
  };

/** Rejects empty, oversized, or control-character OAuth callback values. */
function isSafeValue(value: unknown, limit: number): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= limit &&
    !value.split('').some((character) => {
      const code = character.charCodeAt(0);
      return code <= 0x1f || code === 0x7f;
    });
}

/** Ensures a relayed callback contains no fields beyond the selected variant. */
function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

/** Accepts only one bounded state plus either one code or the explicit access-denied result. */
export function parseRemoteMcpOAuthCallbackParameters(
  search: string,
): RemoteMcpOAuthCallbackMessage | null {
  const parameters = new URLSearchParams(search);
  if ([...parameters.keys()].some((key) => !CALLBACK_PARAMETER_NAMES.has(key))) return null;

  const states = parameters.getAll('state');
  const codes = parameters.getAll('code');
  const errors = parameters.getAll('error');
  if (states.length !== 1 ||
      !REMOTE_MCP_OAUTH_STATE_PATTERN.test(states[0]))
    return null;

  if (codes.length === 1 && errors.length === 0 &&
      isSafeValue(codes[0], REMOTE_MCP_OAUTH_CODE_LIMIT))
    return {
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      code: codes[0],
    };
  if (codes.length === 0 && errors.length === 1 && errors[0] === 'access_denied')
    return {
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      error: 'access_denied',
    };
  return null;
}

/** Narrows a popup message to the exact Remote MCP callback contract. */
export function isRemoteMcpOAuthCallbackMessage(
  value: unknown,
): value is RemoteMcpOAuthCallbackMessage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const message = value as Record<string, unknown>;
  if (message.type !== REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE ||
      typeof message.state !== 'string' ||
      !REMOTE_MCP_OAUTH_STATE_PATTERN.test(message.state))
    return false;
  return hasExactKeys(message, ['type', 'state', 'code'])
    ? isSafeValue(message.code, REMOTE_MCP_OAUTH_CODE_LIMIT)
    : hasExactKeys(message, ['type', 'state', 'error']) && message.error === 'access_denied';
}
