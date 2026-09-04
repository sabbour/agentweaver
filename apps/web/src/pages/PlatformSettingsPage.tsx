import { useEffect, useMemo, useState } from 'react';
import {
  Button,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Spinner,
  Textarea,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  AddRegular,
  CheckmarkCircleFilled,
  DeleteRegular,
  EditRegular,
  Eye24Regular,
  EyeOff24Regular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { formatApiErrorMessage } from '../api/errors';
import type {
  ByokProviderConfig,
  ByokProviderRequest,
  ByokProviderType,
  PlatformDefaultCopilotConnection,
} from '../api/types';
import {
  AppCard,
  AppDialog,
  Body,
  Label,
  PageContainer,
  PageHeader,
  PageSection,
  SetupReadiness,
} from '../components/ui';
import { useSearchParams } from 'react-router-dom';
import { markRequiredSetupPending } from '../components/onboarding/firstRunTourStorage';

const useStyles = makeStyles({
  form: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
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
  providerList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  providerCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  providerHeaderRow: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
  },
  providerLabel: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  activeBadge: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorPaletteGreenForeground1,
  },
  typeList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  typeOption: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'flex-start',
    gap: tokens.spacingVerticalXXS,
    padding: tokens.spacingVerticalM,
    textAlign: 'left',
    width: '100%',
    height: 'auto',
  },
});

// The three provider TYPES an admin can add. Foundry Local / Microsoft Foundry are
// intentionally NOT listed here — this deployment has no Microsoft-internal Foundry
// infrastructure to back them.
const ADDABLE_PROVIDER_TYPES: { type: ByokProviderType; label: string; description: string }[] = [
  {
    type: 'openai',
    label: 'Custom endpoint',
    description: 'Connect an OpenAI-compatible endpoint, including vLLM or OpenRouter.',
  },
  {
    type: 'azure',
    label: 'Azure OpenAI',
    description: 'Connect Azure OpenAI with its resource URL, API version, and deployment name.',
  },
  {
    type: 'anthropic',
    label: 'Anthropic',
    description: 'Connect hosted Claude models through the Messages API.',
  },
];

const PROVIDER_TYPE_LABELS: Record<ByokProviderType, string> = {
  openai: 'Custom endpoint',
  azure: 'Azure OpenAI',
  anthropic: 'Anthropic',
};

// Agentweaver's BYOK client only understands "openai" / "azure" / "anthropic" as opaque
// provider types routed through one generic HTTP client (see ByokProviderConfigurationService.cs
// and GitHub.Copilot.ProviderConfig). Wire API, custom headers, and the Azure API version all map
// onto real SDK fields (ProviderConfig.WireApi/Headers, AzureOptions.ApiVersion) — nothing here is
// invented UI without a backend home.
const DEFAULT_AZURE_API_VERSION = '2024-08-01-preview';
const DEFAULT_ANTHROPIC_BASE_URL = 'https://api.anthropic.com';

interface ProviderFormState {
  name: string;
  baseUrl: string;
  model: string;
  apiKey: string;
  wireApi: 'completions' | 'responses';
  headersText: string;
  azureApiVersion: string;
}

function blankFormState(type: ByokProviderType): ProviderFormState {
  return {
    name: '',
    baseUrl: type === 'anthropic' ? DEFAULT_ANTHROPIC_BASE_URL : '',
    model: '',
    apiKey: '',
    wireApi: 'responses',
    headersText: '',
    azureApiVersion: type === 'azure' ? DEFAULT_AZURE_API_VERSION : '',
  };
}

function formStateFromProvider(provider: ByokProviderConfig): ProviderFormState {
  return {
    name: provider.name,
    baseUrl: provider.base_url,
    model: provider.model,
    apiKey: '',
    wireApi: provider.wire_api ?? 'responses',
    headersText: provider.headers && Object.keys(provider.headers).length > 0
      ? JSON.stringify(provider.headers, null, 2)
      : '',
    azureApiVersion: provider.azure_api_version ?? (provider.type === 'azure' ? DEFAULT_AZURE_API_VERSION : ''),
  };
}

/** Parses the optional custom-headers JSON textarea. Returns `undefined` when blank, the parsed
 * flat string map on success, or throws a user-facing message on invalid input. */
