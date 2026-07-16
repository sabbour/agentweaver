import { AssistantRunPage } from '../pages/AssistantRunPage';
import { Navigate, useSearchParams } from 'react-router-dom';
import { resolveAssistantFlag } from '../utils/assistantFlag';

/**
 * Feature-flag gate for the #346 MCP-driven operator assistant page.
 *
 * The page is rolled out gradually (per Morpheus's design) rather than replacing the
 * existing LeftNav "operator dock" trigger outright. It is reachable at `/assistant`
 * only when the flag is on:
 *   - `?assistant=1` enables it (and persists to localStorage so later navigations keep it).
 *   - `?assistant=0` clears the flag.
 *   - otherwise the persisted localStorage flag decides.
 * When disabled, redirect to the overview so the route is inert until the page is proven
 * end-to-end and the dock is retired in a later pass.
 *
 * The Sessions page (SessionsPage.tsx) and its LeftNav entry gate on the same flag via
 * `resolveAssistantFlag`/`isAssistantFlagEnabled` in utils/assistantFlag.ts.
 */
export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const enabled = resolveAssistantFlag(searchParams.get('assistant'));

  if (!enabled) return <Navigate to="/" replace />;
  return <AssistantRunPage />;
}
