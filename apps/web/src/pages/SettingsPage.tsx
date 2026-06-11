import { useState } from 'react';
import {
  Button,
  Divider,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  Title2,
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type { SandboxPolicy } from '../api/types';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '640px',
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
      const updated = await apiClient.updateSandboxPolicy({
        repository_path: policy.repository_path,
        shell_enabled: policy.shell_enabled,
        direct: policy.direct,
        network_enabled: policy.network_enabled,
      });
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
    <div className={styles.root}>
      <Title2>Settings</Title2>

      <Divider />

      <div className={styles.section}>
        <Title3>Sandbox policy</Title3>
        <Text>
          View and update the sandbox policy for a repository. Enter the repository path to load its
          current policy.
        </Text>

        <Field label="Repository path">
          <Input
            value={repositoryPath}
            placeholder="C:/path/to/repo"
            onChange={(_, data) => setRepositoryPath(data.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') void handleFetch(); }}
          />
        </Field>

        <div className={styles.actions}>
          <Button
            appearance="secondary"
            disabled={!repositoryPath.trim() || loading}
            onClick={() => void handleFetch()}
          >
            {loading ? 'Loading' : 'Load policy'}
          </Button>
          {loading && <Spinner size="extra-tiny" aria-hidden="true" />}
        </div>

        {fetchError && (
          <MessageBar intent="error">
            <MessageBarBody>{fetchError}</MessageBarBody>
          </MessageBar>
        )}

        {policy && (
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

            <Field label="Direct execution (no sandbox isolation)">
              <Switch
                label={policy.direct ? 'On — commands run on host shell directly' : 'Off — uses bwrap/mxc isolation'}
                checked={policy.direct}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, direct: data.checked } : prev)
                }
              />
            </Field>

            <Field label="Outbound network">
              <Switch
                label={policy.network_enabled ? 'Enabled' : 'Blocked'}
                checked={policy.network_enabled}
                onChange={(_, data) =>
                  setPolicy((prev) => prev ? { ...prev, network_enabled: data.checked } : prev)
                }
              />
            </Field>

            <Field label="Allowed repository roots">
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
            </Field>

            <Field label="Blocked command patterns">
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
            </Field>

            <div className={styles.actions}>
              <Button
                appearance="primary"
                disabled={saving}
                onClick={() => void handleSave()}
              >
                {saving ? 'Saving' : 'Save'}
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
        )}
      </div>
    </div>
  );
}
