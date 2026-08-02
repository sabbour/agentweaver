import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Divider,
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
import {
  ArrowSwapRegular,
  AddRegular,
  ChevronDownRegular,
  ShieldPersonRegular,
  SignOutRegular,
} from '@fluentui/react-icons';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type { AuthMode, AuthSessionResponse, LinkedGitHubAccount, ProjectAccessOverview } from '../api/types';

const useStyles = makeStyles({
  trigger: {
    cursor: 'pointer',
    minWidth: 0,
    maxWidth: '100%',
    width: '100%',
    justifyContent: 'flex-start',
  },
  triggerInner: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    minWidth: 0,
    width: '100%',
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
  login: {
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
  entraBadge: {
    flexShrink: 0,
  },
  popoverIdentityBanner: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorNeutralForeground2,
  },
  popover: {
    width: '320px',
    maxWidth: 'calc(100vw - 32px)',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalM,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  sectionLabel: {
    color: tokens.colorNeutralForeground3,
  },
  accountButton: {
    justifyContent: 'flex-start',
    minHeight: '44px',
  },
  accountButtonInner: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    width: '100%',
    minWidth: 0,
  },
  accountMeta: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    minWidth: 0,
    flex: 1,
  },
  accountSecondary: {
    color: tokens.colorNeutralForeground2,
  },
  truncate: {
    minWidth: 0,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
    display: 'block',
    width: '100%',
  },
  triggerCollapsed: {
    justifyContent: 'center',
  },
  triggerInnerCollapsed: {
    justifyContent: 'center',
  },
});

function apiErrorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    if (err.status === 503) return 'GitHub sign-in is not configured on this server.';
    try {
      const problem = JSON.parse(err.body) as { detail?: string };
      if (problem.detail) return problem.detail;
    } catch { /* ignore */ }
    return `Error ${err.status}: ${err.body}`;
  }
  return err instanceof Error ? err.message : String(err);
}


export interface GitHubSignInProps {
  projectId?: string;
  /** Collapsed rail mode (icon-only sidebar) — hides label/chevron, keeps the trigger reachable via tooltip. */
  collapsed?: boolean;
}

