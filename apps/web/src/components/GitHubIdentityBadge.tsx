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
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import { ChevronDownRegular, PersonRegular, SignOutRegular } from '@fluentui/react-icons';
import { useEffect, useState } from 'react';
import { clearSessionAuth } from '../config';
import type { AuthSessionResponse, ProjectAccessOverview, ProjectCopilotConnection } from '../api/types';

const useStyles = makeStyles({
  trigger: {
    cursor: 'pointer',
    minWidth: 0,
    width: '100%',
    justifyContent: 'flex-start',
  },
  triggerCollapsed: {
    justifyContent: 'center',
  },
  triggerInner: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
    width: '100%',
  },
  triggerInnerCollapsed: {
    justifyContent: 'center',
  },
  avatar: {
    width: '28px',
    height: '28px',
    borderRadius: '50%',
    objectFit: 'cover',
    flexShrink: 0,
  },
  fallbackAvatar: {
    width: '28px',
    height: '28px',
    borderRadius: '50%',
    backgroundColor: tokens.colorNeutralBackground4,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
    color: tokens.colorNeutralForeground2,
  },
  label: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    flex: 1,
  },
  chevron: {
    color: tokens.colorNeutralForeground3,
    flexShrink: 0,
  },
  popover: {
    width: '320px',
    maxWidth: 'calc(100vw - 32px)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalM,
  },
  identity: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
  },
  identityDetails: {
    display: 'flex',
    flexDirection: 'column',
    minWidth: 0,
  },
  secondary: {
    color: tokens.colorNeutralForeground2,
  },
  truncate: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalS,
  },
});

function identityLabel(session: AuthSessionResponse | null): string {
  if (session?.authenticated === false) return 'Not signed in';
  return session?.display_name ?? session?.login ?? session?.email ?? 'GitHub identity';
}

function connectionStatus(connection: ProjectCopilotConnection | null): string {
  if (connection?.status === 'connected') {
    return connection.github_login
      ? `Connected as @${connection.github_login}`
      : 'Connected';
  }
  return 'Not connected';
}

function repositoryStatus(access: ProjectAccessOverview | null): string {
  return access?.effective_github_login
    ? `Repository access: @${access.effective_github_login}`
    : 'Repository access: not connected';
}

function formatProjectAccessStatusError(err: unknown): string {
  if (err instanceof ApiError && err.status === 404) {
    return 'Repository access status is unavailable because this deployment did not return a project access snapshot.';
  }
  return formatApiErrorMessage(err, 'Could not load the repository access status.');
}

function formatProjectCopilotStatusError(err: unknown): string {
  if (err instanceof ApiError) {
    switch (parseApiBody(err.body).error) {
      case 'project_owner_required':
        return 'Only a project owner can view this project’s GitHub Copilot connection.';
      case 'human_entra_subject_required':
        return 'Sign in with your Microsoft Entra work account to view this project’s GitHub status.';
      case 'github_binding_unavailable':
        return 'The project’s GitHub Copilot connection is currently unavailable. Retry, or reconnect it from Project settings.';
    }
  }
  return formatApiErrorMessage(err, 'Could not load the project Copilot connection status.');
}

export interface GitHubIdentityBadgeProps {
  projectId?: string;
  collapsed?: boolean;
}

export function GitHubIdentityBadge({ projectId, collapsed }: GitHubIdentityBadgeProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [connection, setConnection] = useState<ProjectCopilotConnection | null>(null);
  const [connectionLoading, setConnectionLoading] = useState(false);
  const [access, setAccess] = useState<ProjectAccessOverview | null>(null);
  const [accessError, setAccessError] = useState<string | null>(null);
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
      setAccessError(null);
      setCopilotConnectionError(null);
      void Promise.allSettled([
        apiClient.getProjectCopilotConnection(projectId),
        apiClient.getProjectAccessOverview(projectId),
      ]).then(([connectionResult, accessResult]) => {
        if (!active) return;
        if (connectionResult.status === 'fulfilled') {
          setConnection(connectionResult.value);
        } else {
          setConnection(null);
          setCopilotConnectionError(formatProjectCopilotStatusError(connectionResult.reason));
        }

        if (accessResult.status === 'fulfilled') {
          setAccess(accessResult.value);
        } else {
          setAccess(null);
          setAccessError(formatProjectAccessStatusError(accessResult.reason));
        }
      }).finally(() => {
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
      clearSessionAuth();
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
                <Text weight="semibold">Project GitHub status</Text>
                {connectionLoading && <Spinner label="Loading GitHub connection status" size="extra-tiny" />}
                {!connectionLoading && (
                  <>
                    <Text size={200} className={styles.secondary}>
                      {accessError ?? repositoryStatus(access)}
                    </Text>
                    <Text size={200} className={styles.secondary}>
                      {copilotConnectionError ?? `AI source: GitHub Copilot — ${connectionStatus(connection)}`}
                    </Text>
                  </>
                )}
                <Link href={`/projects/${encodeURIComponent(projectId)}/settings`}>
                  Manage project connections
                </Link>
                <Link href="/settings">Manage account GitHub connections</Link>
              </div>
            </>
          )}

          <Divider />
          <Button
            appearance="subtle"
            icon={<SignOutRegular />}
            disabled={signingOut}
            onClick={() => void signOut()}
          >
            {signingOut ? 'Signing out…' : 'Sign out'}
          </Button>
        </div>
      </PopoverSurface>
    </Popover>
  );
}
