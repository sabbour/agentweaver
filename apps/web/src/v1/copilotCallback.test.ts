import { describe, expect, it } from 'vitest';
import {
  COPILOT_CALLBACK_MESSAGE_TYPE,
  isCopilotCallbackMessage,
  parseCopilotCallbackParameters,
  expectedCopilotCallbackState,
} from './copilotCallback';

describe('Copilot user callback contract', () => {
  it('accepts one bounded state and authorization code', () => {
    expect(parseCopilotCallbackParameters('?state=request-1&code=opaque-code')).toEqual({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      code: 'opaque-code',
    });
  });

  it('accepts only the exact access-denied outcome', () => {
    expect(parseCopilotCallbackParameters('?state=request-1&error=access_denied')).toEqual({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      error: 'access_denied',
    });
    expect(parseCopilotCallbackParameters('?state=request-1&error=server_error')).toBeNull();
  });

  it('rejects missing, duplicate, oversized, and control-character parameters', () => {
    expect(parseCopilotCallbackParameters('?code=opaque-code')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&state=b&code=opaque-code')).toBeNull();
    expect(parseCopilotCallbackParameters(`?state=${'s'.repeat(513)}&code=x`)).toBeNull();
    expect(parseCopilotCallbackParameters('?state=%0A&code=x')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&code=x&error=access_denied')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&code=x&unexpected=value')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&error=access_denied&error_description=denied')).toBeNull();
  });

  it('requires the exact configured HTTPS redirect and a single authorization state', () => {
    const callbackUri = 'https://app.example.test/auth/github/copilot-app/callback';
    expect(expectedCopilotCallbackState(
      `https://github.com/login/oauth/authorize?redirect_uri=${encodeURIComponent(callbackUri)}&state=abc`,
      callbackUri,
    )).toBe('abc');
    expect(() => expectedCopilotCallbackState(
      'https://github.com/login/oauth/authorize?redirect_uri=https%3A%2F%2Fevil.test%2Fcallback&state=abc',
      callbackUri,
    )).toThrow(/CallbackUri/);
    expect(() => expectedCopilotCallbackState(
      `http://github.com/login/oauth/authorize?redirect_uri=${encodeURIComponent(callbackUri)}&state=abc`,
      callbackUri,
    )).toThrow(/insecure/);
    expect(() => expectedCopilotCallbackState(
      `https://github.com/login/oauth/authorize?redirect_uri=${encodeURIComponent(callbackUri)}&state=a&state=b`,
      callbackUri,
    )).toThrow(/state/);
  });

  it('rejects callback messages with extra fields or malformed values', () => {
    expect(isCopilotCallbackMessage({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      code: 'opaque-code',
    })).toBe(true);
    expect(isCopilotCallbackMessage({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      error: 'access_denied',
      code: 'unexpected',
    })).toBe(false);
    expect(isCopilotCallbackMessage({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      code: 'opaque-code',
      extra: true,
    })).toBe(false);
  });
});
