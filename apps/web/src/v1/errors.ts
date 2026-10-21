/** Error formatting shared by v1 pages. Kept outside components for fast refresh. */
import { GatewayError } from './api';
import { RepoAppBrokerError } from './repoApp';

/** Formats Gateway and unexpected failures for display in page-level notices. */
export function errorMessage(error: unknown): string {
  if (error instanceof GatewayError) {
    // Show the status and code too; they help when reporting a problem.
    const code = error.code ? ` (${error.code})` : '';
    return `HTTP ${error.status}${code}: ${error.message}`;
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
