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
import { Eye24Regular, EyeOff24Regular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import type {
  ByokProviderConfig,
  ByokProviderType,
  PlatformDefaultCopilotConnection,
} from '../api/types';
import { AppCard, Body, Label, PageContainer, PageHeader, PageSection } from '../components/ui';
import { useSearchParams } from 'react-router-dom';

type AiMode = 'copilot' | 'byok';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '640px',
  },
  formActions: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  stack: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  connectionCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  connectionLabel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
});

const PROVIDER_LABELS: Record<ByokProviderType, string> = {
  azure: 'Azure',
  openai: 'OpenAI-compatible',
  anthropic: 'Anthropic',
};

const BASE_URL_HINTS: Record<ByokProviderType, string> = {
  azure: 'Bare Azure OpenAI resource endpoint, no path — e.g. https://<resource>.openai.azure.com. '
    + 'For a Foundry project or OpenAI-compatible endpoint (with a path), use "OpenAI-compatible" instead.',
  openai: 'Any full OpenAI-compatible endpoint, e.g. https://<resource>.openai.azure.com/openai/v1 or '
    + 'https://<resource>.services.ai.azure.com/api/projects/<project>',
  anthropic: 'e.g. https://api.anthropic.com',
};

const BASE_URL_PLACEHOLDERS: Record<ByokProviderType, string> = {
  azure: 'https://<resource>.openai.azure.com',
  openai: 'https://api.example.com',
  anthropic: 'https://api.anthropic.com',
};

const PLATFORM_COPILOT_AUTH_RESULTS = {
  success: {
    intent: 'success',
    message: 'The platform-default GitHub Copilot account is connected.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    message: 'Connect GitHub Copilot while signed in with your work account.',
  },
  platform_admin_required: {
    intent: 'warning',
    message: 'Only a Platform Admin can connect the platform-default GitHub Copilot account.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    message: 'The GitHub Copilot connection could not be completed. Start a new connection from Platform settings.',
  },
  authorization_transaction_consumed: {
    intent: 'error',
    message: 'This GitHub Copilot connection has already been used. Start a new connection from Platform settings.',
  },
  github_binding_unavailable: {
    intent: 'error',
    message: 'The GitHub Copilot connection is currently unavailable. Try again later.',
  },
} as const;

type PlatformCopilotAuthorizationResultCode = keyof typeof PLATFORM_COPILOT_AUTH_RESULTS;

function isPlatformCopilotAuthorizationResultCode(
  value: string | null,
): value is PlatformCopilotAuthorizationResultCode {
  return value !== null && Object.hasOwn(PLATFORM_COPILOT_AUTH_RESULTS, value);
}

