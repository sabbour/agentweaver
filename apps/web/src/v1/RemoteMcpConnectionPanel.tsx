import { Badge, Button, Field, Input, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useEffect, useRef, useState } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { gatewayClient } from './api';
import { isRemoteMcpOAuthConsentPreparation, isRemoteMcpOAuthManagementStatus } from './contracts';
import type { RemoteMcpOAuthManagementStatus } from './contracts';

/** Manages exact owner-held Remote MCP connections without collecting credentials. */
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
  const popup = useRef<Window | null>(null);
  const generation = useRef(0);
  useEffect(() => () => {
    generation.current += 1;
    popup.current?.close();
    popup.current = null;
  }, []);

  const current = status?.connectionId === connectionId && status.projectId === projectId ? status : null;
  const checksumValid = /^[a-f0-9]{64}$/.test(checksum);
  const connectionValid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(connectionId);
  const configured = Boolean(current?.currentConfigurationMatches && current.currentConfigurationRevision === configurationRevision);

  /** Rejects cross-project or malformed responses before showing connection state. */
  const accept = (result: RemoteMcpOAuthManagementStatus) => {
    if (!isRemoteMcpOAuthManagementStatus(result) || result.projectId !== projectId || result.connectionId !== connectionId)
      throw new Error('The service returned a different or invalid connection.');
    setStatus(result);
  };

  const run = async (operation: () => Promise<RemoteMcpOAuthManagementStatus>) => {
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      const result = await operation();
      if (request === generation.current) accept(result);
    } catch (reason) {
      if (request === generation.current) setError(reason instanceof Error ? reason.message : 'The connection request failed. Try again.');
    } finally {
      if (request === generation.current) setBusy(false);
    }
  };

  const authorize = async () => {
    if (!current || !configured || !checksumValid || configurationRevision === null) return;
    const opened = window.open('about:blank', `agentweaver-remote-mcp-${crypto.randomUUID()}`, 'popup,width=600,height=760');
    if (!opened) {
      setError('The sign-in window was blocked. Allow popups and try again.');
      return;
    }
    popup.current?.close();
    popup.current = opened;
    const request = ++generation.current;
    setBusy(true);
    setError(null);
    try {
      const preparation = await apiCall((token, tenant) => gatewayClient.prepareRemoteMcpOAuthConsent(token, connectionId, {
        expectedConnectionRevision: current.connectionRevision,
        expectedCredentialRevision: current.credentialRevision,
        expectedConfigurationRevision: configurationRevision,
        expectedConfigurationSha256: checksum,
      }, tenant), null);
      if (request !== generation.current) return;
      if (!isRemoteMcpOAuthConsentPreparation(preparation)) throw new Error('The service returned an invalid sign-in request.');
      const destination = new URL(preparation.authorizationUri);
      if (destination.protocol !== 'https:' || destination.username || destination.password || opened.closed)
        throw new Error('The sign-in address is invalid or its window was closed. Try again.');
      opened.location.replace(destination.href);
      setSignInStarted(true);
    } catch (reason) {
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
          {current
            ? <Button disabled={busy || !configured || !checksumValid} onClick={() => void authorize()}>Authorize tool service</Button>
            : <>
              <Field label="Sign-in service address"><Input value={issuer} type="url" disabled={busy} onChange={(_, data) => setIssuer(data.value)} /></Field>
              <Field label="Requested tool permissions" hint="Separate permission names with spaces."><Input value={scopes} disabled={busy} onChange={(_, data) => setScopes(data.value)} /></Field>
              <Button disabled={busy || !connectionValid || !checksumValid || !issuer || configurationRevision === null}
                onClick={() => {
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
