import { Badge, Button, Field, Input, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useCallback, useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { isRemoteMcpOAuthConsentPreparation, isRemoteMcpOAuthManagementStatus } from './contracts';
import type { RemoteMcpConnectionDirectoryEntry, RemoteMcpOAuthManagementStatus } from './contracts';
import { errorMessage } from './errors';

/** How many saved connections to read per directory page. */
const DIRECTORY_PAGE_SIZE = 25;

/**
 * Manages one saved Remote MCP connection for this project. The project's
 * saved connections are listed so the user picks one instead of typing its ID.
 * The picked connection's status can be loaded, and it can be registered,
 * signed in, refreshed, or disconnected. The browser never sees or
 * collects passwords or tokens; the sign-in service keeps them.
 */
export function RemoteMcpConnectionPanel({ projectId, configurationRevision }: {
  projectId: string; configurationRevision: number | null;
}) {
  const { apiCall } = useAuth();
  const [connectionId, setConnectionId] = useState('');
  const [checksum, setChecksum] = useState('');
  const [issuer, setIssuer] = useState('');
  const [scopes, setScopes] = useState('');
  const [status, setStatus] = useState<RemoteMcpOAuthManagementStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [signInStarted, setSignInStarted] = useState(false);
  // Saved connections from the project directory, read one page at a time.
  const [directory, setDirectory] = useState<RemoteMcpConnectionDirectoryEntry[] | null>(null);
  const [nextConnectionId, setNextConnectionId] = useState<string | null>(null);
  const [directoryError, setDirectoryError] = useState<string | null>(null);
  const [directoryLoading, setDirectoryLoading] = useState(false);
  // The sign-in popup we opened, so it can be closed when it is no longer valid.
  const popup = useRef<Window | null>(null);
  // Counter for the latest request. Responses from older requests are ignored.
  const generation = useRef(0);
  // On unmount, ignore any request still in flight and close an open sign-in popup.
  useEffect(() => () => {
    generation.current += 1;
    popup.current?.close();
    popup.current = null;
  }, []);

  // Use the loaded status only while it still matches the typed connection ID and this project.
  // Editing the ID hides the old status right away.
  const current = status?.connectionId === connectionId && status.projectId === projectId ? status : null;
  // Client-side format checks: the checksum is a lowercase SHA-256 hex string and the ID is a UUID.
  // They only enable buttons; the service does the real validation.
  const checksumValid = /^[a-f0-9]{64}$/.test(checksum);
  const connectionValid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(connectionId);
  // The connection may be authorized or refreshed only if the service says its
  // stored setup matches the project's current configuration revision.
  const configured = Boolean(current?.currentConfigurationMatches && current.currentConfigurationRevision === configurationRevision);

  /** Rejects cross-project or malformed responses before showing connection state. */
  const accept = (result: RemoteMcpOAuthManagementStatus, expectedId: string) => {
    if (!isRemoteMcpOAuthManagementStatus(result) || result.projectId !== projectId || result.connectionId !== expectedId)
      throw new Error('The service returned a different or invalid connection.');
    setStatus(result);
  };

  /**
   * Runs one connection request and shows its result if it is still the latest
   * request. `expectedId` is the connection the response must be for.
   */
  const run = async (operation: () => Promise<RemoteMcpOAuthManagementStatus>, expectedId = connectionId) => {
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      const result = await operation();
      // accept() throws for a wrong or malformed response, which lands in the catch below.
      if (request === generation.current) accept(result, expectedId);
    } catch (reason) {
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'The connection request failed. Try again.');
    } finally {
      if (request === generation.current) setBusy(false);
    }
  };

  /**
   * Reads one page of the project's saved connections. Without `after` it
   * starts over; with it, the page is added to the list already shown.
   */
  const loadDirectory = useCallback(async (after?: string) => {
    setDirectoryLoading(true);
    setDirectoryError(null);
    try {
      const page = await apiCall((token, tenant) => gatewayClient.listRemoteMcpConnections(
        token, projectId, after ? { pageSize: DIRECTORY_PAGE_SIZE, after } : { pageSize: DIRECTORY_PAGE_SIZE }, tenant,
      ), null);
      // Removed connections cannot be used, so they are not offered.
      const usable = page.connections.filter((entry) => entry.state !== 'removed');
      setDirectory((previous) => (after && previous ? [...previous, ...usable] : usable));
      setNextConnectionId(page.nextConnectionId);
    } catch (reason) {
      setDirectoryError(errorMessage(reason));
    } finally {
      setDirectoryLoading(false);
    }
  }, [apiCall, projectId]);

  useEffect(() => {
    // Load on the next tick so state updates stay out of the effect body.
    const timer = window.setTimeout(() => void loadDirectory(), 0);
    return () => window.clearTimeout(timer);
  }, [loadDirectory]);

  /** Starts over with a different connection ID, dropping anything that belonged to the old one. */
  const switchConnection = (nextId: string) => {
    // Ignore in-flight requests and close any sign-in popup for the old connection.
    generation.current += 1;
    popup.current?.close();
    popup.current = null;
    setConnectionId(nextId);
    setStatus(null);
    setError(null);
    setSignInStarted(false);
  };

  /** Picks a listed connection: fills its ID and saved checksum, then loads its status. */
  const choose = (entry: RemoteMcpConnectionDirectoryEntry) => {
    switchConnection(entry.connectionId);
    // The directory reports the checksum of the saved setup, so it does not need to be typed.
    setChecksum(entry.configurationSha256);
    void run(() => apiCall(
      (token, tenant) => gatewayClient.getRemoteMcpOAuthConnection(token, entry.connectionId, tenant), null,
    ), entry.connectionId);
  };

  /** Asks the service for a sign-in address and sends the popup there. */
  const authorize = async () => {
    if (!current || !configured || !checksumValid || configurationRevision === null) return;
    // Open the popup first, while we are still inside the click handler. Browsers
    // block popups opened later, after an await. It starts blank and gets its real
    // address once the service answers. The random name keeps each attempt in its own window.
    const opened = window.open('about:blank', `agentweaver-remote-mcp-${crypto.randomUUID()}`, 'popup,width=600,height=760');
    if (!opened) {
      setError('The sign-in window was blocked. Allow popups and try again.');
      return;
    }
    // Only one sign-in popup at a time: close any earlier one.
    popup.current?.close();
    popup.current = opened;
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      // Send every revision we saw plus the checksum. The service refuses to start
      // sign-in if the connection, credential, or configuration changed since then.
      const preparation = await apiCall((token, tenant) => gatewayClient.prepareRemoteMcpOAuthConsent(token, connectionId, {
        expectedConnectionRevision: current.connectionRevision,
        expectedCredentialRevision: current.credentialRevision,
        expectedConfigurationRevision: configurationRevision,
        expectedConfigurationSha256: checksum,
      }, tenant), null);
      if (request !== generation.current) return;
      if (!isRemoteMcpOAuthConsentPreparation(preparation)) throw new Error('The service returned an invalid sign-in request.');
      // Navigate the popup only to a plain HTTPS address with no embedded user name
      // or password, and only if the user has not closed the popup meanwhile.
      const destination = new URL(preparation.authorizationUri);
      if (destination.protocol !== 'https:' || destination.username || destination.password || opened.closed)
        throw new Error('The sign-in address is invalid or its window was closed. Try again.');
      // replace() keeps the blank page out of the popup's history.
      opened.location.replace(destination.href);
      setSignInStarted(true);
    } catch (reason) {
      // Never leave a blank or half-started popup open after a failure.
      opened.close();
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'Sign-in could not start.');
    } finally {
      if (request === generation.current) setBusy(false);
    }
  };
  return (
    <PageSection title="Remote MCP" description="Connect an external tool service. Choose a saved connection to read its current status. Passwords and tokens stay with the sign-in service.">
      {directoryError && <MessageBar intent="error"><MessageBarBody>{directoryError}</MessageBarBody></MessageBar>}
      {directory === null
        ? directoryLoading && <p className="v1-muted">Loading saved connections�</p>
        : directory.length === 0
          ? <p className="v1-muted">This project has no saved Remote MCP connections.</p>
          : (
            <div className="v1-table-wrap">
              <table className="v1-table">
                <caption>Saved connections</caption>
                <thead>
                  <tr>
                    <th scope="col">Name</th>
                    <th scope="col">Address</th>
                    <th scope="col">State</th>
                    <th scope="col">Tools</th>
                    <th scope="col"><span className="v1-visually-hidden">Action</span></th>
                  </tr>
                </thead>
                <tbody>
                  {directory.map((entry) => (
                    <tr key={entry.connectionId}>
                      <th scope="row">{entry.displayName}</th>
                      <td><code>{entry.endpointUri}</code></td>
                      <td><Badge appearance="outline">{entry.state}</Badge></td>
                      {/* The tool count is known only after the service has read the tool list. */}
                      <td>{entry.discoveryRevision === null ? <span className="v1-muted">Not read yet</span> : entry.toolCount}</td>
                      <td>
                        <Button size="small" disabled={busy} aria-label={`Select ${entry.displayName}`} onClick={() => choose(entry)}>
                          {entry.connectionId === connectionId ? 'Selected' : 'Select'}
                        </Button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      <div className="v1-actions">
        {nextConnectionId && (
          <Button disabled={directoryLoading} onClick={() => void loadDirectory(nextConnectionId)}>Load more connections</Button>
        )}
        <Button disabled={directoryLoading} onClick={() => void loadDirectory()}>{directoryLoading ? 'Loading�' : 'Refresh list'}</Button>
      </div>
      <Field label="Remote MCP connection ID" hint="Filled in when you select a saved connection. Type an ID only to register a new connection.">
        <Input value={connectionId} disabled={busy} onChange={(_, data) => switchConnection(data.value)} />
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
          {/* Refresh and disconnect send the revisions shown, so they fail if the connection changed since it was loaded. Disconnect also sends a fresh key so a retried request is applied only once. */}
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
          <p className="v1-muted">Selecting a saved connection fills in its checksum. For a new connection, use the checksum from the saved project connection setup.</p>
          <Field label="Configuration checksum"><Input value={checksum} disabled={busy} onChange={(_, data) => setChecksum(data.value)} /></Field>
          {/* With a loaded connection, offer sign-in. Without one, offer to register the typed ID as a new connection. */}
          {current
            ? <Button disabled={busy || !configured || !checksumValid} onClick={() => void authorize()}>Authorize tool service</Button>
            : <>
              <Field label="Sign-in service address"><Input value={issuer} type="url" disabled={busy} onChange={(_, data) => setIssuer(data.value)} /></Field>
              <Field label="Requested tool permissions" hint="Separate permission names with spaces."><Input value={scopes} disabled={busy} onChange={(_, data) => setScopes(data.value)} /></Field>
              <Button disabled={busy || !connectionValid || !checksumValid || !issuer || configurationRevision === null}
                onClick={() => {
                  // Check for null and copy the revision, so the request below uses the value seen at click time.
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
