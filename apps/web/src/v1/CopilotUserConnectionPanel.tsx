import { Button, MessageBar, MessageBarBody } from '@fluentui/react-components';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useAuth } from './AuthContext';
import { gatewayClient, GatewayError } from './api';
import { gatewayBaseUrl } from './config';
import {
  isCopilotConnectionBegin,
  isCopilotConnectionReceipt,
} from './contracts';
import type {
  CopilotConnectionReceipt,
} from './contracts';
import {
  COPILOT_CALLBACK_PATH,
  expectedCopilotCallbackState,
  isCopilotCallbackMessage,
} from './copilotCallback';
import type { CopilotCallbackMessage } from './copilotCallback';

const POPUP_TIMEOUT_MS = 10 * 60 * 1000;
const POPUP_POLL_INTERVAL_MS = 250;

interface PendingConnection {
  popup: Window;
  state: string;
  actorId: string;
  connectionId: string;
  projectId: string;
  accessToken: string | undefined;
  generation: number;
  completing: boolean;
  intervalId?: number;
  timeoutId?: number;
}

interface StartingConnection {
  popup: Window;
  projectId: string;
  accessToken: string | undefined;
  generation: number;
}

function messageForError(reason: unknown): string {
  if (reason instanceof GatewayError) {
    const code = reason.code ? ` (${reason.code})` : '';
    return `HTTP ${reason.status}${code}: ${reason.message}`;
  }
  return reason instanceof Error ? reason.message : 'The GitHub Copilot connection failed.';
}

function receiptForProject(
  value: unknown,
  connectionId: string,
  projectId: string,
): CopilotConnectionReceipt {
  if (!isCopilotConnectionReceipt(value) ||
      value.connectionId !== connectionId ||
      value.scope !== 'project' ||
      value.scopeId !== projectId) {
    throw new Error('The Gateway returned a connection outside this project or the expected connection.');
  }
  return value;
}

function copilotCallbackUri(): string {
  if (window.location.protocol !== 'https:') {
    throw new Error('Open project settings over HTTPS before connecting GitHub Copilot.');
  }
  const gatewayUrl = new URL(gatewayBaseUrl, window.location.origin);
  if (gatewayUrl.origin !== window.location.origin) {
    throw new Error('GitHub Copilot connection requires the Gateway on this same origin for its nonce cookie.');
  }
  return new URL(COPILOT_CALLBACK_PATH, window.location.origin).toString();
}

function stateLabel(state: CopilotConnectionReceipt['state']): string {
  switch (state) {
    case 'pending': return 'Authorization pending';
    case 'connected': return 'Connected';
    case 'refreshing': return 'Refreshing';
    case 'transientUnavailable': return 'Temporarily unavailable';
    case 'reconnectRequired': return 'Reconnect required';
    case 'revoked': return 'Revoked';
    case 'refreshIndeterminate': return 'Refresh state indeterminate';
  }
}

