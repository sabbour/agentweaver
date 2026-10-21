/** Error formatting shared by v1 pages. Kept outside components for fast refresh. */
import { GatewayError } from './api';
import { RepoAppBrokerError } from './repoApp';

/** Formats Gateway and unexpected failures for display in page-level notices. */
export function errorMessage(error: unknown): string {
  if (error instanceof GatewayError) {
    const code = error.code ? ` (${error.code})` : '';
    return `HTTP ${error.status}${code}: ${error.message}`;
  }
  return error instanceof Error ? error.message : 'The request failed unexpectedly.';
}

/** Extracts a stable API error code when an action needs code-specific handling. */
export function errorCode(error: unknown): string | undefined {
  return error instanceof GatewayError || error instanceof RepoAppBrokerError
    ? error.code
    : undefined;
}
