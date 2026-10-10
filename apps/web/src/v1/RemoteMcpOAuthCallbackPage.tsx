import { useEffect, useMemo, useRef } from 'react';
import {
  parseRemoteMcpOAuthCallbackParameters,
} from './remoteMcpOAuthCallback';

export function RemoteMcpOAuthPopupCallbackPage() {
  const callback = useMemo(() => window.location.hash
    ? null
    : parseRemoteMcpOAuthCallbackParameters(window.location.search), []);
  const opener = useMemo(() => {
    const value = window.opener;
    return value && value !== window ? value : null;
  }, []);
  const message = !callback
    ? 'The response was invalid. Close this window and try again.'
    : opener
      ? 'Returning to Agentweaver…'
      : 'This response has no matching Agentweaver window. Close this window and try again.';
  const attempted = useRef(false);

  useEffect(() => {
    if (attempted.current) return;
    attempted.current = true;
    window.history.replaceState(null, '', window.location.pathname);

    if (!callback || !opener) return;

    opener.postMessage(callback, window.location.origin);
    window.close();
  }, [callback, opener]);

  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>Returning from the MCP provider</h1>
        <p role="status">{message}</p>
      </section>
    </main>
  );
}
