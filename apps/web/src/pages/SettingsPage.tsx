import { apiClient } from '../api/apiClient';
import { MCP_URL, GITHUB_AUTHORIZE_URL } from '../config';
import { ApiError } from '../api/client';
import {
  Badge,
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useState } from 'react';
import type { AuthMode, AuthSessionResponse, LinkedGitHubAccount, SandboxPolicy, ServerInfo } from '../api/types';
import {
  Body,
  Label,
  MetricRow,
  PageContainer,
  PageHeader,
  PageSection,
  TitleText,
} from '../components/ui';

const useStyles = makeStyles({
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '760px',
  },
  listBox: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  listItem: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    padding: `${tokens.spacingVerticalXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },
  emptyNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  formActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
    flexWrap: 'wrap',
  },
  badgeRow: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
  },
  subBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  accountList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  accountRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
    padding: tokens.spacingHorizontalM,
    backgroundColor: tokens.colorNeutralBackground1,
    flexWrap: 'wrap',
  },
  accountIdentity: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalM,
    minWidth: 0,
  },
  accountAvatar: {
    width: '40px',
    height: '40px',
    borderRadius: tokens.borderRadiusCircular,
    objectFit: 'cover',
    flexShrink: 0,
  },
  accountText: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  accountMeta: {
    color: tokens.colorNeutralForeground2,
  },
  accountActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
});

const AUTH_MODE_LABELS: Record<AuthMode, string> = {
  entra: 'Entra ID',
  'github-legacy': 'GitHub',
};

function formatError(err: unknown): string {
  return err instanceof ApiError
    ? `API error ${err.status}: ${err.body}`
    : err instanceof Error
      ? err.message
      : String(err);
}

function authModeLabel(mode: AuthMode | undefined): string {
  return mode ? AUTH_MODE_LABELS[mode] : 'GitHub';
}

function goToGitHubLinkFlow() {
  const url = new URL(GITHUB_AUTHORIZE_URL);
  // Assumption for Tank's Entra-linked GitHub flow: the backend distinguishes a
  // post-sign-in link round-trip from an initial sign-in via `intent=link`, then
  // returns the browser to this page after the OAuth callback completes.
  url.searchParams.set('intent', 'link');
  if (typeof window !== 'undefined') {
    url.searchParams.set('return_to', `${window.location.pathname}${window.location.search}`);
    window.location.href = url.toString();
  }
}

export function SettingsPage() {
  const styles = useStyles();
  const [repositoryPath, setRepositoryPath] = useState('');
  const [policy, setPolicy] = useState<SandboxPolicy | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);

  const [serverInfo, setServerInfo] = useState<ServerInfo | null>(null);
  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authError, setAuthError] = useState<string | null>(null);

  const [linkedAccounts, setLinkedAccounts] = useState<LinkedGitHubAccount[]>([]);
  const [accountsLoading, setAccountsLoading] = useState(false);
  const [accountsError, setAccountsError] = useState<string | null>(null);
  const [accountActionError, setAccountActionError] = useState<string | null>(null);
  const [accountActionKey, setAccountActionKey] = useState<string | null>(null);
  const [unlinkCandidate, setUnlinkCandidate] = useState<LinkedGitHubAccount | null>(null);

  const authMode: AuthMode = session?.auth_mode ?? serverInfo?.auth_mode ?? 'github-legacy';

  const loadLinkedAccounts = useCallback(async () => {
    setAccountsLoading(true);
    setAccountsError(null);
    try {
      const accounts = await apiClient.listLinkedGitHubAccounts();
      setLinkedAccounts(accounts);
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) {
        setAccountsError('Linked GitHub account management is not available on this deployment yet.');
        setLinkedAccounts([]);
      } else {
        setAccountsError(formatError(err));
      }
    } finally {
      setAccountsLoading(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    void Promise.all([
      apiClient.getServerInfo(),
      apiClient.getAuthSession(),
    ])
      .then(([server, authSession]) => {
        if (cancelled) return;
        setServerInfo(server);
        setSession(authSession);
      })
      .catch((err) => {
        if (!cancelled) setAuthError(formatError(err));
      })
      .finally(() => {
        if (!cancelled) setAuthLoading(false);
      });

    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (authMode !== 'entra') return;
    queueMicrotask(() => { void loadLinkedAccounts(); });
  }, [authMode, loadLinkedAccounts]);

  const platformRoles = useMemo(
    () => (session?.platform_roles ?? []).filter((role, index, all) => all.indexOf(role) === index),
    [session?.platform_roles],
  );

  const handleSetDefault = async (login: string) => {
    setAccountActionKey(`default:${login}`);
    setAccountActionError(null);
    try {
      await apiClient.setDefaultLinkedGitHubAccount(login);
      await loadLinkedAccounts();
    } catch (err) {
      setAccountActionError(formatError(err));
    } finally {
      setAccountActionKey(null);
    }
  };

  const handleUnlink = async (login: string) => {
    setAccountActionKey(`unlink:${login}`);
    setAccountActionError(null);
    try {
      await apiClient.unlinkLinkedGitHubAccount(login);
      await loadLinkedAccounts();
    } catch (err) {
      setAccountActionError(formatError(err));
    } finally {
      setAccountActionKey(null);
    }
  };

  const unlinkWarnings = unlinkCandidate
    ? [
      ...(unlinkCandidate.unlink_warnings ?? []),
      ...(unlinkCandidate.is_default
        ? [linkedAccounts.length === 1
          ? 'This is your only linked GitHub account. Projects that rely on GitHub will lose repository and Copilot access until you link another account.'
          : 'This is your default GitHub account. Choose another default if you still want a fallback identity for projects without an override.']
        : []),
      ...((unlinkCandidate.dependent_project_names?.length ?? 0) > 0
        ? [`Projects using this account directly: ${(unlinkCandidate.dependent_project_names ?? []).join(', ')}.`]
        : []),
      ...(unlinkCandidate.default_for_project_count && unlinkCandidate.default_for_project_count > 0
        ? [`This account is currently the default fallback for ${unlinkCandidate.default_for_project_count} project(s).`]
        : []),
      ...(unlinkCandidate.override_project_count && unlinkCandidate.override_project_count > 0
        ? [`This account is selected explicitly on ${unlinkCandidate.override_project_count} project(s).`]
        : []),
    ]
    : [];

  const handleFetch = async () => {
    if (!repositoryPath.trim()) return;
    setLoading(true);
    setFetchError(null);
    setPolicy(null);
    setSaveSuccess(false);
    setSaveError(null);
    try {
      const result = await apiClient.getSandboxPolicy(repositoryPath.trim());
      setPolicy(result);
    } catch (err) {
      setFetchError(formatError(err));
    } finally {
      setLoading(false);
    }
  };

  const handleSave = async () => {
    if (!policy) return;
    setSaving(true);
    setSaveError(null);
    setSaveSuccess(false);
    try {
      const updated = await apiClient.updateSandboxPolicy(policy);
      setPolicy(updated);
      setSaveSuccess(true);
    } catch (err) {
      setSaveError(formatError(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <PageContainer width="readable">
      <PageHeader
        title="Account settings"
        description="Manage authentication, linked GitHub accounts, MCP access, and local repository sandbox policy."
      />

      <PageSection
        title="Authentication"
        description="Review how this deployment authenticates you and, in Entra ID mode, which platform roles and GitHub accounts are currently linked."
      >
        <div className={styles.section}>
          {authLoading && <Spinner label="Loading authentication settings" />}
          {authError && (
            <MessageBar intent="error">
              <MessageBarBody>{authError}</MessageBarBody>
            </MessageBar>
          )}
          {!authLoading && !authError && (
            <>
              <MetricRow
                items={[
                  { label: 'Authentication mode', value: authModeLabel(authMode) },
                  { label: 'Signed in as', value: session?.display_name ?? session?.login ?? 'Current user' },
                  {
                    label: 'Platform roles',
                    value: authMode === 'entra'
                      ? (platformRoles.length > 0 ? platformRoles.join(', ') : 'No app roles assigned')
                      : 'Project ownership and GitHub sign-in',
                  },
                ]}
              />

              {authMode === 'entra' ? (
                <div className={styles.subBlock}>
                  <TitleText>Platform access</TitleText>
                  <Body tone="muted">
                    Platform roles are assigned in Microsoft Entra ID. Agentweaver shows them here,
                    but cannot grant or revoke them from this screen.
                  </Body>
                  <div className={styles.badgeRow}>
                    {platformRoles.length > 0 ? (
                      platformRoles.map((role) => (
                        <Badge key={role} appearance="filled">{role}</Badge>
                      ))
                    ) : (
                      <Label as="span" className={styles.emptyNote}>No Entra app roles are currently assigned.</Label>
                    )}
                  </div>
                </div>
              ) : (
                <MessageBar intent="info">
                  <MessageBarBody>
                    This deployment uses GitHub authentication. Entra app-role mapping and multi-account
                    GitHub linking stay off in this mode.
                  </MessageBarBody>
                </MessageBar>
              )}
            </>
          )}
        </div>
      </PageSection>

      <PageSection
        title="Linked GitHub accounts"
        description="In Entra ID mode, one signed-in user can link multiple GitHub identities and choose which one Agentweaver uses by default."
        actions={authMode === 'entra' ? (
          <Button appearance="primary" onClick={goToGitHubLinkFlow}>
            Link another GitHub account
          </Button>
        ) : undefined}
      >
        <div className={styles.section}>
          {authMode !== 'entra' ? (
            <MessageBar intent="info">
              <MessageBarBody>
                Linked GitHub accounts are available when this deployment uses Entra ID. In GitHub mode,
                your signed-in GitHub session remains the single source of repository access.
              </MessageBarBody>
            </MessageBar>
          ) : (
            <>
              <Body tone="muted">
                Agentweaver project roles and GitHub repository permissions are separate. Your linked GitHub account's real repository permission controls what GitHub operations succeed.
              </Body>
              <Body tone="muted">
                Linking another account starts a fresh GitHub OAuth flow and returns here when the new account is attached to your current Entra session.
              </Body>
              {accountsLoading && <Spinner label="Loading linked GitHub accounts" />}
              {accountsError && (
                <MessageBar intent="warning">
                  <MessageBarBody>{accountsError}</MessageBarBody>
                </MessageBar>
              )}
              {accountActionError && (
                <MessageBar intent="error">
                  <MessageBarBody>{accountActionError}</MessageBarBody>
                </MessageBar>
              )}
              {!accountsLoading && !accountsError && (
                <div className={styles.accountList}>
                  {linkedAccounts.length === 0 ? (
                    <Label as="span" className={styles.emptyNote}>No GitHub accounts are linked yet.</Label>
                  ) : (
                    linkedAccounts.map((account) => (
                      <div key={account.login} className={styles.accountRow}>
                        <div className={styles.accountIdentity}>
                          <img src={account.avatar_url} alt="" className={styles.accountAvatar} />
                          <div className={styles.accountText}>
                            <TitleText>{account.name ?? account.login}</TitleText>
                            <Body className={styles.accountMeta} tone="muted">@{account.login}</Body>
                            <div className={styles.badgeRow}>
                              {account.is_default && <Badge appearance="filled">Default</Badge>}
                              <Badge appearance="outline">{account.type === 'org' ? 'Organization' : 'User'}</Badge>
                              <Badge appearance={account.copilot_entitled ? 'filled' : 'tint'}>
                                {account.copilot_entitled === null
                                  ? 'Copilot status pending'
                                  : account.copilot_entitled
                                    ? 'Copilot included'
                                    : 'No Copilot entitlement'}
                              </Badge>
                            </div>
                          </div>
                        </div>
                        <div className={styles.accountActions}>
                          {!account.is_default && (
                            <Button
                              appearance="secondary"
                              disabled={accountActionKey !== null}
                              onClick={() => void handleSetDefault(account.login)}
                            >
                              {accountActionKey === `default:${account.login}` ? 'Saving' : 'Set as default'}
                            </Button>
                          )}
                          <Button
                            appearance="subtle"
                            disabled={accountActionKey !== null}
                            onClick={() => setUnlinkCandidate(account)}
                          >
                            {accountActionKey === `unlink:${account.login}` ? 'Unlinking' : 'Unlink'}
                          </Button>
                        </div>
                      </div>
                    ))
                  )}
                </div>
              )}
            </>
          )}
        </div>
      </PageSection>

      {unlinkCandidate && (
        <PageSection
          title="Unlink GitHub account"
          description={`Review what changes if you remove @${unlinkCandidate.login}.`}
        >
          <div className={styles.section}>
            <Body>
              Remove @{unlinkCandidate.login} from this Entra profile? Agentweaver no longer has a shared fallback GitHub token, so unlinking immediately removes any repository or Copilot access that depends on this account.
            </Body>
            {unlinkWarnings.length > 0 && (
              <div className={styles.listBox}>
                {unlinkWarnings.map((warning) => (
                  <div key={warning} className={styles.listItem}>{warning}</div>
                ))}
              </div>
            )}
            <div className={styles.formActions}>
              <Button appearance="secondary" onClick={() => setUnlinkCandidate(null)}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={accountActionKey !== null}
                onClick={() => {
                  const login = unlinkCandidate.login;
                  setUnlinkCandidate(null);
                  void handleUnlink(login);
                }}
              >
                Confirm unlink
              </Button>
            </div>
          </div>
        </PageSection>
      )}

      <PageSection
        title="MCP clients"
        description="Connect an external MCP client to Agentweaver. This connection is associated with your signed-in account, not a single project."
      >
        <div className={styles.section}>
          <Field
            label="MCP server URL"
            hint="Use this URL in your MCP client configuration."
          >
            <Input value={MCP_URL} readOnly />
          </Field>
        </div>
      </PageSection>

      <PageSection
        title="Sandbox policy"
        description="View and update the sandbox policy for a repository. Enter the repository path to load its current policy."
      >
        <div className={styles.section}>
          <Field
            label="Repository path"
            hint="Use an absolute local path for the repository whose sandbox policy should be inspected."
          >
            <Input
              id="settings-repository-path"
              value={repositoryPath}
              placeholder="C:/path/to/repo"
              onChange={(_, data) => setRepositoryPath(data.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') void handleFetch(); }}
            />
          </Field>

          <div className={styles.formActions}>
            <Button
              appearance="primary"
              disabled={!repositoryPath.trim() || loading}
              onClick={() => void handleFetch()}
            >
              {loading ? 'Loading' : 'Load policy'}
            </Button>
            <Button
              appearance="secondary"
              disabled={loading && !repositoryPath}
              onClick={() => {
                setRepositoryPath('');
                setPolicy(null);
                setFetchError(null);
                setSaveError(null);
                setSaveSuccess(false);
              }}
            >
              Clear
            </Button>
            {loading && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>

          {fetchError && (
            <MessageBar intent="error">
              <MessageBarBody>{fetchError}</MessageBarBody>
            </MessageBar>
          )}
        </div>
      </PageSection>

      {policy && (
        <PageSection title="Policy settings">
          <div className={styles.section}>
            <Field label="Shell execution">
              <Switch
                label={policy.shell_enabled ? 'Enabled' : 'Disabled'}
                checked={policy.shell_enabled}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, shell_enabled: data.checked } : prev)
                }
              />
            </Field>

            <Field
              label="Sandbox enabled"
              hint="When off, commands run directly on the host with no isolation layer."
            >
              <Switch
                label={policy.direct ? 'Off — no isolation layer' : 'On — commands run in the sandbox'}
                checked={!policy.direct}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, direct: !data.checked } : prev)
                }
              />
            </Field>

            <Field
              label="Outbound network"
              hint={policy.direct ? 'Only applies when the sandbox is enabled.' : undefined}
            >
              <Switch
                label={policy.network_enabled ? 'Enabled' : 'Blocked'}
                checked={policy.network_enabled}
                disabled={policy.direct}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, network_enabled: data.checked } : prev)
                }
              />
            </Field>

            <Field label="Allowed repository roots">
              <div className={styles.listBox}>
                {policy.allowed_repository_roots.length === 0 ? (
                  <Label as="span" className={styles.emptyNote}>None configured</Label>
                ) : (
                  policy.allowed_repository_roots.map((root, i) => (
                    <div key={i} className={styles.listItem}>{root}</div>
                  ))
                )}
              </div>
            </Field>

            <Field label="Blocked command patterns">
              <div className={styles.listBox}>
                {policy.destructive_command_patterns.length === 0 ? (
                  <Label as="span" className={styles.emptyNote}>None configured</Label>
                ) : (
                  policy.destructive_command_patterns.map((pat, i) => (
                    <div key={i} className={styles.listItem}>{pat}</div>
                  ))
                )}
              </div>
            </Field>

            <div className={styles.formActions}>
              <Button
                appearance="primary"
                disabled={saving}
                onClick={() => void handleSave()}
              >
                {saving ? 'Saving' : 'Save'}
              </Button>
              <Button
                appearance="secondary"
                disabled={loading && !repositoryPath}
                onClick={() => {
                  setPolicy(null);
                  setFetchError(null);
                  setSaveError(null);
                  setSaveSuccess(false);
                }}
              >
                Clear loaded policy
              </Button>
            </div>

            {saveError && (
              <MessageBar intent="error">
                <MessageBarBody>{saveError}</MessageBarBody>
              </MessageBar>
            )}
            {saveSuccess && (
              <MessageBar intent="success">
                <MessageBarBody>Sandbox policy saved.</MessageBarBody>
              </MessageBar>
            )}
          </div>
        </PageSection>
      )}
    </PageContainer>
  );
}
