import { Badge, Button, Field, Input, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { isRemoteMcpOAuthConsentPreparation, isRemoteMcpOAuthManagementStatus } from './contracts';
import type { RemoteMcpOAuthManagementStatus } from './contracts';

/**
 * Manages one owner-held Remote MCP connection, identified by its exact ID.
 * The panel loads, registers, authorizes, refreshes, and disconnects the
 * connection. It never asks for passwords or tokens; sign-in happens on the
 * external service in a popup window.
 */
export function RemoteMcpConnectionPanel({ projectId, configurationRevision }: {
  projectId: string; configurationRevision: number | null;
}) {
  const { apiCall } = useAuth();
  const [connectionId, setConnectionId] = useState('');
  // SHA-256 of the saved connection setup. The server compares it to the
  // stored setup so consent is only granted for the setup the user expects.
  const [checksum, setChecksum] = useState('');
  const [issuer, setIssuer] = useState('');
  const [scopes, setScopes] = useState('');
  const [status, setStatus] = useState<RemoteMcpOAuthManagementStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [signInStarted, setSignInStarted] = useState(false);
  // The open sign-in popup, so it can be closed when the user moves on.
  const popup = useRef<Window | null>(null);
  // Request counter. Every new request or ID change increases it. A response
  // is applied only if the counter still matches the value from when the
  // request started; otherwise the response is stale and is ignored.
  const generation = useRef(0);
  // On unmount, invalidate any in-flight response and close the popup.
  useEffect(() => () => {
    generation.current += 1;
    popup.current?.close();
    popup.current = null;
  }, []);

  // Only show status that belongs to the ID in the input and to this project.
  // This hides an old result while the user types a different ID.
  const current = status?.connectionId === connectionId && status.projectId === projectId ? status : null;
  // The checksum must be exactly 64 lowercase hex characters (a SHA-256 digest).
  const checksumValid = /^[a-f0-9]{64}$/.test(checksum);
  // Connection IDs are UUIDs. Checking the format first avoids a pointless request.
  const connectionValid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(connectionId);
  // The connection is usable for authorize and refresh only when the server says
  // its setup matches the current configuration, and that configuration is the
  // same revision this page has loaded. Otherwise the user could consent to an
  // outdated setup.
  const configured = Boolean(current?.currentConfigurationMatches && current.currentConfigurationRevision === configurationRevision);

  /** Rejects cross-project or malformed responses before showing connection state. */
  const accept = (result: RemoteMcpOAuthManagementStatus) => {
    // Treat a response for another project or connection as an error, so the
    // panel never shows state the user did not ask for.
    if (!isRemoteMcpOAuthManagementStatus(result) || result.projectId !== projectId || result.connectionId !== connectionId)
      throw new Error('The service returned a different or invalid connection.');
    setStatus(result);
  };

  /**
   * Runs one status-returning request (load, register, refresh, or disconnect)
   * and applies the result only if no newer request has started since.
   */
  const run = async (operation: () => Promise<RemoteMcpOAuthManagementStatus>) => {
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      const result = await operation();
      // accept() can throw for a mismatched response. The catch below shows that error.
      if (request === generation.current) accept(result);
    } catch (reason) {
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'The connection request failed. Try again.');
    } finally {
      // A newer request owns the busy flag, so only the latest one clears it.
      if (request === generation.current) setBusy(false);
    }
  };

  /**
   * Starts OAuth sign-in. Opens a blank popup first, asks the server to prepare
   * consent, then sends the popup to the returned sign-in address.
   */
  const authorize = async () => {
    // The button is disabled in these cases too. This guard also narrows types.
    if (!current || !configured || !checksumValid || configurationRevision === null) return;
    // Open the popup now, while still inside the click handler. Browsers block
    // popups opened after an await. A unique window name avoids reusing an old window.
    const opened = window.open('about:blank', `agentweaver-remote-mcp-${crypto.randomUUID()}`, 'popup,width=600,height=760');
    if (!opened) {
      setError('The sign-in window was blocked. Allow popups and try again.');
      return;
    }
    // Only one sign-in popup at a time. Close any earlier one.
    popup.current?.close();
    popup.current = opened;
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      // Send every revision the user saw, plus the checksum. The server refuses
      // to prepare consent if the connection, credential, or configuration
      // changed in the meantime.
      const preparation = await apiCall((token, tenant) => gatewayClient.prepareRemoteMcpOAuthConsent(token, connectionId, {
        expectedConnectionRevision: current.connectionRevision,
        expectedCredentialRevision: current.credentialRevision,
        expectedConfigurationRevision: configurationRevision,
        expectedConfigurationSha256: checksum,
      }, tenant), null);
      // The user changed the ID or the panel unmounted. Those paths already
      // closed the popup, so just stop.
      if (request !== generation.current) return;
      if (!isRemoteMcpOAuthConsentPreparation(preparation)) throw new Error('The service returned an invalid sign-in request.');
      // Only follow an HTTPS address without embedded user name or password,
      // and only if the user has not already closed the popup.
      const destination = new URL(preparation.authorizationUri);
      if (destination.protocol !== 'https:' || destination.username || destination.password || opened.closed)
        throw new Error('The sign-in address is invalid or its window was closed. Try again.');
      // replace() keeps about:blank out of the popup's history.
      opened.location.replace(destination.href);
      setSignInStarted(true);
    } catch (reason) {
      // Never leave a blank popup open after a failure.
      opened.close();
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'Sign-in could not start.');
    } finally {
      if (request === generation.current) setBusy(false);
    }
  };
  return (
    <PageSection title="Remote MCP" description="Connect an external tool service. Enter its saved connection ID to read current status. Passwords and tokens stay with the sign-in service.">
      <Field label="Remote MCP connection ID">
        <Input value={connectionId} disabled={busy} onChange={(_, data) => {
          // A new ID starts over: ignore pending responses for the old ID, close
          // its sign-in popup, and clear its status and messages.
          generation.current += 1;
          popup.current?.close();
          popup.current = null;
          setConnectionId(data.value);
          setStatus(null);
          setError(null);
          setSignInStarted(false);
        }} />
      </Field>
      <Button disabled={busy || !connectionValid} onClick={() => void run(() => apiCall(
        (token, tenant) => gatewayClient.getRemoteMcpOAuthConnection(token, connectionId, tenant), null,
      ))}>{busy ? 'Checking connection…' : 'Load connection'}</Button>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {current && <>
        <Badge appearance="tint">{current.state}</Badge>
        <p>{current.credentialUseAvailable ? 'The service reports that this connection can be used.' : 'The service reports that this connection cannot be used.'}</p>
        {!configured && <p className="v1-warning">The saved connection does not match the current project configuration. Update its setup before authorizing or refreshing.</p>}
        <div className="v1-actions">
          {/* Refresh and disconnect send the revisions shown on screen, so the server rejects them if the connection changed meanwhile. Refresh also needs a matching configuration. Disconnect is always allowed and carries an idempotency key so a retried click is safe. */}
          <Button disabled={busy || !configured} onClick={() => void run(() => apiCall((token, tenant) => gatewayClient.refreshRemoteMcpOAuthConnection(token, connectionId, {
            expectedConnectionRevision: current.connectionRevision, expectedCredentialRevision: current.credentialRevision,
            expectedConfigurationRevision: current.storedConfigurationRevision,
          }, tenant), null))}>Refresh connection</Button>
          <Button disabled={busy} onClick={() => void run(() => apiCall((token, tenant) => gatewayClient.disconnectRemoteMcpOAuthConnection(token, connectionId, {
            expectedConnectionRevision: current.connectionRevision, expectedCredentialRevision: current.credentialRevision,
            expectedConfigurationRevision: current.storedConfigurationRevision, idempotencyKey: crypto.randomUUID(),
          }, tenant), null))}>Disconnect tool service</Button>
        </div>
      </>}
      <details>
        <summary>Connection setup</summary>
        <div className="v1-form">
          <p className="v1-muted">Use the checksum from the saved project connection setup. This version cannot discover connections or their checksums automatically.</p>
          <Field label="Configuration checksum"><Input value={checksum} disabled={busy} onChange={(_, data) => setChecksum(data.value)} /></Field>
          {/* A loaded connection can be authorized. Until one is loaded, show the form that registers a new connection. */}
          {current
            ? <Button disabled={busy || !configured || !checksumValid} onClick={() => void authorize()}>Authorize tool service</Button>
            : <>
              <Field label="Sign-in service address"><Input value={issuer} type="url" disabled={busy} onChange={(_, data) => setIssuer(data.value)} /></Field>
              <Field label="Requested tool permissions" hint="Separate permission names with spaces."><Input value={scopes} disabled={busy} onChange={(_, data) => setScopes(data.value)} /></Field>
              <Button disabled={busy || !connectionValid || !checksumValid || !issuer || configurationRevision === null}
                onClick={() => {
                  // Copy the revision into a local so TypeScript keeps it non-null
                  // inside the async callback.
                  if (configurationRevision === null) return;
                  const revision = configurationRevision;
                  void run(() => apiCall((token, tenant) => gatewayClient.registerRemoteMcpOAuthConnection(token, {
                  projectId, connectionId, expectedConfigurationRevision: revision,
                  expectedConfigurationSha256: checksum, issuerUri: issuer,
                  scopes: scopes.split(/\s+/).filter(Boolean),
                }, tenant), null));
                }}>Register connection</Button>
            </>}
        </div>
      </details>
      {signInStarted && <p className="v1-muted">After sign-in returns, select Load connection to check the result. A returned window does not prove a connection succeeded.</p>}
    </PageSection>
  );
}
