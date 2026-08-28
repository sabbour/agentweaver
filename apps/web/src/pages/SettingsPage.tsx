import { apiClient } from '../api/apiClient';
import { MCP_URL } from '../config';
import {
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
import { useEffect, useMemo, useState } from 'react';
import type { AuthSessionResponse, SandboxPolicy } from '../api/types';
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
});

function formatError(err: unknown): string {
  return err instanceof Error
      ? err.message
      : String(err);
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

  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authError, setAuthError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void apiClient.getAuthSession()
      .then((authSession) => {
        if (cancelled) return;
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

  const platformRoles = useMemo(
    () => (session?.platform_roles ?? []).filter((role, index, all) => all.indexOf(role) === index),
    [session?.platform_roles],
  );

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
        description="Review authentication, MCP access, and local repository sandbox policy."
      />

      <PageSection
        title="Authentication"
        description="Review how this deployment authenticates you and which Entra platform roles are currently assigned."
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
                  { label: 'Authentication mode', value: 'Entra ID' },
                  { label: 'Signed in as', value: session?.display_name ?? session?.login ?? 'Current user' },
                  {
                    label: 'Platform roles',
                    value: platformRoles.length > 0 ? platformRoles.join(', ') : 'No app roles assigned',
                  },
                ]}
              />

              <div className={styles.subBlock}>
                <TitleText>Platform access</TitleText>
                <Body tone="muted">
                  Platform roles are assigned in Microsoft Entra ID. Agentweaver shows them here,
                  but cannot grant or revoke them from this screen.
                </Body>
                <div className={styles.badgeRow}>
                  {platformRoles.length > 0 ? (
                    platformRoles.map((role) => (
                      <Label key={role} as="span">{role}</Label>
                    ))
                  ) : (
                    <Label as="span" className={styles.emptyNote}>No Entra app roles are currently assigned.</Label>
                  )}
                </div>
              </div>
            </>
          )}
        </div>
      </PageSection>

      <PageSection
        title="MCP clients"
        description="Connect an external MCP client to Agentweaver. This connection is associated with your signed-in account, not a single project."
      >
        <div className={styles.section}>
          <Field
            label="MCP server URL"
            hint="Use this URL in your MCP client configuration."
          >
            <Input value={MCP_URL} readOnly data-testid="mcp-server-url" />
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