export function GitHubSignIn({ projectId, collapsed }: GitHubSignInProps) {
  const styles = useStyles();
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [linkedAccounts, setLinkedAccounts] = useState<LinkedGitHubAccount[]>([]);
  const [projectAccess, setProjectAccess] = useState<ProjectAccessOverview | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const authSession = await apiClient.getAuthSession();
      setSession(authSession);
      if (!authSession.authenticated) {
        setLinkedAccounts([]);
        setProjectAccess(null);
        return;
      }

      if (authSession.auth_mode === 'entra') {
        const [accounts, access] = await Promise.all([
          apiClient.listLinkedGitHubAccounts().catch((err) => {
            if (err instanceof ApiError && err.status === 404) return [] as LinkedGitHubAccount[];
            throw err;
          }),
          projectId
            ? apiClient.getProjectAccessOverview(projectId).catch((err) => {
              if (err instanceof ApiError && err.status === 404) return null;
              throw err;
            })
            : Promise.resolve(null),
        ]);
        setLinkedAccounts(accounts);
        setProjectAccess(access);
      } else {
        setProjectAccess(null);
        setLinkedAccounts([]);
      }
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [projectId]);

  useEffect(() => {
    let cancelled = false;
    queueMicrotask(() => {
      void load().catch((err) => {
        if (!cancelled) setError(apiErrorMessage(err));
      });
    });
    return () => { cancelled = true; };
  }, [load]);

  const authMode: AuthMode | undefined = session?.auth_mode;

  const currentAccount = useMemo(() => {
    if (authMode === 'entra') {
      const login = projectAccess?.effective_github_login ?? linkedAccounts.find((account) => account.is_default)?.login ?? null;
      return login ? linkedAccounts.find((account) => account.login === login) ?? null : null;
    }
    return session?.login
      ? {
        login: session.login,
        name: session.display_name ?? session.login,
        avatar_url: session.avatar_url ?? '',
        type: 'user' as const,
        is_default: true,
        copilot_entitled: null,
      }
      : null;
  }, [authMode, linkedAccounts, projectAccess?.effective_github_login, session]);

  const otherAccounts = useMemo(
    () => linkedAccounts.filter((account) => account.login !== currentAccount?.login),
    [currentAccount?.login, linkedAccounts],
  );

  const handleSwitch = async (login: string) => {
    setSaving(login);
    setError(null);
    try {
      if (authMode === 'entra' && projectId) {
        await apiClient.setProjectGitHubIdentityOverride(projectId, login);
      } else {
        await apiClient.setDefaultLinkedGitHubAccount(login);
      }
      await load();
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setSaving(null);
    }
  };

  const handleSignOut = async () => {
    setSaving('__signout__');
    setError(null);
    try {
      await apiClient.signOutSession();
      window.location.href = '/';
    } catch (err) {
      setError(apiErrorMessage(err));
      setSaving(null);
    }
  };

  const handleAddAccount = async () => {
    setSaving('__addaccount__');
    setError(null);
    try {
      // The plain /auth/github/authorize redirect is a sign-in flow, not a link flow -- the
      // server ignores any "intent" query param there. Linking a second account requires
      // POST /auth/github-accounts/link, which registers a pending-link state so the OAuth
      // callback actually calls CompleteLinkAsync() instead of a normal sign-in exchange.
      const { authorize_url: authorizeUrl } = await apiClient.beginLinkGitHubAccount();
      window.location.href = authorizeUrl;
    } catch (err) {
      setError(apiErrorMessage(err));
      setSaving(null);
    }
  };

  if (loading) {
    return <Spinner size="extra-tiny" aria-label="Loading GitHub account switcher" />;
  }

  if (error && !session?.authenticated) {
    return null;
  }

  const triggerLabel = authMode === 'entra'
    ? (currentAccount?.login ?? 'Link GitHub')
    : (session?.login ?? 'GitHub');

  const triggerAvatar = currentAccount?.avatar_url;
  const permissionLabel = projectAccess?.effective_github_permission
    ? `${projectAccess.effective_github_permission} access`
    : null;

  const tooltipContent = authMode === 'entra'
    ? 'Signed in with Microsoft Entra ID · Click to manage linked GitHub accounts'
    : 'Click to manage your GitHub sign-in';

  return (
    <Popover positioning="above-start">
      <PopoverTrigger disableButtonEnhancement>
        <Tooltip content={tooltipContent} relationship="description" withArrow positioning="above">
          <Button
            appearance="transparent"
            className={collapsed ? mergeClasses(styles.trigger, styles.triggerCollapsed) : styles.trigger}
            type="button"
            aria-label="GitHub account switcher"
          >
            <span className={collapsed ? mergeClasses(styles.triggerInner, styles.triggerInnerCollapsed) : styles.triggerInner}>
              {triggerAvatar ? (
                <img src={triggerAvatar} alt="" className={styles.avatar} />
              ) : (
                <span className={styles.fallbackAvatar}><ArrowSwapRegular /></span>
              )}
              {!collapsed && (
                <>
                  <Text className={styles.login}>{triggerLabel}</Text>
                  {authMode === 'entra' && (
                    <Badge
                      className={styles.entraBadge}
                      appearance="tint"
                      color="brand"
                      size="small"
                      icon={<ShieldPersonRegular />}
                    >
                      Entra ID
                    </Badge>
                  )}
                  <span className={styles.chevron}><ChevronDownRegular /></span>
                </>
              )}
            </span>
          </Button>
        </Tooltip>
      </PopoverTrigger>
      <PopoverSurface>
        <div className={styles.popover}>
          {authMode === 'entra' && (
            <div className={styles.popoverIdentityBanner}>
              <ShieldPersonRegular />
              <Text size={200}>Signed in with Microsoft Entra ID</Text>
            </div>
          )}
          <div className={styles.section}>
            <Text weight="semibold">Current GitHub account</Text>
            {currentAccount ? (
              <div className={styles.accountButtonInner}>
                {currentAccount.avatar_url ? (
                  <img src={currentAccount.avatar_url} alt="" className={styles.avatar} />
                ) : (
                  <span className={styles.fallbackAvatar}><ArrowSwapRegular /></span>
                )}
                <div className={styles.accountMeta}>
                  <Text weight="semibold" className={styles.truncate}>{currentAccount.name ?? currentAccount.login}</Text>
                  <Text size={200} className={mergeClasses(styles.accountSecondary, styles.truncate)}>
                    @{currentAccount.login}
                    {permissionLabel ? ` · ${permissionLabel}` : ''}
                  </Text>
                </div>
                <ArrowSwapRegular />
              </div>
            ) : (
              <Text size={200} className={styles.accountSecondary}>
                No GitHub account is active yet.
              </Text>
            )}
          </div>

          {authMode === 'entra' && (
            <>
              <Divider />
              <div className={styles.section}>
                <Text size={200} className={styles.sectionLabel}>Switch account</Text>
                {otherAccounts.length > 0 ? (
                  otherAccounts.map((account) => (
                    <Button
                      key={account.login}
                      appearance="subtle"
                      className={styles.accountButton}
                      disabled={saving !== null}
                      onClick={() => void handleSwitch(account.login)}
                    >
                      <span className={styles.accountButtonInner}>
                        {account.avatar_url ? (
                          <img src={account.avatar_url} alt="" className={styles.avatar} />
                        ) : (
                          <span className={styles.fallbackAvatar}><ArrowSwapRegular /></span>
                        )}
                        <span className={styles.accountMeta}>
                          <Text weight="semibold" className={styles.truncate}>{account.name ?? account.login}</Text>
                          <Text size={200} className={mergeClasses(styles.accountSecondary, styles.truncate)}>@{account.login}</Text>
                        </span>
                        {saving === account.login ? <Spinner size="tiny" /> : null}
                      </span>
                    </Button>
                  ))
                ) : (
                  <Text size={200} className={styles.accountSecondary}>
                    {linkedAccounts.length === 0
                      ? 'No linked GitHub accounts yet.'
                      : 'No other linked GitHub accounts are available.'}
                  </Text>
                )}
              </div>
            </>
          )}

          <Divider />
          <div className={styles.section}>
            {authMode === 'entra' && (
              <Button appearance="subtle" icon={<AddRegular />} disabled={saving !== null} onClick={() => void handleAddAccount()}>
                {saving === '__addaccount__' ? <Spinner size="tiny" /> : 'Add account'}
              </Button>
            )}
            <Button
              appearance="subtle"
              icon={<SignOutRegular />}
              disabled={saving === '__signout__'}
              onClick={() => void handleSignOut()}
            >
              Sign out…
            </Button>
            {error && <Text size={200} className={styles.accountSecondary}>{error}</Text>}
          </div>
        </div>
      </PopoverSurface>
    </Popover>
  );
}
