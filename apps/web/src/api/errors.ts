import { ApiError } from './client';
import type { NoTeamStartOrchestrationError } from './types';

export type ApiErrorKind =
  | 'not-found'
  | 'unauthorized'
  | 'forbidden'
  | 'conflict'
  | 'rate-limited'
  | 'server'
  | 'network'
  | 'unknown';

export interface FormattedApiError {
  kind: ApiErrorKind;
  status?: number;
  message: string;
  detail?: string;
}

// Shown whenever isGitHubRepoAppConnectionRequired(err) is true, regardless of which of the
// connection-required codes triggered it, so the text always matches the single "Connect GitHub"
// action offered in that state — never mentions "retry", since no retry option is shown.
const GITHUB_CONNECTION_REQUIRED_MESSAGE = 'Set up repository access to see your GitHub repositories.';

// Repository-access-domain error codes: raised by the GitHub Repo App installation/authorization
// endpoints. These never imply anything about the model provider (Copilot/BYOK) used for AI
// generation — a repository-access failure and a model-provider failure are separate credential
// authorities and must not be conflated in the message shown to the user.
const REPOSITORY_ACCESS_ERROR_MESSAGES: Record<string, string> = {
  // A live Repo App connection exists, but the GitHub API call itself failed transiently
  // (network error, timeout, GitHub outage). Reconnecting would not help, so this pairs with a
  // "Retry" action rather than "Connect GitHub".
  github_capability_transient: 'GitHub is temporarily unavailable. Try again in a moment.',
};

// Model-provider-domain error codes: raised when the caller's AI inference source (GitHub
// Copilot, unless a deployment-wide or project override is active) is not connected or usable.
const MODEL_PROVIDER_ERROR_MESSAGES: Record<string, string> = {
  github_copilot_auth_required: 'Authorize GitHub Copilot to use it as the model provider.',
  model_provider_connection_required: 'Connect a model provider to continue.',
};

function repositoryAccessErrorMessage(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  if (isGitHubRepoAppConnectionRequired(err)) return GITHUB_CONNECTION_REQUIRED_MESSAGE;
  const code = parseApiBody(err.body).error;
  return code ? REPOSITORY_ACCESS_ERROR_MESSAGES[code] ?? null : null;
}

function modelProviderErrorMessage(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  const code = parseApiBody(err.body).error;
  return code ? MODEL_PROVIDER_ERROR_MESSAGES[code] ?? null : null;
}

// Combined dispatcher used by the generic formatApiErrorMessage() below, which has no context on
// which credential domain (repository access vs. model provider) a given call site belongs to.
// Call sites that know their domain should prefer the specific helper above instead.
export function githubConnectionErrorMessage(err: unknown): string | null {
  return repositoryAccessErrorMessage(err) ?? modelProviderErrorMessage(err);
}

const GITHUB_CONNECTION_REQUIRED_CODES = new Set([
  'github_binding_unavailable',
  'github_capability_unavailable',
]);

/**
 * True when the API rejected a request because the caller has not yet connected the GitHub Repo
 * App (or the connection is stale/unavailable) — the case where the caller should be offered a
 * "Connect GitHub" action rather than a generic retry.
 */
export function isGitHubRepoAppConnectionRequired(err: unknown): boolean {
  if (!(err instanceof ApiError)) return false;
  const body = parseApiBody(err.body);
  return !!body.error && GITHUB_CONNECTION_REQUIRED_CODES.has(body.error);
}

export function parseApiBody(body: string): { error?: string; message?: string; detail?: string } {
  if (!body) return {};
  try {
    const parsed = JSON.parse(body) as Record<string, unknown>;
    return {
      error: typeof parsed.error === 'string' ? parsed.error : undefined,
      message: typeof parsed.message === 'string' ? parsed.message : undefined,
      detail: typeof parsed.detail === 'string' ? parsed.detail : undefined,
    };
  } catch {
    return { message: body };
  }
}

export function parseNoTeamStartError(err: unknown): NoTeamStartOrchestrationError | null {
  if (!(err instanceof ApiError) || err.status !== 409) return null;
  const body = parseApiBody(err.body);
  if (body.error !== 'no_team') return null;
  return {
    error: 'no_team',
    message: body.message ?? 'This project has no team. Cast a team before starting an orchestration.',
  };
}

export function formatApiError(err: unknown, fallback = 'The request failed.'): FormattedApiError {
  if (err instanceof ApiError) {
    const body = parseApiBody(err.body);
    const serverText = body.message ?? body.detail ?? body.error;
    const detail = serverText && serverText !== body.error ? serverText : undefined;
    switch (err.status) {
      case 401:
        return { kind: 'unauthorized', status: err.status, message: 'Sign in again to continue.', detail };
      case 403:
        return { kind: 'forbidden', status: err.status, message: 'You do not have permission to perform this action.', detail };
      case 404:
        return { kind: 'not-found', status: err.status, message: 'The requested run or resource was not found.', detail };
      case 409:
        return { kind: 'conflict', status: err.status, message: serverText ?? 'This action is no longer valid for the current run state.', detail };
      case 429:
        return { kind: 'rate-limited', status: err.status, message: 'Too many requests. Wait a moment and try again.', detail };
      default:
        if (err.status >= 500) {
          return { kind: 'server', status: err.status, message: 'The API returned a server error. Try again after it recovers.', detail: serverText };
        }
        return { kind: 'unknown', status: err.status, message: serverText ?? fallback, detail };
    }
  }

  if (err instanceof TypeError) {
    return { kind: 'network', message: 'Network error. Check the API connection and try again.', detail: err.message };
  }

  if (err instanceof Error) {
    return { kind: 'unknown', message: err.message || fallback };
  }

  return { kind: 'unknown', message: String(err || fallback) };
}

export function formatApiErrorMessage(err: unknown, fallback?: string): string {
  const githubMessage = githubConnectionErrorMessage(err);
  if (githubMessage) return githubMessage;
  const formatted = formatApiError(err, fallback);
  return formatted.detail && formatted.detail !== formatted.message
    ? `${formatted.message} ${formatted.detail}`
    : formatted.message;
}
