import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiErrorMessage, parseApiBody } from '../api/errors';
import {
  Button,
  Divider,
  Link,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Spinner,
  Text,
  Tooltip,
  mergeClasses,
} from '@fluentui/react-components';
import { ChevronDownRegular, PersonRegular, SignOutRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import { clearSessionAuth } from '../config';
import { useIdentityMenuStyles } from './identityMenuStyles';
import type { AuthSessionResponse, ProjectCopilotConnection } from '../api/types';
import type { ReactNode } from 'react';

const useStyles = useIdentityMenuStyles;

function identityLabel(session: AuthSessionResponse | null): string {
  if (session?.authenticated === false) return 'Not signed in';
  return session?.display_name ?? session?.login ?? session?.email ?? 'GitHub identity';
}

function connectionStatus(connection: ProjectCopilotConnection | null): string {
  if (connection?.effective_source === 'byok') {
    return 'Custom key configured in Platform settings';
  }
  if (connection?.effective_source === 'platform_default') {
    return connection.github_login
      ? `Platform default @${connection.github_login}`
      : 'Platform default configured';
  }
  if (connection?.status === 'connected') {
    return connection.github_login
      ? `Connected as @${connection.github_login}`
      : 'Connected';
  }
  return 'Not connected';
}

function formatProjectCopilotStatusError(err: unknown): string {
  if (err instanceof ApiError) {
    switch (parseApiBody(err.body).error) {
      case 'project_owner_required':
        return 'Only a project owner can view this project’s GitHub Copilot connection.';
      case 'human_entra_subject_required':
        return 'Sign in with your Microsoft Entra work account to view this project’s GitHub status.';
      case 'project_model_provider_reconnect_required':
        return 'Reconnect the project GitHub Copilot authorization used for unattended AI work.';
    }
  }
  return formatApiErrorMessage(err, 'Could not load the project Copilot connection status.');
}

export interface GitHubIdentityBadgeProps {
  projectId?: string;
  collapsed?: boolean;
  footerMeta?: ReactNode;
}

/**
 * Loads and renders the signed-in GitHub identity and, when a project is supplied, that
 * project's Copilot model-provider connection through the API. The effects track loading and
 * failures; props control the target and display.
 */
export function GitHubIdentityBadge({ projectId, collapsed, footerMeta }: GitHubIdentityBadgeProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [connection, setConnection] = useState<ProjectCopilotConnection | null>(null);
  const [connectionLoading, setConnectionLoading] = useState(false);
  const [copilotConnectionError, setCopilotConnectionError] = useState<string | null>(null);
  const [signingOut, setSigningOut] = useState(false);

  useEffect(() => {
    let active = true;
    void apiClient.getAuthSession()
      .then((nextSession) => {
        if (!active) return;
        setSession(nextSession);
        setSessionError(null);
      })
      .catch(() => {
        if (active) setSessionError('Could not load the signed-in identity.');
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    let active = true;
    if (!open || !projectId) return () => { active = false; };

    queueMicrotask(() => {
      if (!active) return;
      setConnectionLoading(true);
      setCopilotConnectionError(null);
      void apiClient.getProjectCopilotConnection(projectId)
        .then((nextConnection) => {
          if (!active) return;
          setConnection(nextConnection);
        })
        .catch((err: unknown) => {
          if (!active) return;
          setConnection(null);
          setCopilotConnectionError(formatProjectCopilotStatusError(err));
        })
        .finally(() => {
          if (active) setConnectionLoading(false);
        });
    });

    return () => {
      active = false;
    };
  }, [open, projectId]);

  const signOut = async () => {
    setSigningOut(true);
    try {
      await apiClient.signOutSession();
      clearSessionAuth(true);
      window.location.assign('/');
    } catch (err) {
      setSessionError(formatApiErrorMessage(err, 'Could not sign out. Try again.'));
      setSigningOut(false);
    }
  };

  const label = identityLabel(session);

  return (
    <Popover open={open} onOpenChange={(_, data) => setOpen(data.open)} positioning="above-start">
      <PopoverTrigger disableButtonEnhancement>
        <Tooltip
          content="View signed-in identity"
          relationship="label"
          positioning="above"
        >
          <Button
            appearance="transparent"
            type="button"
            aria-label="GitHub identity"
            className={collapsed ? mergeClasses(styles.trigger, styles.triggerCollapsed) : styles.trigger}
          >
            <span className={collapsed ? mergeClasses(styles.triggerInner, styles.triggerInnerCollapsed) : styles.triggerInner}>
              {session?.avatar_url ? (
                <img src={session.avatar_url} alt="" className={styles.avatar} />
              ) : (
                <span className={styles.fallbackAvatar} aria-hidden="true"><PersonRegular /></span>
              )}
              {!collapsed && (
                <>
                  <Text className={styles.label}>{label}</Text>
                  <ChevronDownRegular className={styles.chevron} aria-hidden="true" />
                </>
              )}
            </span>
          </Button>
        </Tooltip>
      </PopoverTrigger>
      <PopoverSurface>
        <div className={styles.popover}>
          <div className={styles.section}>
            <Text weight="semibold">Signed in</Text>
            <div className={styles.identity}>
              {session?.avatar_url ? (
                <img src={session.avatar_url} alt="" className={styles.avatar} />
              ) : (
                <span className={styles.fallbackAvatar} aria-hidden="true"><PersonRegular /></span>
              )}
              <div className={styles.identityDetails}>
                <Text weight="semibold" className={styles.truncate}>{label}</Text>
                {session?.login && <Text size={200} className={mergeClasses(styles.secondary, styles.truncate)}>@{session.login}</Text>}
                {!session?.login && session?.email && <Text size={200} className={mergeClasses(styles.secondary, styles.truncate)}>{session.email}</Text>}
              </div>
            </div>
            {sessionError && <Text size={200} className={styles.secondary}>{sessionError}</Text>}
          </div>

          {projectId && (
            <>
              <Divider />
              <div className={styles.section}>
                <Text weight="semibold">Model provider</Text>
                {connectionLoading && <Spinner label="Loading model provider status" size="extra-tiny" />}
                {!connectionLoading && (
                  <Text size={200} className={styles.secondary}>
                    {copilotConnectionError ?? connectionStatus(connection)}
                  </Text>
                )}
                <Link href={`/projects/${encodeURIComponent(projectId)}/settings`}>
                  Manage project connections
                </Link>
                <Link href="/settings">Manage account GitHub connections</Link>
              </div>
            </>
          )}

          <Divider />
          <div className={styles.footer} data-testid="github-identity-menu-footer">
            <Button
              appearance="subtle"
              icon={<SignOutRegular />}
              disabled={signingOut}
              onClick={() => void signOut()}
              className={styles.signOut}
            >
              {signingOut ? 'Signing out…' : 'Sign out'}
            </Button>
            {footerMeta && <div className={styles.footerMeta}>{footerMeta}</div>}
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}
