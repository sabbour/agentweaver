export const COPILOT_CALLBACK_PATH = '/auth/github/copilot-app/callback';
export const COPILOT_CALLBACK_MESSAGE_TYPE = 'agentweaver.copilot-user.callback';
export const COPILOT_CALLBACK_STATE_LIMIT = 512;
export const COPILOT_CALLBACK_CODE_LIMIT = 4096;
const COPILOT_CALLBACK_PARAMETER_NAMES = new Set(['state', 'code', 'error']);

export type CopilotCallbackMessage =
  | { type: typeof COPILOT_CALLBACK_MESSAGE_TYPE; state: string; code: string }
  | { type: typeof COPILOT_CALLBACK_MESSAGE_TYPE; state: string; error: 'access_denied' };

declare global {
  interface Window {
    __AGENTWEAVER_COPILOT_CALLBACK__?: unknown;
  }
}

function isBoundedParameter(value: unknown, limit: number): value is string {
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

export function expectedCopilotCallbackState(
  authorizationUri: string,
  callbackUri: string,
): string {
  let authorization: URL;
  try {
    authorization = new URL(authorizationUri);
  } catch {
    throw new Error('The Gateway returned an invalid GitHub authorization URL.');
  }
  if (authorization.protocol !== 'https:' ||
      authorization.username ||
      authorization.password ||
      authorization.hash) {
    throw new Error('The Gateway returned an insecure GitHub authorization URL.');
  }

  const redirectUris = authorization.searchParams.getAll('redirect_uri');
  if (redirectUris.length !== 1 || redirectUris[0] !== callbackUri) {
    throw new Error(
      `Configure the Host CallbackUri as ${callbackUri} before connecting GitHub Copilot.`,
    );
  }

  const states = authorization.searchParams.getAll('state');
  if (states.length !== 1 || !isBoundedParameter(states[0], COPILOT_CALLBACK_STATE_LIMIT)) {
    throw new Error('The Gateway returned an invalid GitHub authorization state.');
  }
  return states[0];
}

export function parseCopilotCallbackParameters(search: string): CopilotCallbackMessage | null {
  const parameters = new URLSearchParams(search);
  if ([...parameters.keys()].some((key) => !COPILOT_CALLBACK_PARAMETER_NAMES.has(key))) return null;
  const states = parameters.getAll('state');
  if (states.length !== 1 || !isBoundedParameter(states[0], COPILOT_CALLBACK_STATE_LIMIT)) return null;

  const codes = parameters.getAll('code');
  const errors = parameters.getAll('error');
  if (codes.length === 1 &&
      errors.length === 0 &&
      isBoundedParameter(codes[0], COPILOT_CALLBACK_CODE_LIMIT)) {
    return {
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      code: codes[0],
    };
  }
  if (codes.length === 0 && errors.length === 1 && errors[0] === 'access_denied') {
    return {
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: states[0],
      error: 'access_denied',
    };
  }
  return null;
}

export function isCopilotCallbackMessage(value: unknown): value is CopilotCallbackMessage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const message = value as Record<string, unknown>;
  if (message.type !== COPILOT_CALLBACK_MESSAGE_TYPE ||
      !isBoundedParameter(message.state, COPILOT_CALLBACK_STATE_LIMIT)) return false;

  if (Object.hasOwn(message, 'code')) {
    return hasExactKeys(message, ['type', 'state', 'code']) &&
      isBoundedParameter(message.code, COPILOT_CALLBACK_CODE_LIMIT);
  }
  return hasExactKeys(message, ['type', 'state', 'error']) && message.error === 'access_denied';
}
