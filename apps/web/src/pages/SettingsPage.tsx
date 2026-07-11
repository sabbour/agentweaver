import {
  apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { BladeHeader,
FormFieldRow,
FormFooter,
Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  } from '../copilot-fluent-system';
import { PageHeader } from '../components/PageHeader';
import { makeStyles,
  tokens,
} from '../copilot-fluent-system';
import { Settings24Regular, Shield24Regular } from '../copilot-fluent-system';
import { useState } from 'react';
import type { SandboxPolicy } from '../api/types';
const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '900px',
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
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
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  formSurface: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
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
    <div className={['azf-stack azf-page azf-pattern-shell', styles.root].filter(Boolean).join(' ')}>
      <PageHeader
        title="Settings"
        subtitle="System-level configuration for local repository policy."
        resourceIcon={<Settings24Regular />}
      />

      <div className={['azf-surface azf-surface--panel azf-surface--padding-comfortable', styles.formSurface].filter(Boolean).join(' ')}>
        <BladeHeader
          size="compact"
          title="Sandbox policy"
          subtitle="View and update the sandbox policy for a repository. Enter the repository path to load its current policy."
          resourceIcon={<Shield24Regular />}
        />

        <FormFieldRow label="Repository path" htmlFor="settings-repository-path" hint="Use an absolute local path for the repository whose sandbox policy should be inspected.">
          <Input
            id="settings-repository-path"
            value={repositoryPath}
            placeholder="C:/path/to/repo"
            onChange={(_, data) => setRepositoryPath(data.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void handleFetch(); }}
          />
        </FormFieldRow>

        <FormFooter
          primaryAction={{
            id: 'load-policy',
            label: loading ? 'Loading' : 'Load policy',
            disabled: !repositoryPath.trim() || loading,
            loading,
            onClick: () => void handleFetch(),
          }}
          secondaryAction={{
            id: 'clear-policy',
            label: 'Clear',
            disabled: loading && !repositoryPath,
            onClick: () => {
              setRepositoryPath('');
              setPolicy(null);
              setFetchError(null);
              setSaveError(null);
              setSaveSuccess(false);
            },
          }}
          feedback={loading ? <Spinner size="extra-tiny" aria-hidden="true" /> : undefined}
        />

        {fetchError && (
          <MessageBar intent="error">
            <MessageBarBody>{fetchError}</MessageBarBody>
          </MessageBar>
        )}

        {policy && (
          <div className={['azf-surface azf-surface--subtle azf-surface--padding-comfortable', styles.section].filter(Boolean).join(' ')}>
            <FormFieldRow label="Shell execution">
              <Switch
                label={policy.shell_enabled ? 'Enabled' : 'Disabled'}
                checked={policy.shell_enabled}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, shell_enabled: data.checked } : prev)
                }
              />
            </FormFieldRow>

            <FormFieldRow
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
            </FormFieldRow>

            <FormFieldRow
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
            </FormFieldRow>

            <FormFieldRow label="Allowed repository roots">
              <div className={styles.listBox}>
                {policy.allowed_repository_roots.length === 0 ? (
                  <Text className={styles.emptyNote}>None configured</Text>
                ) : (
                  policy.allowed_repository_roots.map((root, i) => (
                    /* SECURITY (Y-3): root rendered as text — no HTML */
                    <div key={i} className={styles.listItem}>{root}</div>
                  ))
                )}
              </div>
            </FormFieldRow>

            <FormFieldRow label="Blocked command patterns">
              <div className={styles.listBox}>
                {policy.destructive_command_patterns.length === 0 ? (
                  <Text className={styles.emptyNote}>None configured</Text>
                ) : (
                  policy.destructive_command_patterns.map((pat, i) => (
                    /* SECURITY (Y-3): pattern rendered as text — no HTML */
                    <div key={i} className={styles.listItem}>{pat}</div>
                  ))
                )}
              </div>
            </FormFieldRow>

            <FormFooter
              primaryAction={{
                id: 'save-policy',
                label: saving ? 'Saving' : 'Save',
                disabled: saving,
                loading: saving,
                onClick: () => void handleSave(),
              }}
              secondaryAction={{
                id: 'discard-policy',
                label: 'Discard changes',
                disabled: saving || loading,
                onClick: () => void handleFetch(),
              }}
              feedback={saving ? <Spinner size="extra-tiny" aria-hidden="true" /> : undefined}
            />

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
        )}
      </div>
    </div>
  );
}
