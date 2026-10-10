import { useEffect, useRef, useState } from 'react';
import {
  parseRemoteMcpOAuthCallbackParameters,
} from './remoteMcpOAuthCallback';

export function RemoteMcpOAuthPopupCallbackPage() {
  const [message, setMessage] = useState('Checking the authorization response…');
  const attempted = useRef(false);

  useEffect(() => {
    if (attempted.current) return;
    attempted.current = true;

    const callback = window.location.hash
      ? null
      : parseRemoteMcpOAuthCallbackParameters(window.location.search);
    window.history.replaceState(null, '', window.location.pathname);

    if (!callback) {
      setMessage('The response was invalid. Close this window and try again.');
      return;
    }
    const opener = window.opener;
    if (!opener || opener === window) {
      setMessage('This response has no matching Agentweaver window. Close this window and try again.');
      return;
    }

    opener.postMessage(callback, window.location.origin);
    window.close();
    setMessage('Returning to Agentweaver…');
  }, []);

  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>Returning from the MCP provider</h1>
        <p role="status">{message}</p>
      </section>
    </main>
  );
}
