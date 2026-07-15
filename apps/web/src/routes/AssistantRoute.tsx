import { AssistantRunPage } from '../pages/AssistantRunPage';
import { Navigate, useSearchParams } from 'react-router-dom';

const ASSISTANT_FLAG_KEY = 'agentweaver.assistant.enabled';

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
 */
function resolveEnabled(queryFlag: string | null): boolean {
  try {
    if (queryFlag === '1') {
      window.localStorage.setItem(ASSISTANT_FLAG_KEY, '1');
      return true;
    }
    if (queryFlag === '0') {
      window.localStorage.removeItem(ASSISTANT_FLAG_KEY);
      return false;
    }
    return window.localStorage.getItem(ASSISTANT_FLAG_KEY) === '1';
  } catch {
    // localStorage may be unavailable (privacy mode); fall back to the query param only.
    return queryFlag === '1';
  }
}

export function AssistantRoute() {
  const [searchParams] = useSearchParams();
  const enabled = resolveEnabled(searchParams.get('assistant'));

  if (!enabled) return <Navigate to="/" replace />;
  return <AssistantRunPage />;
}
