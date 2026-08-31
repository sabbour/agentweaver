import { apiClient } from '../api/apiClient';
import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  MessageBar,
  MessageBarBody,
  Spinner,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useCallback, useEffect, useRef, useState } from 'react';
import type { ProjectCopilotConnection } from '../api/types';

const CONNECTION_LOAD_ERROR = 'Could not load this project’s GitHub Copilot connection. Refresh and try again.';
const GITHUB_APPS_EXPLANATION = 'GitHub Copilot provides AI access. The separate Repo App provides repository access.';

export function GitHubCopilotConnectionPicker({
  projectId,
  triggerLabel = 'Manage GitHub Copilot',
  showConnectionStatus = false,
}: {
  projectId: string;
  triggerLabel?: string;
  showConnectionStatus?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [connection, setConnection] = useState<ProjectCopilotConnection | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [connectionError, setConnectionError] = useState<string | null>(null);
  const refreshGeneration = useRef(0);

  const refreshConnection = useCallback(async () => {
    const generation = ++refreshGeneration.current;
    setLoading(true);
    setLoadError(null);
    try {
      const nextConnection = await apiClient.getProjectCopilotConnection(projectId);
      if (generation !== refreshGeneration.current) return;
      setConnection(nextConnection);
    } catch {
      if (generation !== refreshGeneration.current) return;
      setConnection(null);
      setLoadError(CONNECTION_LOAD_ERROR);
    } finally {
      if (generation === refreshGeneration.current) setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    let cancelled = false;
    if (showConnectionStatus || open) {
      queueMicrotask(() => {
        if (!cancelled) void refreshConnection();
      });
    }
    return () => {
      cancelled = true;
      refreshGeneration.current += 1;
    };
  }, [open, refreshConnection, showConnectionStatus]);

  const connect = async () => {
    setConnecting(true);
    setConnectionError(null);
    try {
      const handoff = await apiClient.beginProjectCopilotAuthorization(projectId);
      window.location.assign(handoff.authorization_url);
    } catch {
      setConnectionError('The GitHub Copilot App connection could not be started. Try again.');
      setConnecting(false);
    }
  };

  const connected = connection?.status === 'connected';
  const accountLabel = connection?.github_login
    ? `@${connection.github_login}`
    : 'a GitHub account';

  return (
    <>
      {showConnectionStatus && (
        <div>
          {loading && <Spinner label="Loading GitHub account" size="extra-tiny" />}
          {!loading && loadError && (
            <MessageBar intent="error"><MessageBarBody>{loadError}</MessageBarBody></MessageBar>
          )}
          {!loading && !loadError && connected && (
            <MessageBar intent="success">
              <MessageBarBody>GitHub Copilot is connected as {accountLabel}. {GITHUB_APPS_EXPLANATION}</MessageBarBody>
            </MessageBar>
          )}
          {!loading && !loadError && !connected && (
            <MessageBar intent="warning">
              <MessageBarBody>No GitHub account is connected to this project for Copilot. {GITHUB_APPS_EXPLANATION}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      )}
      <Button appearance={connected ? 'secondary' : 'primary'} onClick={() => setOpen(true)}>
        {triggerLabel}
      </Button>
      <Dialog open={open} onOpenChange={(_, data) => setOpen(data.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle
              action={
                <Button
                  appearance="subtle"
                  aria-label="Close"
                  icon={<DismissRegular />}
                  onClick={() => setOpen(false)}
                />
              }
            >Connect GitHub Copilot</DialogTitle>
            <DialogContent>
              <p>
                Choose the GitHub account with Copilot access in GitHub’s secure browser page.
                {` ${GITHUB_APPS_EXPLANATION}`} Agentweaver keeps credentials private and uses this account only for this project.
              </p>
              {loading && <Spinner label="Loading GitHub connection" />}
              {!loading && loadError && (
                <MessageBar intent="error"><MessageBarBody>{loadError}</MessageBarBody></MessageBar>
              )}
              {!loading && !loadError && connected && (
                <MessageBar intent="success">
                  <MessageBarBody>Connected GitHub account: {accountLabel}</MessageBarBody>
                </MessageBar>
              )}
              {!loading && !loadError && !connected && (
                <MessageBar intent="warning">
                  <MessageBarBody>No GitHub account is connected to this project for Copilot. {GITHUB_APPS_EXPLANATION}</MessageBarBody>
                </MessageBar>
              )}
              {connectionError && (
                <MessageBar intent="error"><MessageBarBody>{connectionError}</MessageBarBody></MessageBar>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={loading} onClick={() => void refreshConnection()}>
                Refresh
              </Button>
              <Button appearance="secondary" onClick={() => setOpen(false)}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={connecting}
                onClick={() => void connect()}
                style={{ whiteSpace: 'nowrap' }}
              >
                {connecting ? 'Opening GitHub…' : connected ? 'Switch GitHub account' : 'Connect GitHub account'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}
