/** Error formatting shared by v1 pages. Kept outside components for fast refresh. */
import { GatewayError } from './api';
import { RepoAppBrokerError } from './repoApp';

/** Formats errors for display in page-level notices without exposing internal codes. */
export function errorMessage(error: unknown): string {
  if (error instanceof GatewayError) {
    const detail = error.message.trim();
    const normalizedDetail = detail.toLowerCase();
    const hasUserDetail = detail.length > 0
      && normalizedDetail !== error.code?.toLowerCase()
      && !['unauthorized', 'forbidden', 'not found'].includes(normalizedDetail)
      && !/^The request failed with HTTP \d+\.$/.test(detail);
    if (error.status === 401) return hasUserDetail ? detail : 'Sign in to continue.';
    if (error.status === 403)
      return hasUserDetail ? detail : 'You do not have permission to complete this request.';
    if (error.status === 404) return 'The requested item could not be found.';
    if (hasUserDetail)
      return `HTTP ${error.status}: ${detail}`;
    return 'The request could not be completed. Try again.';
  }
  if (error instanceof RepoAppBrokerError) {
    if (error.code === 'github_repo_app_not_configured')
      return 'GitHub access is not set up. Ask your administrator to configure it.';
    return 'The GitHub connection could not be checked. Try again.';
  }
  return error instanceof Error ? error.message : 'The request failed unexpectedly.';
}

/** Extracts a stable API error code when an action needs code-specific handling. */
export function errorCode(error: unknown): string | undefined {
  // Only our two typed API errors have a code. Other errors return undefined so
  // callers fall back to the generic message instead of matching on free text.
  return error instanceof GatewayError || error instanceof RepoAppBrokerError
    ? error.code
    : undefined;
}
