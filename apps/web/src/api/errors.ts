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

const GITHUB_CONNECTION_MESSAGES: Record<string, string> = {
  github_binding_unavailable: 'GitHub connections are temporarily unavailable. Connect GitHub and try again.',
  github_copilot_auth_required: 'Connect your GitHub Copilot account to use AI features.',
  github_copilot_connection_required: 'Connect your GitHub Copilot account to continue.',
};

export function githubConnectionErrorMessage(err: unknown): string | null {
  if (!(err instanceof ApiError)) return null;
  const body = parseApiBody(err.body);
  const code = body.error;
  if (code && GITHUB_CONNECTION_MESSAGES[code]) return GITHUB_CONNECTION_MESSAGES[code];
  if (err.status === 404) {
    return 'Connect GitHub to access this project repository and AI features.';
  }
  return null;
}

/**
 * True when the API rejected a request because the caller has not yet connected the GitHub Repo
 * App (or the connection is stale) — the case where the caller should be offered a "Connect
 * GitHub" action rather than a generic retry.
 */
export function isGitHubRepoAppConnectionRequired(err: unknown): boolean {
  if (!(err instanceof ApiError)) return false;
  const body = parseApiBody(err.body);
  return body.error === 'github_binding_unavailable';
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
  return formatted.detail ? `${formatted.message} ${formatted.detail}` : formatted.message;
}
