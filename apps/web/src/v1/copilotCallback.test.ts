import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { runInNewContext } from 'node:vm';
import { describe, expect, it } from 'vitest';
import {
  COPILOT_CALLBACK_MESSAGE_TYPE,
  isCopilotCallbackMessage,
  parseCopilotCallbackParameters,
  expectedCopilotCallbackState,
} from './copilotCallback';

describe('Copilot user callback contract', () => {
  const indexHtml = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');

  function captureProductionCallback(search: string): unknown {
    const script = indexHtml.match(/<script>\s*([\s\S]*?)\s*<\/script>/)?.[1];
    if (!script) throw new Error('The production callback capture script was not found.');
    const state: {
      location: { pathname: string; search: string };
      history: { replaceState: () => void };
      __AGENTWEAVER_COPILOT_CALLBACK__?: unknown;
    } = {
      location: { pathname: '/auth/github/copilot-app/callback', search },
      history: { replaceState: () => {} },
    };
    runInNewContext(script, { window: state, URLSearchParams });
    return state.__AGENTWEAVER_COPILOT_CALLBACK__;
  }

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
    expect(parseCopilotCallbackParameters(`?state=${'s'.repeat(2049)}&code=x`)).toBeNull();
    expect(parseCopilotCallbackParameters('?state=%0A&code=x')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&code=x&error=access_denied')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&code=x&unexpected=value')).toBeNull();
    expect(parseCopilotCallbackParameters('?state=a&error=access_denied&error_description=denied')).toEqual({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'a',
      error: 'access_denied',
    });
  });

  it('accepts bounded GitHub denial diagnostics and discards them', () => {
    const query = new URLSearchParams({
      state: 'request-1',
      error: 'access_denied',
      error_description: 'The user declined.',
      error_uri: 'https://github.com/settings/connections/applications',
    });
    expect(parseCopilotCallbackParameters(`?${query}`)).toEqual({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state: 'request-1',
      error: 'access_denied',
    });
    query.set('error_uri', 'x'.repeat(2049));
    expect(parseCopilotCallbackParameters(`?${query}`)).toBeNull();
  });

  it('uses the production index script to capture the larger CLI state and denial fields', () => {
    const state = 's'.repeat(1024);
    expect(captureProductionCallback(
      `?state=${state}&error=access_denied&error_description=denied&error_uri=${encodeURIComponent('https://github.com/')}`,
    )).toEqual({
      type: COPILOT_CALLBACK_MESSAGE_TYPE,
      state,
      error: 'access_denied',
    });
    expect(captureProductionCallback(`?state=${'s'.repeat(2049)}&code=x`)).toBeUndefined();
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