export function CopilotUserConnectionPanel({ projectId }: { projectId: string }) {
  const { session, apiCall } = useAuth();
  const accessToken = session?.accessToken;
  const pendingRef = useRef<PendingConnection | null>(null);
  const startingRef = useRef<StartingConnection | null>(null);
  const scopeRef = useRef({ projectId, accessToken });
  const scopeGenerationRef = useRef(0);
  const statusRequestRef = useRef(0);
  const [flowRevision, setFlowRevision] = useState(0);
  const [flowActive, setFlowActive] = useState(false);
  const [starting, setStarting] = useState(false);
  const [phase, setPhase] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<CopilotConnectionReceipt | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);

  const isCurrentScope = useCallback((
    expectedProjectId: string,
    expectedAccessToken: string | undefined,
    generation: number,
  ) => scopeGenerationRef.current === generation &&
    scopeRef.current.projectId === expectedProjectId &&
    scopeRef.current.accessToken === expectedAccessToken, []);

  const clearFlow = useCallback((active: PendingConnection) => {
    if (pendingRef.current !== active) return false;
    pendingRef.current = null;
    if (active.intervalId !== undefined) window.clearInterval(active.intervalId);
    if (active.timeoutId !== undefined) window.clearTimeout(active.timeoutId);
    if (!active.popup.closed) active.popup.close();
    setFlowActive(false);
    setFlowRevision((current) => current + 1);
    setPhase(null);
    return true;
  }, []);

  const failFlow = useCallback((active: PendingConnection, message: string) => {
    if (!clearFlow(active)) return;
    setError(message);
    setNotice(null);
  }, [clearFlow]);

  useLayoutEffect(() => {
    const current = scopeRef.current;
    if (current.projectId === projectId && current.accessToken === accessToken) return;

    scopeRef.current = { projectId, accessToken };
    scopeGenerationRef.current += 1;
    statusRequestRef.current += 1;

    const starting = startingRef.current;
    startingRef.current = null;
    if (starting && !starting.popup.closed) starting.popup.close();

    const active = pendingRef.current;
    if (active) clearFlow(active);
    setStarting(false);
    setFlowActive(false);
    setPhase(null);
    setReceipt(null);
    setError(null);
    setNotice(null);
    setRefreshing(false);
  }, [accessToken, clearFlow, projectId]);

  useEffect(() => {
    const active = pendingRef.current;
    if (!active) return;

    active.intervalId = window.setInterval(() => {
      if (pendingRef.current === active && active.popup.closed && !active.completing) {
        failFlow(active, 'The GitHub authorization popup was closed before the connection completed.');
      }
    }, POPUP_POLL_INTERVAL_MS);
    active.timeoutId = window.setTimeout(() => {
      failFlow(active, 'GitHub authorization timed out. Start the connection again.');
    }, POPUP_TIMEOUT_MS);

    return () => {
      if (active.intervalId !== undefined) window.clearInterval(active.intervalId);
      if (active.timeoutId !== undefined) window.clearTimeout(active.timeoutId);
    };
  }, [failFlow, flowRevision]);

  useEffect(() => {
    const active = pendingRef.current;
    if (!active ||
        !isCurrentScope(active.projectId, active.accessToken, active.generation) ||
        !accessToken) return;
    let cancelled = false;
    void apiCall(async (token, tenantSelector) => {
      const context = await gatewayClient.getAuthorizationContext(token, tenantSelector);
      if (context.actorId !== active.actorId) {
        throw new Error('The signed-in identity changed during GitHub authorization.');
      }
    }).catch((reason: unknown) => {
      if (!cancelled &&
          pendingRef.current === active &&
          isCurrentScope(active.projectId, active.accessToken, active.generation)) {
        failFlow(active, `The current identity could not be confirmed. ${messageForError(reason)}`);
      }
    });
    return () => { cancelled = true; };
  }, [accessToken, apiCall, failFlow, isCurrentScope]);

  useLayoutEffect(() => () => {
    scopeGenerationRef.current += 1;
    statusRequestRef.current += 1;
    const starting = startingRef.current;
    startingRef.current = null;
    if (starting && !starting.popup.closed) starting.popup.close();

    const active = pendingRef.current;
    if (!active) return;
    pendingRef.current = null;
    if (active.intervalId !== undefined) window.clearInterval(active.intervalId);
    if (active.timeoutId !== undefined) window.clearTimeout(active.timeoutId);
    if (!active.popup.closed) active.popup.close();
  }, []);

  const completeFlow = useCallback(async (
    active: PendingConnection,
    callback: CopilotCallbackMessage,
  ) => {
    if (pendingRef.current !== active ||
        !isCurrentScope(active.projectId, active.accessToken, active.generation)) return;
    statusRequestRef.current += 1;
    active.completing = true;
    setPhase('Completing GitHub authorization…');
    try {
      const status = await apiCall(async (token, tenantSelector) => {
        const context = await gatewayClient.getAuthorizationContext(token, tenantSelector);
        if (context.actorId !== active.actorId) {
          throw new Error('The signed-in identity changed during GitHub authorization.');
        }
        if (pendingRef.current !== active ||
            !isCurrentScope(active.projectId, active.accessToken, active.generation)) {
          throw new Error('The selected project or identity changed during GitHub authorization.');
        }
        const completed = await gatewayClient.completeCopilotConnection(
          token,
          callback,
          tenantSelector,
        );
        receiptForProject(completed, active.connectionId, active.projectId);
        if (pendingRef.current !== active ||
            !isCurrentScope(active.projectId, active.accessToken, active.generation)) {
          throw new Error('The selected project or identity changed during GitHub authorization.');
        }
        const refreshed = await gatewayClient.getCopilotConnection(
          token,
          active.connectionId,
          tenantSelector,
        );
        return receiptForProject(refreshed, active.connectionId, active.projectId);
      });
      if (pendingRef.current !== active ||
          !isCurrentScope(active.projectId, active.accessToken, active.generation)) return;
      clearFlow(active);
      setReceipt(status);
      setError(null);
      setNotice(status.state === 'connected'
        ? 'GitHub Copilot is connected for this project.'
        : `GitHub authorization finished. Current owner status: ${stateLabel(status.state)}.`);
    } catch (reason) {
      if (pendingRef.current !== active ||
          !isCurrentScope(active.projectId, active.accessToken, active.generation)) return;
      failFlow(active, messageForError(reason));
    }
  }, [apiCall, clearFlow, failFlow, isCurrentScope]);

  useEffect(() => {
    const onMessage = (event: MessageEvent<unknown>) => {
      const active = pendingRef.current;
      if (!active ||
          active.completing ||
          !isCurrentScope(active.projectId, active.accessToken, active.generation) ||
          event.origin !== window.location.origin ||
          event.source !== active.popup ||
          !isCopilotCallbackMessage(event.data))
        return;

      const callback = event.data;
      if (callback.state !== active.state) {
        failFlow(active, 'The GitHub authorization response did not match this request. Start again.');
        return;
      }
      if ('error' in callback) {
        failFlow(active, 'GitHub authorization was declined.');
        return;
      }
      void completeFlow(active, callback);
    };
    window.addEventListener('message', onMessage);
    return () => window.removeEventListener('message', onMessage);
  }, [completeFlow, failFlow, isCurrentScope]);

  const connect = async () => {
    if (startingRef.current || pendingRef.current) return;
    const popup = window.open(
      'about:blank',
      'agentweaver-copilot-user',
      'popup,width=560,height=720',
    );
    if (!popup) {
      setError('Allow pop-ups to connect GitHub without losing this signed-in session.');
      setNotice(null);
      return;
    }

    const attempt: StartingConnection = {
      popup,
      projectId,
      accessToken,
      generation: scopeGenerationRef.current,
    };
    startingRef.current = attempt;
    statusRequestRef.current += 1;
    setRefreshing(false);
    setStarting(true);
    setError(null);
    setNotice(null);
    setReceipt(null);
    try {
      const callbackUri = copilotCallbackUri();
      const result = await apiCall(async (token, tenantSelector) => {
        const context = await gatewayClient.getAuthorizationContext(token, tenantSelector);
        if (startingRef.current !== attempt ||
            !isCurrentScope(attempt.projectId, attempt.accessToken, attempt.generation)) {
          throw new Error('The selected project or identity changed during GitHub authorization.');
        }
        const begin = await gatewayClient.beginCopilotConnection(
          token,
          'project',
          attempt.projectId,
          tenantSelector,
        );
        return { actorId: context.actorId, begin };
      });
      if (startingRef.current !== attempt ||
          !isCurrentScope(attempt.projectId, attempt.accessToken, attempt.generation)) {
        if (!popup.closed) popup.close();
        return;
      }
      if (!isCopilotConnectionBegin(result.begin) ||
          result.begin.connection.scope !== 'project' ||
          result.begin.connection.scopeId !== attempt.projectId) {
        throw new Error('The Gateway returned a connection outside this project.');
      }
      const state = expectedCopilotCallbackState(result.begin.authorizationUri, callbackUri);
      if (popup.closed) {
        throw new Error('The GitHub authorization popup was closed before it could start.');
      }

      const active: PendingConnection = {
        popup,
        state,
        actorId: result.actorId,
        connectionId: result.begin.connection.connectionId,
        projectId: attempt.projectId,
        accessToken: attempt.accessToken,
        generation: attempt.generation,
        completing: false,
      };
      startingRef.current = null;
      pendingRef.current = active;
      setReceipt(result.begin.connection);
      setPhase('Waiting for GitHub authorization…');
      setFlowActive(true);
      setFlowRevision((current) => current + 1);
      popup.location.replace(result.begin.authorizationUri);
    } catch (reason) {
      const active = pendingRef.current;
      if (active?.popup === attempt.popup &&
          active.generation === attempt.generation &&
          isCurrentScope(active.projectId, active.accessToken, active.generation)) {
        failFlow(active, messageForError(reason));
      } else if (isCurrentScope(attempt.projectId, attempt.accessToken, attempt.generation) &&
          startingRef.current === attempt) {
        startingRef.current = null;
        if (!popup.closed) popup.close();
        setError(messageForError(reason));
        setNotice(null);
      }
    } finally {
      if (startingRef.current === attempt) startingRef.current = null;
      if (isCurrentScope(attempt.projectId, attempt.accessToken, attempt.generation))
        setStarting(false);
    }
  };

  const refreshStatus = async () => {
    if (!receipt) return;
    const requestGeneration = ++statusRequestRef.current;
    const currentProjectId = projectId;
    const currentAccessToken = accessToken;
    const scopeGeneration = scopeGenerationRef.current;
    const isCurrentRequest = () =>
      statusRequestRef.current === requestGeneration &&
      isCurrentScope(currentProjectId, currentAccessToken, scopeGeneration);
    const currentReceipt = receipt;
    setRefreshing(true);
    setError(null);
    setNotice(null);
    try {
      const status = await apiCall((token, tenantSelector) =>
        gatewayClient.getCopilotConnection(token, currentReceipt.connectionId, tenantSelector));
      if (!isCurrentRequest()) return;
      setReceipt(receiptForProject(status, currentReceipt.connectionId, currentProjectId));
    } catch (reason) {
      if (isCurrentRequest()) setError(messageForError(reason));
    } finally {
      if (isCurrentRequest()) setRefreshing(false);
    }
  };

  return (
    <section className="v1-panel" aria-labelledby="copilot-user-connection-title">
      <div className="v1-panel-heading">
        <h2 id="copilot-user-connection-title">GitHub Copilot connection</h2>
        {receipt && (
          <Button
            appearance="secondary"
            disabled={refreshing || starting || flowActive}
            onClick={() => void refreshStatus()}
          >
            {refreshing ? 'Refreshing…' : 'Refresh status'}
          </Button>
        )}
      </div>
      <p className="v1-muted">
        Connect GitHub Copilot in a pop-up while keeping this signed-in settings session open.
        The owner response, not the callback page, determines connection status.
      </p>
      {receipt && (
        <p>
          Owner status: <strong>{stateLabel(receipt.state)}</strong>
          {' · '}Connection ID: <code>{receipt.connectionId}</code>
        </p>
      )}
      {phase && <p role="status">{phase}</p>}
      {error && <MessageBar intent="error" role="alert"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {notice && (
        <MessageBar intent={receipt?.state === 'connected' ? 'success' : 'info'} role="status">
          <MessageBarBody>{notice}</MessageBarBody>
        </MessageBar>
      )}
      <div className="v1-actions">
        <Button
          appearance="primary"
          disabled={starting || flowActive}
          onClick={() => void connect()}
        >
          {starting || flowActive ? 'Connecting…' : 'Connect GitHub Copilot'}
        </Button>
      </div>
    </section>
  );
}

function callbackPageMessage(): string {
  const callback = window.__AGENTWEAVER_COPILOT_CALLBACK__;
  if (!isCopilotCallbackMessage(callback))
    return 'The GitHub response was invalid. Close this window and start again.';
  const opener = window.opener;
  if (!opener || opener === window)
    return 'This response has no matching project settings window. Close this window and start again.';
  return 'Returning to the project settings window…';
}

export function CopilotPopupCallbackPage() {
  const [message] = useState(callbackPageMessage);
  const attempted = useRef(false);

  useEffect(() => {
    if (attempted.current) return;
    attempted.current = true;
    const callback = window.__AGENTWEAVER_COPILOT_CALLBACK__;
    window.__AGENTWEAVER_COPILOT_CALLBACK__ = undefined;
    if (!isCopilotCallbackMessage(callback)) return;
    const opener = window.opener;
    if (!opener || opener === window) return;
    opener.postMessage(callback, window.location.origin);
    window.close();
  }, []);

  return (
    <main className="v1-auth-page">
      <section className="v1-auth-card">
        <h1>Returning from GitHub</h1>
        <p role="status">{message}</p>
      </section>
    </main>
  );
}
