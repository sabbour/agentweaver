import { describe, expect, it } from 'vitest';
import {
  isRemoteMcpOAuthCallbackMessage,
  parseRemoteMcpOAuthCallbackParameters,
  REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
} from './remoteMcpOAuthCallback';

const state = 'A'.repeat(43);

describe('Remote MCP OAuth callback contract', () => {
  it('accepts one state and one bounded authorization code', () => {
    expect(parseRemoteMcpOAuthCallbackParameters(
      `?state=${state}&code=opaque-code`,
    )).toEqual({
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state,
      code: 'opaque-code',
    });
  });

  it('rejects missing, duplicate, unexpected, and unsafe query parameters', () => {
    expect(parseRemoteMcpOAuthCallbackParameters('?code=opaque-code')).toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(`?state=${state}&state=${state}&code=x`))
      .toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(`?state=${state}&code=x&error=access_denied`))
      .toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(`?state=${state}&code=x&extra=y`)).toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(`?state=${state}&code=%0A`)).toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(`?state=short&code=x`)).toBeNull();
    expect(parseRemoteMcpOAuthCallbackParameters(
      `?state=${state}&code=${'x'.repeat(4097)}`,
    )).toBeNull();
  });

  it('accepts only exact same-flow callback messages', () => {
    expect(isRemoteMcpOAuthCallbackMessage({
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state,
      code: 'opaque-code',
    })).toBe(true);
    expect(isRemoteMcpOAuthCallbackMessage({
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state,
      code: 'opaque-code',
      accessToken: 'not-allowed',
    })).toBe(false);
    expect(isRemoteMcpOAuthCallbackMessage({
      type: REMOTE_MCP_OAUTH_CALLBACK_MESSAGE_TYPE,
      state: 'short',
      code: 'opaque-code',
    })).toBe(false);
  });
});
