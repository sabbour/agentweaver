import { useEffect, useState } from 'react';
import {
  Button,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Radio,
  RadioGroup,
  Spinner,
  makeStyles,
  tokens,
  type RadioGroupOnChangeData,
} from '@fluentui/react-components';
import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import type { ByokProviderConfig, ByokProviderType } from '../api/types';
import { Body, PageContainer, PageHeader, PageSection } from '../components/ui';

type AiMode = 'copilot' | 'byok';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '480px',
  },
  formActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
});

const PROVIDER_LABELS: Record<ByokProviderType, string> = {
  openai: 'OpenAI-compatible',
  azure: 'Azure',
  anthropic: 'Anthropic',
};

export function PlatformSettingsPage() {
  const styles = useStyles();
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [existingConfig, setExistingConfig] = useState<ByokProviderConfig | null>(null);
  const [mode, setMode] = useState<AiMode>('copilot');

  const [providerType, setProviderType] = useState<ByokProviderType>('openai');
  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');

  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);

  useEffect(() => {
    let cancelled = false;
    apiClient.getByokProviderConfig()
      .then((config) => {
        if (cancelled) return;
        setExistingConfig(config);
        setMode(config ? 'byok' : 'copilot');
        if (config) {
          setProviderType(config.type);
          setBaseUrl(config.base_url);
          setModel(config.model);
        }
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setLoadError(formatApiErrorMessage(err));
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, []);

  const handleModeChange = (_: unknown, data: RadioGroupOnChangeData) => {
    const next = data.value as AiMode;
    setMode(next);
    setSaveError(null);
    setSaveSuccess(false);
  };

  const handleSaveByok = async () => {
    setSaving(true);
    setSaveError(null);
    setSaveSuccess(false);
    try {
      await apiClient.setByokProviderConfig({
        type: providerType,
        base_url: baseUrl,
        model,
        api_key: apiKey,
      });
      const refreshed = await apiClient.getByokProviderConfig();
      setExistingConfig(refreshed);
      setMode(refreshed ? 'byok' : 'copilot');
      setApiKey('');
      setSaveSuccess(true);
    } catch (err) {
      setSaveError(formatApiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const handleSwitchToCopilot = async () => {
    setSaving(true);
    setSaveError(null);
    setSaveSuccess(false);
    try {
      await apiClient.clearByokProviderConfig();
      setExistingConfig(null);
      setMode('copilot');
      setApiKey('');
      setSaveSuccess(true);
    } catch (err) {
      setSaveError(formatApiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <PageContainer width="readable">
      <PageHeader
        title="Platform settings"
        description="Deployment-wide configuration for Agentweaver."
      />
      <PageSection title="AI inference source">
        <Body tone="muted" style={{ marginBottom: tokens.spacingVerticalM }}>
          Choose exactly one AI source for the whole deployment. This is not per-project or
          per-person — it applies to everyone, including background and scheduled runs.
        </Body>
        {loading && <Spinner size="small" label="Loading configuration" />}
        {loadError && (
          <MessageBar intent="error"><MessageBarBody>{loadError}</MessageBarBody></MessageBar>
        )}
        {!loading && !loadError && (
          <div className={styles.form}>
            <RadioGroup value={mode} onChange={handleModeChange} disabled={saving}>
              <Radio value="copilot" label="GitHub Copilot mode — everyone connects their own Copilot login" />
              <Radio value="byok" label="Custom key mode — one shared key is used for everyone" />
            </RadioGroup>

            {mode === 'copilot' && (
              <>
                <Body tone="muted">
                  In this mode, every signed-in person connects their own GitHub Copilot login to
                  use AI features.
                </Body>
                {existingConfig && (
                  <div className={styles.formActions}>
                    <Button appearance="primary" disabled={saving} onClick={() => void handleSwitchToCopilot()}>
                      {saving ? 'Switching' : 'Switch to GitHub Copilot mode'}
                    </Button>
                    {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                )}
              </>
            )}

            {mode === 'byok' && (
              <>
                <Field label="Provider type" required>
                  <RadioGroup
                    value={providerType}
                    onChange={(_, data) => setProviderType(data.value as ByokProviderType)}
                    disabled={saving}
                  >
                    {(Object.keys(PROVIDER_LABELS) as ByokProviderType[]).map((type) => (
                      <Radio key={type} value={type} label={PROVIDER_LABELS[type]} />
                    ))}
                  </RadioGroup>
                </Field>
                <Field label="Base URL" required>
                  <Input
                    value={baseUrl}
                    onChange={(_, data) => setBaseUrl(data.value)}
                    placeholder="https://api.example.com"
                    disabled={saving}
                  />
                </Field>
                <Field label="Model" required>
                  <Input
                    value={model}
                    onChange={(_, data) => setModel(data.value)}
                    placeholder="gpt-4o"
                    disabled={saving}
                  />
                </Field>
                <Field label="API key" required>
                  <Input
                    type="password"
                    value={apiKey}
                    onChange={(_, data) => setApiKey(data.value)}
                    placeholder={existingConfig ? 'Re-enter to change the saved key' : undefined}
                    disabled={saving}
                  />
                </Field>
                <div className={styles.formActions}>
                  <Button
                    appearance="primary"
                    disabled={saving || !baseUrl || !model || !apiKey}
                    onClick={() => void handleSaveByok()}
                  >
                    {saving ? 'Saving' : 'Save custom key configuration'}
                  </Button>
                  {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
                {existingConfig && (
                  <Body tone="muted">
                    A custom key is already configured. Re-enter the API key above (the backend
                    never returns it) to change any field.
                  </Body>
                )}
              </>
            )}

            {saveError && (
              <MessageBar intent="error"><MessageBarBody>{saveError}</MessageBarBody></MessageBar>
            )}
            {saveSuccess && (
              <MessageBar intent="success"><MessageBarBody>Configuration saved.</MessageBarBody></MessageBar>
            )}
          </div>
        )}
      </PageSection>
    </PageContainer>
  );
}
