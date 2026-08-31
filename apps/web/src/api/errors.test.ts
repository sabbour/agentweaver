import { ApiError } from './client';
import { githubConnectionErrorMessage } from './errors';
import { describe, expect, it } from 'vitest';

describe('githubConnectionErrorMessage', () => {
  it.each([
    [409, { error: 'github_binding_unavailable' }],
    [401, { error: 'github_copilot_auth_required' }],
    [409, { error: 'github_copilot_connection_required' }],
  ])('returns an actionable message for GitHub connection error %s', (status, body) => {
    expect(githubConnectionErrorMessage(new ApiError(status, JSON.stringify(body)))).toMatch(/Connect GitHub|Connect your GitHub/);
  });
});
