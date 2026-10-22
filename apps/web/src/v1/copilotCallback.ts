/** Browser route that captures the GitHub Copilot callback before mounting the app. */
export const COPILOT_CALLBACK_PATH = '/auth/github/copilot-app/callback';
/** Same-origin popup message discriminator for the Copilot authorization result. */
export const COPILOT_CALLBACK_MESSAGE_TYPE = 'agentweaver.copilot-user.callback';
/** Maximum accepted OAuth state length. */
export const COPILOT_CALLBACK_STATE_LIMIT = 512;
/** Maximum accepted authorization-code length. */
export const COPILOT_CALLBACK_CODE_LIMIT = 4096;
const COPILOT_CALLBACK_PARAMETER_NAMES = new Set(['state', 'code', 'error']);

/** Exact success or user-denial payload relayed from the GitHub Copilot callback window. */
export type CopilotCallbackMessage =
  | { type: typeof COPILOT_CALLBACK_MESSAGE_TYPE; state: string; code: string }
  | { type: typeof COPILOT_CALLBACK_MESSAGE_TYPE; state: string; error: 'access_denied' };

declare global {
  interface Window {
    __AGENTWEAVER_COPILOT_CALLBACK__?: unknown;
  }
}

/** Rejects empty, oversized, or control-character callback values before relaying them. */
function isBoundedParameter(value: unknown, limit: number): value is string {
  return typeof value === 'string' &&
    value.length > 0 &&
    value.length <= limit &&
    !value.split('').some((character) => {
      const code = character.charCodeAt(0);
      return code <= 0x1f || code === 0x7f;
    });
}

/** Ensures callback objects contain only the explicitly allowed fields. */
function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}

/** Extracts the unique state only when the authorization URL targets the expected secure callback. */
export function expectedCopilotCallbackState(
  authorizationUri: string,
  callbackUri: string,
): string {
  let authorization: URL;
  try {
    authorization = new URL(authorizationUri);
  } catch {
    throw new Error('The server returned an invalid GitHub authorization URL.');
  }
  if (authorization.protocol !== 'https:' ||
      authorization.username ||
      authorization.password ||
      authorization.hash) {
    throw new Error('The server returned an insecure GitHub authorization URL.');
  }

  const redirectUris = authorization.searchParams.getAll('redirect_uri');
  if (redirectUris.length !== 1 || redirectUris[0] !== callbackUri) {
    throw new Error(
      `Configure the Host CallbackUri as ${callbackUri} before connecting GitHub Copilot.`,
    );
  }

  const states = authorization.searchParams.getAll('state');
  if (states.length !== 1 || !isBoundedParameter(states[0], COPILOT_CALLBACK_STATE_LIMIT)) {
    throw new Error('The server returned an invalid GitHub authorization state.');
  }
  return states[0];
}

/** Parses the allowed callback query fields and rejects duplicates or unrelated parameters. */
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

/** Validates a relayed popup message against the callback's exact bounded contract. */
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
