import { ApiError } from './client';
import { githubConnectionErrorMessage, isGitHubRepoAppConnectionRequired } from './errors';
import { describe, expect, it } from 'vitest';

describe('githubConnectionErrorMessage', () => {
  it.each([
    [409, { error: 'github_binding_unavailable' }],
    [409, { error: 'github_capability_unavailable' }],
    [401, { error: 'github_copilot_auth_required' }],
    [409, { error: 'github_copilot_connection_required' }],
  ])('returns an actionable message for GitHub connection error %s', (status, body) => {
    expect(githubConnectionErrorMessage(new ApiError(status, JSON.stringify(body)))).toMatch(/Connect GitHub|reconnect GitHub|Connect your GitHub/i);
  });
});

describe('isGitHubRepoAppConnectionRequired', () => {
  it.each([
    'github_binding_unavailable',
    'github_capability_unavailable',
  ])('returns true for %s so callers offer a "Connect GitHub" action instead of a generic retry', (code) => {
    expect(isGitHubRepoAppConnectionRequired(new ApiError(409, JSON.stringify({ error: code })))).toBe(true);
  });

  it('returns false for unrelated error codes', () => {
    expect(isGitHubRepoAppConnectionRequired(new ApiError(409, JSON.stringify({ error: 'no_team' })))).toBe(false);
  });
});