function parseHeadersText(text: string): Record<string, string> | undefined {
  const trimmed = text.trim();
  if (!trimmed) return undefined;
  let parsed: unknown;
  try {
    parsed = JSON.parse(trimmed);
  } catch {
    throw new Error('Custom headers must be valid JSON. For example: {"X-Api-Version": "2024-01-01"}.');
  }
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw new Error('Custom headers must be a flat JSON object of string values.');
  }
  const entries = Object.entries(parsed as Record<string, unknown>);
  if (entries.some(([, value]) => typeof value !== 'string')) {
    throw new Error('Custom headers must be a flat JSON object of string values.');
  }
  return Object.fromEntries(entries as [string, string][]);
}

const PLATFORM_COPILOT_AUTH_RESULTS = {
  success: {
    intent: 'success',
    message: 'The platform-default GitHub Copilot account is connected.',
  },
  human_entra_subject_required: {
    intent: 'warning',
    message: 'Authorize GitHub Copilot while signed in with your work account.',
  },
  platform_admin_required: {
    intent: 'warning',
    message: 'Only a Platform Admin can connect the platform-default GitHub Copilot account.',
  },
  authorization_transaction_invalid: {
    intent: 'error',
    message: 'The GitHub Copilot connection failed. Start a new connection from Platform settings.',
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
  const [providers, setProviders] = useState<ByokProviderConfig[]>([]);
  const [activeProviderId, setActiveProviderId] = useState<string | null>(null);
  const [platformCopilotConnection, setPlatformCopilotConnection] = useState<PlatformDefaultCopilotConnection | null>(null);
  const [platformCopilotError, setPlatformCopilotError] = useState<string | null>(null);

  const [connectingCopilot, setConnectingCopilot] = useState(false);
  const [disconnectingCopilot, setDisconnectingCopilot] = useState(false);
  const [switchingActive, setSwitchingActive] = useState<string | 'copilot' | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const copilotAuthorizationResult = searchParams.get('copilot_app_auth');

  // Add/edit dialog state. `pickerOpen` shows the searchable type list; once a type is chosen
  // (for add) or an existing provider is being edited, `form` drives the type-specific fields.
  const [pickerOpen, setPickerOpen] = useState(false);
  const [typeQuery, setTypeQuery] = useState('');
  const [form, setForm] = useState<{ mode: 'add' | 'edit'; type: ByokProviderType; editingId?: string } | null>(null);
  const [formState, setFormState] = useState<ProviderFormState>(blankFormState('openai'));
  const [formError, setFormError] = useState<string | null>(null);
  const [showApiKey, setShowApiKey] = useState(false);
  const [saving, setSaving] = useState(false);

  const [removeTarget, setRemoveTarget] = useState<ByokProviderConfig | null>(null);
  const [removing, setRemoving] = useState(false);

  const dismissCopilotAuthorizationResult = () => {
    const next = new URLSearchParams(searchParams);
    next.delete('copilot_app_auth');
    setSearchParams(next, { replace: true });
  };

  const refreshProviders = async () => {
    const list = await apiClient.listByokProviders();
    setProviders(list.providers);
    setActiveProviderId(list.active_provider_id);
    return list;
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

  useEffect(() => {
    let cancelled = false;
    Promise.all([apiClient.listByokProviders(), apiClient.getPlatformDefaultCopilotConnection()])
      .then(([list, connection]) => {
        if (cancelled) return;
        setProviders(list.providers);
        setActiveProviderId(list.active_provider_id);
        setPlatformCopilotConnection(connection);
        setLoading(false);
      })
      .catch((err) => {
        if (cancelled) return;
        setLoadError(formatApiErrorMessage(err));
        setLoading(false);
      });
    return () => { cancelled = true; };
  }, []);

  const handleConnectPlatformCopilot = async () => {
    setConnectingCopilot(true);
    setPlatformCopilotError(null);
    try {
      const handoff = await apiClient.beginPlatformDefaultCopilotAuthorization();
      if (setupRequired) markRequiredSetupPending();
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
      if (!setupRequired) onRetryAccess?.();
    } catch (err) {
      setPlatformCopilotError(formatApiErrorMessage(err));
    } finally {
      setDisconnectingCopilot(false);
    }
  };

  const handleActivateCopilot = async () => {
    setSwitchingActive('copilot');
    setNotice(null);
    try {
      await apiClient.deactivateByokProviders();
      await refreshProviders();
      setNotice('GitHub Copilot is now the active AI inference source.');
      if (!setupRequired) onRetryAccess?.();
    } catch (err) {
      setLoadError(formatApiErrorMessage(err));
    } finally {
      setSwitchingActive(null);
    }
  };

  const handleActivateProvider = async (provider: ByokProviderConfig) => {
    setSwitchingActive(provider.id);
    setNotice(null);
    try {
      await apiClient.activateByokProvider(provider.id);
      await refreshProviders();
      setNotice(`"${provider.name}" is now the active AI inference source.`);
      if (!setupRequired) onRetryAccess?.();
    } catch (err) {
      setLoadError(formatApiErrorMessage(err));
    } finally {
      setSwitchingActive(null);
    }
  };

  const openAddPicker = () => {
    setTypeQuery('');
    setPickerOpen(true);
  };

  const chooseType = (type: ByokProviderType) => {
    setPickerOpen(false);
    setForm({ mode: 'add', type });
    setFormState(blankFormState(type));
    setFormError(null);
    setShowApiKey(false);
  };

  const openEdit = (provider: ByokProviderConfig) => {
    setForm({ mode: 'edit', type: provider.type, editingId: provider.id });
    setFormState(formStateFromProvider(provider));
    setFormError(null);
    setShowApiKey(false);
  };

  const closeForm = () => {
    setForm(null);
    setFormError(null);
  };

  const filteredTypes = useMemo(() => {
    const q = typeQuery.trim().toLowerCase();
    if (!q) return ADDABLE_PROVIDER_TYPES;
    return ADDABLE_PROVIDER_TYPES.filter(
      (t) => t.label.toLowerCase().includes(q) || t.description.toLowerCase().includes(q),
    );
  }, [typeQuery]);

  const formRequiredFieldsFilled = !!form && !!formState.name.trim() && !!formState.baseUrl.trim() && !!formState.model.trim()
    && (form.type === 'openai' || !!formState.apiKey.trim() || form.mode === 'edit');

  const handleSubmitForm = async () => {
    if (!form) return;
    setSaving(true);
    setFormError(null);
    try {
      let headers: Record<string, string> | undefined;
      try {
        headers = parseHeadersText(formState.headersText);
      } catch (err) {
        setFormError(err instanceof Error ? err.message : String(err));
        setSaving(false);
        return;
      }

      const request: ByokProviderRequest = {
        name: formState.name.trim(),
        type: form.type,
        base_url: formState.baseUrl.trim(),
        model: formState.model.trim(),
        api_key: formState.apiKey.trim() || null,
        wire_api: form.type === 'anthropic' ? null : formState.wireApi,
        headers: headers ?? null,
        azure_api_version: form.type === 'azure' ? (formState.azureApiVersion.trim() || null) : null,
      };

      if (form.mode === 'add') {
        await apiClient.addByokProvider(request);
        setNotice(`"${request.name}" was added. Use "Set active" to switch inference to it.`);
      } else if (form.editingId) {
        await apiClient.updateByokProvider(form.editingId, request);
        setNotice(`"${request.name}" was updated.`);
      }
      await refreshProviders();
      closeForm();
    } catch (err) {
      setFormError(formatApiErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  const handleConfirmRemove = async () => {
    if (!removeTarget) return;
    setRemoving(true);
    try {
      await apiClient.removeByokProvider(removeTarget.id);
      setNotice(`"${removeTarget.name}" was removed.`);
      setRemoveTarget(null);
      await refreshProviders();
      if (!setupRequired) onRetryAccess?.();
    } catch (err) {
      setLoadError(formatApiErrorMessage(err));
    } finally {
      setRemoving(false);
    }
  };

  const authorizationResult = isPlatformCopilotAuthorizationResultCode(copilotAuthorizationResult)
    ? PLATFORM_COPILOT_AUTH_RESULTS[copilotAuthorizationResult]
    : copilotAuthorizationResult
      ? {
        intent: 'error' as const,
        message: 'The GitHub Copilot connection failed. Start a new connection from Platform settings.',
      }
      : null;

  const copilotIsActive = activeProviderId === null;
  const activeCustomProvider = providers.find((provider) => provider.id === activeProviderId) ?? null;
  const modelProviderReady = activeCustomProvider !== null
    || (copilotIsActive && Boolean(platformCopilotConnection?.connected));
  const modelProviderDescription = activeCustomProvider
    ? `${activeCustomProvider.name} (${PROVIDER_TYPE_LABELS[activeCustomProvider.type]}) supplies AI access. Scope: Platform.`
    : platformCopilotConnection?.connected
      ? `GitHub Copilot (@${platformCopilotConnection.github_login ?? 'unknown'}) supplies AI access. Scope: Platform.`
      : 'Choose a model provider before this deployment starts AI work.';

  return (
    <PageContainer width="readable">
      <PageHeader
        title={setupRequired ? 'Set up Agentweaver' : 'Platform settings'}
        description={setupRequired
          ? 'Add model providers and choose one active provider for this deployment.'
          : 'Manage model providers for this Agentweaver deployment.'}
      />
      <SetupReadiness
        model={{
          title: setupRequired ? 'Connect a model provider' : 'Setup readiness',
          description: setupRequired
            ? 'The active provider supplies AI access for all users and projects.'
            : 'The active model provider applies to all users and projects.',
          loading,
          loadingLabel: 'Loading model provider status',
          error: loadError,
          items: [{
            id: 'model-provider',
            title: 'Model provider',
            description: modelProviderDescription,
            requirement: 'required',
            status: modelProviderReady ? 'ready' : 'action-required',
          }],
        }}
        onRetry={() => { window.location.reload(); }}
        primaryAction={!setupRequired && !modelProviderReady && !loading && !loadError ? (
          <Button
            appearance="primary"
            disabled={connectingCopilot}
            onClick={() => void handleConnectPlatformCopilot()}
          >
            {connectingCopilot ? 'Opening GitHub' : 'Authorize GitHub Copilot'}
          </Button>
        ) : setupRequired && modelProviderReady && onRetryAccess ? (
          <Button appearance="primary" onClick={onRetryAccess}>Continue to Agentweaver</Button>
        ) : undefined}
      />
      <PageSection
        title={setupRequired ? 'Choose a provider' : 'Model providers'}
        description={setupRequired
          ? 'Authorize GitHub Copilot, or add a provider and set it active.'
          : 'Choose one active provider. You can save other providers for later use.'}
        actions={(
          <Button
            appearance={setupRequired ? 'secondary' : 'primary'}
            icon={<AddRegular />}
            onClick={openAddPicker}
          >
            Add provider
          </Button>
        )}
      >
        {setupRequired && (
          <MessageBar intent="info">
            <MessageBarBody>
              Individual users can also configure their own provider or GitHub Copilot account
              later under Account settings → AI Access. Personal settings are used when no
              platform BYOK provider is active.
            </MessageBarBody>
          </MessageBar>
        )}
        {authorizationResult && (
          <MessageBar intent={authorizationResult.intent}>
            <MessageBarBody>{authorizationResult.message}</MessageBarBody>
            <Button appearance="subtle" size="small" onClick={dismissCopilotAuthorizationResult}>
              Dismiss
            </Button>
          </MessageBar>
        )}
        {!loading && !loadError && (
          <div className={styles.providerList}>
            {/* GitHub Copilot is always shown first, is never removable, and is implicitly
                active whenever no configured BYOK provider is marked active. */}
            <AppCard className={styles.providerCard}>
              <div className={styles.providerHeaderRow}>
                <div className={styles.providerLabel}>
                  <Label>GitHub Copilot</Label>
                  <Body tone="muted">Use your GitHub Copilot subscription for this deployment.</Body>
                </div>
                {copilotIsActive
                  ? (
                    <span className={styles.activeBadge}>
                      <CheckmarkCircleFilled aria-hidden="true" />
                      <Body>Active</Body>
                    </span>
                  )
                  : (
                    <Button
                      appearance="secondary"
                      disabled={switchingActive !== null}
                      onClick={() => void handleActivateCopilot()}
                    >
                      {switchingActive === 'copilot' ? 'Switching…' : 'Set active'}
                    </Button>
                  )}
              </div>
              {platformCopilotError && (
                <MessageBar intent="error">
                  <MessageBarBody>{platformCopilotError}</MessageBarBody>
                </MessageBar>
              )}
              {!platformCopilotError && platformCopilotConnection?.connected && (
                <MessageBar intent="success">
                  <MessageBarBody>
                    GitHub Copilot (@{platformCopilotConnection.github_login ?? 'unknown'}) is ready. Scope: Platform.
                  </MessageBarBody>
                </MessageBar>
              )}
              {!platformCopilotError && platformCopilotConnection && !platformCopilotConnection.connected && (
                <MessageBar intent="warning">
                  <MessageBarBody>Authorize GitHub Copilot to use it as the active model provider.</MessageBarBody>
                </MessageBar>
              )}
              <div className={styles.formActions}>
                <Button
                  appearance={platformCopilotConnection?.connected ? 'secondary' : 'primary'}
                  disabled={connectingCopilot || disconnectingCopilot}
                  onClick={() => void handleConnectPlatformCopilot()}
                >
                  {connectingCopilot
                    ? 'Opening GitHub'
                    : platformCopilotConnection?.connected
                      ? 'Switch GitHub Copilot account'
                      : 'Authorize GitHub Copilot'}
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

            {providers.map((provider) => (
              <AppCard key={provider.id} className={styles.providerCard}>
                <div className={styles.providerHeaderRow}>
                  <div className={styles.providerLabel}>
                    <Label>{provider.name}</Label>
                    <Body tone="muted">
                      {PROVIDER_TYPE_LABELS[provider.type]} · {provider.model}
                      {!provider.has_api_key && ' · No API key'}
                    </Body>
                  </div>
                  {provider.is_active
                    ? (
                      <span className={styles.activeBadge}>
                        <CheckmarkCircleFilled aria-hidden="true" />
                        <Body>Active</Body>
                      </span>
                    )
                    : (
                      <Button
                        appearance="secondary"
                        disabled={switchingActive !== null}
                        onClick={() => void handleActivateProvider(provider)}
                      >
                        {switchingActive === provider.id ? 'Switching…' : 'Set active'}
                      </Button>
                    )}
                </div>
                <div className={styles.formActions}>
                  <Button appearance="secondary" icon={<EditRegular />} onClick={() => openEdit(provider)}>
                    Edit
                  </Button>
                  <Button appearance="secondary" icon={<DeleteRegular />} onClick={() => setRemoveTarget(provider)}>
                    Remove
                  </Button>
                </div>
              </AppCard>
            ))}

            {notice && (
              <MessageBar intent="success">
                <MessageBarBody>{notice}</MessageBarBody>
              </MessageBar>
            )}
          </div>
        )}
      </PageSection>

      {/* "+ Add provider" picker: a searchable list of the fixed set of provider TYPES. */}
      <AppDialog
        open={pickerOpen}
        onOpenChange={setPickerOpen}
        title="Add provider"
        description="Choose a model provider type."
      >
        <div className={styles.form}>
          <Input
            placeholder="Search provider types"
            value={typeQuery}
            onChange={(_, data) => setTypeQuery(data.value)}
          />
          <div className={styles.typeList}>
            {filteredTypes.map((t) => (
              <Button
                key={t.type}
                appearance="secondary"
                className={styles.typeOption}
                onClick={() => chooseType(t.type)}
              >
                <Label>{t.label}</Label>
                <Body tone="muted">{t.description}</Body>
              </Button>
            ))}
            {filteredTypes.length === 0 && (
              <Body tone="muted">No provider type matches "{typeQuery}".</Body>
            )}
          </div>
        </div>
      </AppDialog>

      {/* Inline type-specific add/edit form. */}
      <AppDialog
        open={form !== null}
        onOpenChange={(open) => { if (!open) closeForm(); }}
        title={form ? `${form.mode === 'add' ? 'Add' : 'Edit'} ${PROVIDER_TYPE_LABELS[form.type]}` : undefined}
      >
        {form && (
          <div className={styles.form}>
            <Field label="Display name" required>
              <Input
                value={formState.name}
                onChange={(_, data) => setFormState((s) => ({ ...s, name: data.value }))}
                placeholder="My provider"
                disabled={saving}
              />
            </Field>
            <Field
              label="Base URL"
              required
              hint={form.type === 'openai'
                ? 'Include the OpenAI-compatible root. For example: "https://api.openai.com/v1".'
                : form.type === 'azure'
                  ? 'The bare Azure OpenAI resource endpoint, no path.'
                  : undefined}
            >
              <Input
                value={formState.baseUrl}
                onChange={(_, data) => setFormState((s) => ({ ...s, baseUrl: data.value }))}
                placeholder={form.type === 'openai'
                  ? 'https://api.example.com/v1'
                  : form.type === 'azure'
                    ? 'https://my-resource.openai.azure.com'
                    : 'https://api.anthropic.com'}
                disabled={saving}
              />
            </Field>
            {form.type === 'azure' && (
              <Field label="API version" required>
                <Input
                  value={formState.azureApiVersion}
                  onChange={(_, data) => setFormState((s) => ({ ...s, azureApiVersion: data.value }))}
                  placeholder={DEFAULT_AZURE_API_VERSION}
                  disabled={saving}
                />
              </Field>
            )}
            <Field label="Model" required hint={form.type === 'azure' ? 'The deployment name to use.' : undefined}>
              <Input
                value={formState.model}
                onChange={(_, data) => setFormState((s) => ({ ...s, model: data.value }))}
                placeholder="gpt-4o"
                disabled={saving}
              />
            </Field>
            {form.type !== 'anthropic' && (
              <Field label="Wire API">
                <Dropdown
                  value={formState.wireApi === 'completions' ? 'Completions' : 'Responses'}
                  selectedOptions={[formState.wireApi]}
                  disabled={saving}
                  onOptionSelect={(_, data) => {
                    if (data.optionValue) {
                      setFormState((s) => ({ ...s, wireApi: data.optionValue as 'completions' | 'responses' }));
                    }
                  }}
                >
                  <Option value="responses">Responses</Option>
                  <Option value="completions">Completions</Option>
                </Dropdown>
              </Field>
            )}
            <Field
              label="API key"
              required={form.type !== 'openai'}
              hint={form.type === 'openai'
                ? 'Optional. Leave this field blank if your endpoint does not require authentication.'
                : undefined}
            >
              <Input
                type={showApiKey ? 'text' : 'password'}
                value={formState.apiKey}
                onChange={(_, data) => setFormState((s) => ({ ...s, apiKey: data.value }))}
                placeholder={form.mode === 'edit' ? 'Leave blank to keep the saved key' : undefined}
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
            <Field label="Custom headers" hint="Optional JSON object of extra HTTP headers to send with every request.">
              <Textarea
                value={formState.headersText}
                onChange={(_, data) => setFormState((s) => ({ ...s, headersText: data.value }))}
                placeholder={'{"X-Api-Version": "2024-01-01"}'}
                disabled={saving}
                resize="vertical"
              />
            </Field>
            {formError && (
              <MessageBar intent="error"><MessageBarBody>{formError}</MessageBarBody></MessageBar>
            )}
            <div className={styles.formActions}>
              <Button appearance="secondary" disabled={saving} onClick={closeForm}>Cancel</Button>
              <Button
                appearance="primary"
                disabled={saving || !formRequiredFieldsFilled}
                onClick={() => void handleSubmitForm()}
              >
                {saving ? 'Saving…' : form.mode === 'add' ? 'Add provider' : 'Save changes'}
              </Button>
              {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
            </div>
          </div>
        )}
      </AppDialog>

      {/* Remove confirmation — never a silent delete. */}
      <AppDialog
        open={removeTarget !== null}
        onOpenChange={(open) => { if (!open) setRemoveTarget(null); }}
        title="Remove provider?"
        description={removeTarget
          ? `This deletes the saved configuration and API key for "${removeTarget.name}". `
            + (removeTarget.is_active ? 'It is currently active — removing it switches the deployment back to GitHub Copilot.' : '')
          : undefined}
        primaryAction={{
          label: removing ? 'Removing…' : 'Remove provider',
          onClick: () => void handleConfirmRemove(),
          loading: removing,
        }}
      />
    </PageContainer>
  );
}