export function PlatformSettingsPage({
  setupRequired = false,
  onRetryAccess,
}: {
  setupRequired?: boolean;
  onRetryAccess?: () => void;
}) {
  const styles = useStyles();
  const [searchParams, setSearchParams] = useSearchParams();
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [existingConfig, setExistingConfig] = useState<ByokProviderConfig | null>(null);
  const [mode, setMode] = useState<AiMode>('copilot');
  const [platformCopilotConnection, setPlatformCopilotConnection] = useState<PlatformDefaultCopilotConnection | null>(null);
  const [platformCopilotError, setPlatformCopilotError] = useState<string | null>(null);

  const [providerType, setProviderType] = useState<ByokProviderType>('azure');
  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');
  const [showApiKey, setShowApiKey] = useState(false);

  const [saving, setSaving] = useState(false);
  const [connectingCopilot, setConnectingCopilot] = useState(false);
  const [disconnectingCopilot, setDisconnectingCopilot] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saveSuccess, setSaveSuccess] = useState(false);
  const copilotAuthorizationResult = searchParams.get('copilot_app_auth');

  const dismissCopilotAuthorizationResult = () => {
    const next = new URLSearchParams(searchParams);
    next.delete('copilot_app_auth');
    setSearchParams(next, { replace: true });
  };

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

  useEffect(() => {
    let cancelled = false;
    if (loading || mode !== 'copilot') return () => { cancelled = true; };

    apiClient.getPlatformDefaultCopilotConnection()
      .then((connection) => {
        if (cancelled) return;
        setPlatformCopilotConnection(connection);
        setPlatformCopilotError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        setPlatformCopilotConnection(null);
        setPlatformCopilotError(formatApiErrorMessage(err));
      });

    return () => { cancelled = true; };
  }, [loading, mode]);

  useEffect(() => {
    if (!setupRequired || !onRetryAccess) return;
    if (existingConfig || platformCopilotConnection?.connected) onRetryAccess();
  }, [existingConfig, onRetryAccess, platformCopilotConnection?.connected, setupRequired]);

  const handleModeChange = (_: unknown, data: RadioGroupOnChangeData) => {
    const next = data.value as AiMode;
    setMode(next);
    setShowApiKey(false);
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
      setShowApiKey(false);
      setSaveSuccess(true);
      onRetryAccess?.();
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
      setShowApiKey(false);
      setSaveSuccess(true);
      onRetryAccess?.();
    } catch (err) {
      setSaveError(formatApiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const handleDisconnectByok = async () => {
    setSaving(true);
    setSaveError(null);
    setSaveSuccess(false);
    try {
      await apiClient.clearByokProviderConfig();
      setExistingConfig(null);
      setMode('copilot');
      setProviderType('azure');
      setBaseUrl('');
      setModel('');
      setApiKey('');
      setShowApiKey(false);
      setSaveSuccess(true);
    } catch (err) {
      setSaveError(formatApiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const refreshPlatformCopilotConnection = async () => {
    try {
      const connection = await apiClient.getPlatformDefaultCopilotConnection();
      setPlatformCopilotConnection(connection);
      setPlatformCopilotError(null);
    } catch (err) {
      setPlatformCopilotConnection(null);
      setPlatformCopilotError(formatApiErrorMessage(err));
    }
  };

  const handleConnectPlatformCopilot = async () => {
    setConnectingCopilot(true);
    setPlatformCopilotError(null);
    try {
      const handoff = await apiClient.beginPlatformDefaultCopilotAuthorization();
      window.location.assign(handoff.authorization_url);
    } catch (err) {
      setPlatformCopilotError(formatApiErrorMessage(err));
      setConnectingCopilot(false);
    }
  };

  const handleDisconnectPlatformCopilot = async () => {
    setDisconnectingCopilot(true);
    setPlatformCopilotError(null);
    try {
      await apiClient.disconnectPlatformDefaultCopilotConnection();
      setPlatformCopilotConnection({ connected: false, github_login: null });
      await refreshPlatformCopilotConnection();
      setSaveSuccess(true);
      onRetryAccess?.();
    } catch (err) {
      setPlatformCopilotError(formatApiErrorMessage(err));
    } finally {
      setDisconnectingCopilot(false);
    }
  };

  const authorizationResult = isPlatformCopilotAuthorizationResultCode(copilotAuthorizationResult)
    ? PLATFORM_COPILOT_AUTH_RESULTS[copilotAuthorizationResult]
    : copilotAuthorizationResult
      ? {
        intent: 'error' as const,
        message: 'The GitHub Copilot connection could not be completed. Start a new connection from Platform settings.',
      }
      : null;

  return (
    <PageContainer width="readable">
      <PageHeader
        title="Platform settings"
        description="Deployment-wide configuration for Agentweaver."
      />
      {setupRequired && (
        <div className={styles.stack}>
          <MessageBar intent="warning">
            <MessageBarBody>
              Agentweaver is locked until an administrator configures either a deployment-wide custom key
              or a platform-default GitHub Copilot account.
            </MessageBarBody>
          </MessageBar>
          {onRetryAccess && (
            <div className={styles.formActions}>
              <Button appearance="secondary" onClick={onRetryAccess}>Retry access</Button>
            </div>
          )}
        </div>
      )}
      <PageSection
        title="AI inference source"
        description="Choose exactly one AI source for the whole deployment. This is not per-project or
          per-person — it applies to everyone, including background and scheduled runs."
      >
        {authorizationResult && (
          <MessageBar intent={authorizationResult.intent}>
            <MessageBarBody>{authorizationResult.message}</MessageBarBody>
            <Button appearance="subtle" size="small" onClick={dismissCopilotAuthorizationResult}>
              Dismiss
            </Button>
          </MessageBar>
        )}
        {loading && <Spinner size="small" label="Loading configuration" />}
        {loadError && (
          <MessageBar intent="error"><MessageBarBody>{loadError}</MessageBarBody></MessageBar>
        )}
        {!loading && !loadError && (
          <div className={styles.form}>
            <RadioGroup value={mode} onChange={handleModeChange} disabled={saving}>
              <Radio value="copilot" label="GitHub Copilot mode — one platform-default Copilot account for unattended work" />
              <Radio value="byok" label="Custom key mode — one shared key is used for everyone" />
            </RadioGroup>

            {mode === 'copilot' && (
              <>
                <Body tone="muted">
                  In this mode, a Platform Admin connects one deployment-wide GitHub Copilot account
                  for unattended and background work. Project-scoped Copilot connections remain
                  separate and can still be managed inside individual project settings.
                </Body>
                <AppCard className={styles.connectionCard}>
                  <div className={styles.connectionLabel}>
                    <Label>Platform-default GitHub Copilot account</Label>
                    <Body tone="muted">
                      Used for deployment-wide GitHub Copilot mode when no BYOK provider is configured.
                    </Body>
                  </div>
                  {platformCopilotError && (
                    <MessageBar intent="error">
                      <MessageBarBody>{platformCopilotError}</MessageBarBody>
                    </MessageBar>
                  )}
                  {!platformCopilotError && platformCopilotConnection?.connected && (
                    <MessageBar intent="success">
                      <MessageBarBody>
                        Connected GitHub login: @{platformCopilotConnection.github_login ?? 'unknown'}
                      </MessageBarBody>
                    </MessageBar>
                  )}
                  {!platformCopilotError && platformCopilotConnection && !platformCopilotConnection.connected && (
                    <MessageBar intent="warning">
                      <MessageBarBody>No platform-default GitHub Copilot account is connected yet.</MessageBarBody>
                    </MessageBar>
                  )}
                  <div className={styles.formActions}>
                    <Button
                      appearance={platformCopilotConnection?.connected ? 'secondary' : 'primary'}
                      disabled={connectingCopilot || disconnectingCopilot}
                      onClick={() => void handleConnectPlatformCopilot()}
                    >
                      {connectingCopilot
                        ? 'Opening GitHub…'
                        : platformCopilotConnection?.connected
                          ? 'Switch GitHub Copilot account'
                          : 'Connect GitHub Copilot'}
                    </Button>
                    <Button
                      appearance="secondary"
                      disabled={connectingCopilot || disconnectingCopilot}
                      onClick={() => void refreshPlatformCopilotConnection()}
                    >
                      Refresh status
                    </Button>
                    {platformCopilotConnection?.connected && (
                      <Button
                        appearance="secondary"
                        disabled={connectingCopilot || disconnectingCopilot}
                        onClick={() => void handleDisconnectPlatformCopilot()}
                      >
                        {disconnectingCopilot ? 'Disconnecting' : 'Disconnect'}
                      </Button>
                    )}
                    {(connectingCopilot || disconnectingCopilot) && (
                      <Spinner size="extra-tiny" aria-hidden="true" />
                    )}
                  </div>
                </AppCard>
                <div className={styles.formActions}>
                  <Button appearance="primary" disabled={saving} onClick={() => void handleSwitchToCopilot()}>
                    {saving ? 'Saving' : 'Save AI inference source'}
                  </Button>
                  {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
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
                <Field label="Base URL" required hint={BASE_URL_HINTS[providerType]}>
                  <Input
                    value={baseUrl}
                    onChange={(_, data) => setBaseUrl(data.value)}
                    placeholder={BASE_URL_PLACEHOLDERS[providerType]}
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
                    type={showApiKey ? 'text' : 'password'}
                    value={apiKey}
                    onChange={(_, data) => setApiKey(data.value)}
                    placeholder={existingConfig ? 'Re-enter to change the saved key' : undefined}
                    disabled={saving}
                    contentAfter={(
                      <Button
                        appearance="transparent"
                        aria-label={showApiKey ? 'Hide API key' : 'Show API key'}
                        icon={showApiKey ? <EyeOff24Regular /> : <Eye24Regular />}
                        size="small"
                        disabled={saving}
                        onClick={() => setShowApiKey((current) => !current)}
                      />
                    )}
                  />
                </Field>
                <div className={styles.formActions}>
                  <Button
                    appearance="primary"
                    disabled={saving || !baseUrl || !model || !apiKey}
                    onClick={() => void handleSaveByok()}
                  >
                    {saving ? 'Saving' : 'Save AI inference source'}
                  </Button>
                  {existingConfig && (
                    <Button
                      appearance="secondary"
                      disabled={saving}
                      onClick={() => void handleDisconnectByok()}
                    >
                      Disconnect
                    </Button>
                  )}
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
