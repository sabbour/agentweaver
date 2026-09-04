import { ApiError } from './client';
import {
  formatModelProviderErrorMessage,
  githubConnectionErrorMessage,
  isGitHubRepoAppConnectionRequired,
} from './errors';
import { describe, expect, it } from 'vitest';

describe('githubConnectionErrorMessage', () => {
  it.each([
    [409, { error: 'github_binding_unavailable' }],
    [409, { error: 'github_capability_unavailable' }],
  ])('returns an actionable repository-access message for error %s', (status, body) => {
    expect(githubConnectionErrorMessage(new ApiError(status, JSON.stringify(body)))).toMatch(/repository access/i);
  });

  it.each([
    [401, { error: 'github_copilot_auth_required' }],
    [409, { error: 'model_provider_connection_required' }],
  ])('returns an actionable, non-"Connect GitHub" message for a model-provider error %s', (status, body) => {
    const message = githubConnectionErrorMessage(new ApiError(status, JSON.stringify(body)));
    expect(message).toMatch(/model provider|Copilot/i);
  });

  it.each([
    [409, { error: 'github_binding_unavailable' }],
    [409, { error: 'github_capability_unavailable' }],
  ])('does not mention retry when only a repository setup action is offered (%s)', (status, body) => {
    expect(githubConnectionErrorMessage(new ApiError(status, JSON.stringify(body)))).not.toMatch(/retry/i);
  });

  it('returns a distinct, non-connect message for a transient capability failure', () => {
    const message = githubConnectionErrorMessage(new ApiError(503, JSON.stringify({ error: 'github_capability_transient' })));
    expect(message).toMatch(/temporarily unavailable/i);
    expect(message).not.toMatch(/connect github/i);
  });

  it('returns null for an unrelated 404, instead of misrepresenting it as a GitHub connection issue', () => {
    expect(githubConnectionErrorMessage(new ApiError(404, JSON.stringify({ error: 'run_not_found' })))).toBeNull();
  });

  it('returns a reconnect-required message for a stale project Copilot binding', () => {
    expect(githubConnectionErrorMessage(
      new ApiError(409, JSON.stringify({ error: 'project_model_provider_reconnect_required' })),
    )).toBe('Reconnect the project GitHub Copilot authorization used for unattended AI work.');
  });
});

describe('formatModelProviderErrorMessage', () => {
  it('tells the operator to reconnect when the stored project Copilot binding is unusable', () => {
    expect(formatModelProviderErrorMessage(
      new ApiError(409, JSON.stringify({ error: 'project_model_provider_reconnect_required' })),
    )).toBe('Reconnect the project GitHub Copilot authorization used for unattended AI work.');
  });
});

describe('isGitHubRepoAppConnectionRequired', () => {
  it.each([
    'github_binding_unavailable',
    'github_capability_unavailable',
  ])('returns true for %s so callers offer repository setup instead of a generic retry', (code) => {
    expect(isGitHubRepoAppConnectionRequired(new ApiError(409, JSON.stringify({ error: code })))).toBe(true);
  });

  it('returns false for unrelated error codes', () => {
    expect(isGitHubRepoAppConnectionRequired(new ApiError(409, JSON.stringify({ error: 'no_team' })))).toBe(false);
  });

  it('returns false for a transient capability failure while still connected', () => {
    expect(isGitHubRepoAppConnectionRequired(new ApiError(503, JSON.stringify({ error: 'github_capability_transient' })))).toBe(false);
  });
});
