import { apiClient } from '../api/apiClient';
import { formatModelProviderErrorMessage } from '../api/errors';
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
import { useLocation } from 'react-router-dom';
import type { ProjectCopilotConnection } from '../api/types';
import { SetupReadiness } from './SetupReadiness';

const CONNECTION_LOAD_ERROR = 'The model provider status did not load. Reload the status and try again.';
const GITHUB_APPS_EXPLANATION = 'GitHub Copilot provides AI access. Repository authorization is managed separately.';

export function ProjectModelProviderSettings({
  projectId,
  triggerLabel = 'Manage GitHub Copilot',
  showConnectionStatus = false,
  suppressProjectOverrideWhenPlatformDefault = false,
  repairRequired = false,
}: {
  projectId: string;
  triggerLabel?: string;
  showConnectionStatus?: boolean;
  suppressProjectOverrideWhenPlatformDefault?: boolean;
  repairRequired?: boolean;
}) {
  const location = useLocation();
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
    } catch (err) {
      if (generation !== refreshGeneration.current) return;
      setConnection(null);
      setLoadError(formatModelProviderErrorMessage(err, CONNECTION_LOAD_ERROR));
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
      const handoff = await apiClient.beginProjectCopilotAuthorization(
        projectId,
        `${location.pathname}${location.search}`,
      );
      window.location.assign(handoff.authorization_url);
    } catch {
      setConnectionError('The GitHub Copilot authorization did not start. Try again.');
      setConnecting(false);
    }
  };

  const connected = connection?.status === 'connected';
  const effectiveSource = connection?.effective_source ?? (connected ? 'project' : 'none');
  const platformDefaultConnected = effectiveSource === 'platform_default';
  const byokConfigured = effectiveSource === 'byok';
  const accountLabel = connection?.github_login
    ? `@${connection.github_login}`
    : 'a GitHub account';
  const canManageProjectConnection = repairRequired || !suppressProjectOverrideWhenPlatformDefault
    || (!platformDefaultConnected && !byokConfigured);
  const noConnectionMessage = 'Choose a model provider before this project starts AI work.';
  const repairMessage = 'Reconnect the project GitHub Copilot authorization used for unattended AI work.';
  const platformDefaultMessage = `GitHub Copilot (${accountLabel}) supplies AI access. Scope: Platform.`;
  const byokConfiguredMessage = 'A custom-key model provider supplies AI access. Scope: Platform.';
  const projectConnectionMessage = `GitHub Copilot (${accountLabel}) supplies AI access. Scope: Project.`;
  const dialogDescription = 'Authorize GitHub Copilot signs you in with GitHub and creates a durable project binding for unattended AI work. It does not install a GitHub App or grant repository access.';
  const dialogConnectedMessage = connection?.github_login
    ? `GitHub Copilot (@${connection.github_login}) is ready. Scope: Project.`
    : 'GitHub Copilot is ready. Scope: Project.';
  const readinessStatus = !repairRequired && (connected || platformDefaultConnected || byokConfigured)
    ? 'ready' as const
    : 'action-required' as const;
  const readinessDescription = repairRequired
    ? repairMessage
    : connected
    ? projectConnectionMessage
    : platformDefaultConnected
      ? platformDefaultMessage
      : byokConfigured
        ? byokConfiguredMessage
        : noConnectionMessage;

  const trigger = canManageProjectConnection ? (
    <Button appearance={connected && !repairRequired ? 'secondary' : 'primary'} onClick={() => setOpen(true)}>
      {triggerLabel}
    </Button>
  ) : undefined;

  return (
    <>
      {showConnectionStatus && (
        <SetupReadiness
          compact
          model={{
            title: 'Model provider',
            description: GITHUB_APPS_EXPLANATION,
            loading,
            loadingLabel: 'Loading model provider status',
            error: loadError,
            items: [{
              id: 'project-model-provider',
              title: 'AI access',
              description: readinessDescription,
              requirement: 'required',
              status: readinessStatus,
            }],
          }}
          onRetry={() => void refreshConnection()}
          primaryAction={trigger}
        />
      )}
      {!showConnectionStatus && trigger}
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
            >Set up the project model provider</DialogTitle>
            <DialogContent>
              <p>{dialogDescription}</p>
              {loading && <Spinner label="Loading model provider status" />}
              {!loading && loadError && (
                <MessageBar intent="error"><MessageBarBody>{loadError}</MessageBarBody></MessageBar>
              )}
              {!loading && !loadError && connected && (
                <MessageBar intent="success">
                  <MessageBarBody>{dialogConnectedMessage}</MessageBarBody>
                </MessageBar>
              )}
              {!loading && !loadError && byokConfigured && !repairRequired && (
                <MessageBar intent="info">
                  <MessageBarBody>{byokConfiguredMessage}</MessageBarBody>
                </MessageBar>
              )}
              {!loading && !loadError && repairRequired && (
                <MessageBar intent="warning">
                  <MessageBarBody>{repairMessage}</MessageBarBody>
                </MessageBar>
              )}
              {!loading && !loadError && !connected && !byokConfigured && !repairRequired && (
                <MessageBar intent="warning">
                  <MessageBarBody>{noConnectionMessage} {GITHUB_APPS_EXPLANATION}</MessageBarBody>
                </MessageBar>
              )}
              {connectionError && (
                <MessageBar intent="error"><MessageBarBody>{connectionError}</MessageBarBody></MessageBar>
              )}
            </DialogContent>
            <DialogActions>
               <Button appearance="secondary" disabled={loading} onClick={() => void refreshConnection()}>
                Reload status
              </Button>
              <Button appearance="secondary" onClick={() => setOpen(false)}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={connecting}
                onClick={() => void connect()}
                style={{ whiteSpace: 'nowrap' }}
              >
                {connecting ? 'Opening GitHub' : connected ? 'Switch GitHub Copilot account' : 'Authorize GitHub Copilot'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </>
  );
}
