import { apiClient } from '../api/apiClient';
import { MCP_URL } from '../config';
import { ApiError } from '../api/client';
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { Copy24Regular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { SandboxPolicy } from '../api/types';
import {
  Label,
  PageContainer,
  PageHeader,
  PageSection,
} from '../components/ui';

const useStyles = makeStyles({
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '640px',
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
  helperText: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  formActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  codeBlock: {
    fontFamily: tokens.fontFamilyMonospace,
  },
});

export function SettingsPage() {
  const styles = useStyles();
  const [repositoryPath, setRepositoryPath] = useState('');
  const [policy, setPolicy] = useState<SandboxPolicy | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [fetchError, setFetchError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);
  const [copiedConfig, setCopiedConfig] = useState<string | null>(null);

  const clientConfigs = [
    {
      id: 'claude-desktop',
      label: 'Claude Desktop (claude_desktop_config.json)',
      value: JSON.stringify({
        mcpServers: {
          agentweaver: {
            url: MCP_URL,
            headers: { Authorization: 'Bearer ${AGENTWEAVER_TOKEN}' },
          },
        },
      }, null, 2),
    },
    {
      id: 'vs-code',
      label: 'VS Code (mcp.json)',
      value: JSON.stringify({
        servers: {
          agentweaver: {
            type: 'http',
            url: MCP_URL,
            headers: { Authorization: 'Bearer ${input:agentweaver-token}' },
          },
        },
        inputs: [{
          id: 'agentweaver-token',
          type: 'promptString',
          description: 'Your existing Agentweaver/GitHub bearer token',
          password: true,
        }],
      }, null, 2),
    },
    {
      id: 'copilot-cli',
      label: 'GitHub Copilot CLI (mcp.json)',
      value: JSON.stringify({
        mcpServers: {
          agentweaver: {
            type: 'http',
            url: MCP_URL,
            headers: { Authorization: 'Bearer ${AGENTWEAVER_TOKEN}' },
          },
        },
      }, null, 2),
    },
  ];

  const copyConfig = async (id: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopiedConfig(id);
    } catch {
      setCopiedConfig(null);
    }
  };

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
      setFetchError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
      );
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
      // Round-trip the FULL policy so omitted fields are never dropped.
      const updated = await apiClient.updateSandboxPolicy(policy);
      setPolicy(updated);
      setSaveSuccess(true);
    } catch (err) {
      setSaveError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
      );
    } finally {
      setSaving(false);
    }
  };

  return (
    <PageContainer width="readable">
      <PageHeader
        title="Settings"
        description="System-level configuration for local repository policy."
      />

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

          <Label as="p" className={styles.helperText}>
            Agentweaver does not display your bearer token. Use the existing token from your
            signed-in Agentweaver/GitHub session, or let a client that supports MCP OAuth sign in
            interactively. For a manual configuration, set <code>AGENTWEAVER_TOKEN</code> in your
            client environment before starting it.
          </Label>

          {clientConfigs.map((config) => (
            <Field key={config.id} label={config.label}>
              <Textarea
                aria-label={config.label}
                className={styles.codeBlock}
                value={config.value}
                readOnly
                resize="vertical"
                rows={config.value.split('\n').length + 1}
              />
              <Button
                appearance="secondary"
                icon={<Copy24Regular />}
                onClick={() => void copyConfig(config.id, config.value)}
              >
                {copiedConfig === config.id ? 'Copied' : 'Copy config'}
              </Button>
            </Field>
          ))}
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
                    /* SECURITY (Y-3): root rendered as text — no HTML */
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
                    /* SECURITY (Y-3): pattern rendered as text — no HTML */
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
                disabled={saving || loading}
                onClick={() => void handleFetch()}
              >
                Discard changes
              </Button>
              {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>

            {saveError && (
              <MessageBar intent="error">
                <MessageBarBody>{saveError}</MessageBarBody>
              </MessageBar>
            )}
            {saveSuccess && (
              <MessageBar intent="success">
                <MessageBarBody>Policy saved.</MessageBarBody>
              </MessageBar>
            )}
          </div>
        </PageSection>
      )}
    </PageContainer>
  );
}