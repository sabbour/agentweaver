import { apiClient } from '../api/apiClient';
import { MCP_URL } from '../config';
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { useEffect, useMemo, useState } from 'react';
import type { AuthSessionResponse } from '../api/types';
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
  emptyNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
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
  const [session, setSession] = useState<AuthSessionResponse | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const [authError, setAuthError] = useState<string | null>(null);
  const [entraAdminUrl, setEntraAdminUrl] = useState<string | null>(null);
  const [repoAppConnecting, setRepoAppConnecting] = useState(false);
  const [repoAppError, setRepoAppError] = useState<string | null>(null);

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
    void apiClient.getAuthConfig()
      .then(({ entra }) => {
        if (!entra.tenant_id || !entra.client_id || cancelled) return;
        setEntraAdminUrl(
          `https://entra.microsoft.com/${encodeURIComponent(entra.tenant_id)}/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/AppRoles/appId/${encodeURIComponent(entra.client_id)}/isMSAApp~/false`,
        );
      })
      .catch(() => {
        // The role list remains useful if the public Entra configuration is unavailable.
      });

    return () => { cancelled = true; };
  }, []);

  const platformRoles = useMemo(
    () => (session?.platform_roles ?? []).filter((role, index, all) => all.indexOf(role) === index),
    [session?.platform_roles],
  );

  const connectRepoApp = async () => {
    setRepoAppConnecting(true);
    setRepoAppError(null);
    try {
      const handoff = await apiClient.beginRepoAppAuthorization();
      window.location.assign(handoff.authorization_url);
    } catch (err) {
      setRepoAppError(formatError(err));
      setRepoAppConnecting(false);
    }
  };

  return (
    <PageContainer width="readable">
      <PageHeader
        title="Account settings"
        description="Review authentication and MCP access."
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
                {entraAdminUrl && (
                  <Button as="a" href={entraAdminUrl} target="_blank" rel="noreferrer" appearance="subtle">
                    Manage in Microsoft Entra ID
                  </Button>
                )}
              </div>
            </>
          )}
        </div>
      </PageSection>

      <PageSection
        title="GitHub connections"
        description="GitHub Copilot provides AI access. The separate Repo App provides repository access."
      >
        <div className={styles.section}>
          <div className={styles.subBlock}>
            <TitleText>GitHub Copilot App</TitleText>
            <Body tone="muted">
              Copilot connections are selected per project, so the account used for AI can match that project’s needs.
            </Body>
            <Button appearance="secondary" onClick={() => window.location.assign('/projects')}>
              Manage Copilot connections in projects
            </Button>
          </div>
          <div className={styles.subBlock}>
            <TitleText>GitHub Repo App</TitleText>
            <Body tone="muted">
              Connect your GitHub account to browse, create, and manage repositories in projects.
            </Body>
            <div className={styles.formActions}>
              <Button appearance="primary" disabled={repoAppConnecting} onClick={() => void connectRepoApp()}>
                {repoAppConnecting ? 'Opening GitHub…' : 'Connect GitHub Repo App'}
              </Button>
            </div>
            {repoAppError && (
              <MessageBar intent="error"><MessageBarBody>{repoAppError}</MessageBarBody></MessageBar>
            )}
          </div>
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
    </PageContainer>
  );
}
