import {
  Badge,
  Button,
  Checkbox,
  Dropdown,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Option,
  Radio,
  RadioGroup,
  Spinner,
  Text,
} from '@fluentui/react-components';
import { AddRegular, EditRegular, Eye24Regular, EyeOff24Regular } from '@fluentui/react-icons';
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type FormEvent,
} from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type {
  ModelProviderCredentialResponse,
  PlatformModelProviderDraft,
  ProjectModelProviderDraftSaveRequest,
} from './contracts';
import { errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';

type ProviderType = 'openai' | 'azure' | 'anthropic';
type PricingChoice = '' | 'manual' | 'saved' | 'clear';

interface Editor {
  name: string;
  type: ProviderType;
  baseUrl: string;
  model: string;
  wireApi: 'responses' | 'completions';
  azureApiVersion: string;
  reasoningEfforts: string;
  promptCapacityTokens: string;
  apiKey: string;
  pricingChoice: PricingChoice;
  rateCardId: string;
  rateCardVersion: string;
  inputRate: string;
  outputRate: string;
  priceSourceUrl: string;
  clearConfirmed: boolean;
}

interface AccessSnapshot {
  session: NonNullable<ReturnType<typeof useAuth>['session']>;
  sessionEpoch: number;
  sessionGeneration: number;
  unboundSessionGeneration: number;
  tenant: string;
}

interface PendingCredential {
  providerId: string;
  operationId: string;
  providerRevision: number;
  expectedCredentialRevision: number;
  status: 'publication_pending' | 'quarantined';
}

const PROVIDER_TYPES: Array<{ type: ProviderType; label: string; description: string }> = [
  { type: 'openai', label: 'Custom endpoint', description: 'Use an OpenAI-compatible endpoint, including vLLM or OpenRouter.' },
  { type: 'azure', label: 'Azure OpenAI', description: 'Use an Azure OpenAI resource, API version, and deployment.' },
  { type: 'anthropic', label: 'Anthropic', description: 'Use a hosted Claude model through the Messages API.' },
];
const TYPE_LABELS = Object.fromEntries(PROVIDER_TYPES.map((item) => [item.type, item.label])) as Record<ProviderType, string>;
const PAGE_SIZE = 50;

function emptyEditor(type: ProviderType = 'openai'): Editor {
  return {
    name: '',
    type,
    baseUrl: type === 'anthropic' ? 'https://api.anthropic.com' : '',
    model: '',
    wireApi: 'responses',
    azureApiVersion: type === 'azure' ? '2024-08-01-preview' : '',
    reasoningEfforts: '',
    promptCapacityTokens: '',
    apiKey: '',
    pricingChoice: '',
    rateCardId: '',
    rateCardVersion: '1',
    inputRate: '',
    outputRate: '',
    priceSourceUrl: '',
    clearConfirmed: false,
  };
}

function editorFromProvider(provider: PlatformModelProviderDraft): Editor {
  return {
    ...emptyEditor(provider.type as ProviderType),
    name: provider.name,
    baseUrl: provider.base_url,
    model: provider.model,
    wireApi: (provider.wire_api ?? 'responses') as 'responses' | 'completions',
    azureApiVersion: provider.azure_api_version ?? '',
    reasoningEfforts: provider.reasoning_efforts.join(', '),
    promptCapacityTokens: provider.prompt_capacity_tokens?.toString() ?? '',
    rateCardId: provider.rate_card_id ?? '',
    rateCardVersion: provider.rate_card_version ?? '1',
  };
}

function validPositiveDecimal(value: string): boolean {
  if (!/^(?:0|[1-9]\d*)(?:\.\d+)?$/.test(value)) return false;
  const [whole = '', fraction = ''] = value.split('.');
  const digits = `${whole}${fraction}`;
  const coefficient = digits.replace(/^0+/, '').replace(/0+$/, '');
  if (!coefficient) return false;
  const exponent = digits.length - digits.replace(/0+$/, '').length - fraction.length;
  if (exponent < -28 || exponent > 28 || coefficient.length > 29) return false;
  const integerDigits = exponent > 0
    ? `${coefficient}${'0'.repeat(exponent)}`
    : coefficient;
  return integerDigits.length < 29 ||
    integerDigits.length === 29 &&
    integerDigits <= '79228162514264337593543950335';
}

function nextVersion(value: string): string {
  const match = /^(.*?)(\d+)$/.exec(value);
  return match ? `${match[1]}${BigInt(match[2]) + 1n}` : `${value}-2`;
}

function switchMode(pageMode: string | null): 'untouched' | 'copilot' | 'byok' {
  if (pageMode === 'active' || pageMode === 'byok') return 'byok';
  if (pageMode === 'explicitHosted' || pageMode === 'copilot') return 'copilot';
  return 'untouched';
}

function rawManualRateCard(
  editor: Editor,
  id: string,
  version: string,
  retrievedOn: string,
): string {
  return `{"runtimeRateCardSchemaVersion":1,"rateCard":{"id":${JSON.stringify(id)},"version":${JSON.stringify(version)},"meterSource":"byok.tokens","unit":"USD","nanoUnitsPerUnit":1000000,"modelMultipliers":{${JSON.stringify(`${editor.model}|input`)}:${editor.inputRate},${JSON.stringify(`${editor.model}|output`)}:${editor.outputRate}}},"provenance":{"source":"manual","sourceUrl":${JSON.stringify(editor.priceSourceUrl)},"retrievedOn":${JSON.stringify(retrievedOn)}}}`;
}

function requirePlatformProvider(
  provider: PlatformModelProviderDraft,
): PlatformModelProviderDraft {
  if (provider.scope !== 'platform' || provider.projectId !== null || !provider.id)
    throw new Error('The Gateway returned a provider draft for a different scope.');
  return provider;
}

/** Manages platform BYOK providers inside the existing Platform Settings page. */
export function PlatformModelProviders() {
  const {
    apiCall,
    session,
    authSessionGeneration,
    authSessionEpoch,
    unboundSessionGeneration,
    authorizationContext,
    isActiveSession,
    ensureSession,
    authorize,
    resolveAuthorizationContext,
  } = useAuth();
  const [access, setAccess] = useState<AccessSnapshot | null>(null);
  const [accessError, setAccessError] = useState<unknown>(null);
  const [checkingAccess, setCheckingAccess] = useState(true);
  const [forbidden, setForbidden] = useState(false);
  const [providers, setProviders] = useState<PlatformModelProviderDraft[]>([]);
  const [nextAfterProviderId, setNextAfterProviderId] = useState<string | null>(null);
  const [settingsRevision, setSettingsRevision] = useState<number | null>(null);
  const [mode, setMode] = useState<'untouched' | 'copilot' | 'byok'>('untouched');
  const [activeProviderId, setActiveProviderId] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [typeQuery, setTypeQuery] = useState('');
  const [editor, setEditor] = useState<Editor | null>(null);
  const [editingProvider, setEditingProvider] = useState<PlatformModelProviderDraft | null>(null);
  const [showKey, setShowKey] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [switching, setSwitching] = useState<string | 'copilot' | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [credentialStatus, setCredentialStatus] = useState<{
    status: ModelProviderCredentialResponse['status'];
    providerId: string;
    providerState?: string | null;
  } | null>(null);
  const [credentialActionError, setCredentialActionError] = useState<unknown>(null);
  const [credentialRevisions, setCredentialRevisions] = useState<Record<string, number>>({});
  const pendingCredential = useRef<PendingCredential | null>(null);
  const [pendingCredentialState, setPendingCredentialState] = useState<PendingCredential | null>(null);
  const metadataOperation = useRef<{ fingerprint: string; operationId: string } | null>(null);
  const requestGeneration = useRef(0);
  const accessGeneration = useRef(0);
  const observedTenant = useRef<string | null>(null);
  const confirmedTenant = useRef<string | null>(authorizationContext?.tenantId ?? null);

  useLayoutEffect(() => {
    confirmedTenant.current = authorizationContext?.tenantId ?? null;
  }, [authorizationContext?.tenantId]);

  const clearRows = useCallback(() => {
    requestGeneration.current += 1;
    setProviders([]);
    setNextAfterProviderId(null);
    setSettingsRevision(null);
    setMode('untouched');
    setActiveProviderId(null);
    setEditor(null);
    setEditingProvider(null);
    setShowKey(false);
    setSaveError(null);
    setCredentialStatus(null);
    setCredentialActionError(null);
    setCredentialRevisions({});
    pendingCredential.current = null;
    setPendingCredentialState(null);
    metadataOperation.current = null;
    setLoadError(null);
    setLoading(false);
  }, []);

  const denyProviderControls = useCallback((reason: unknown): boolean => {
    if (!(reason instanceof GatewayError) || reason.status !== 403) return false;
    clearRows();
    setForbidden(true);
    return true;
  }, [clearRows]);

  useEffect(() => {
    const generation = ++accessGeneration.current;
    const timer = window.setTimeout(() => {
      clearRows();
      setAccess(null);
      setForbidden(false);
      setAccessError(null);
      setCheckingAccess(true);
      if (!session || session.binding) {
        setCheckingAccess(false);
        return;
      }
      void (async () => {
        try {
          const context = await resolveAuthorizationContext();
          if (generation !== accessGeneration.current ||
              !context || !isActiveSession(session) || session.binding)
            return;
          confirmedTenant.current = context.tenantId;
          setAccess({
            session,
            sessionEpoch: authSessionEpoch,
            sessionGeneration: authSessionGeneration,
            unboundSessionGeneration,
            tenant: context.tenantId,
          });
        } catch (reason) {
          if (generation === accessGeneration.current) setAccessError(reason);
        } finally {
          if (generation === accessGeneration.current) setCheckingAccess(false);
        }
      })();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [
    authSessionEpoch,
    authSessionGeneration,
    clearRows,
    isActiveSession,
    resolveAuthorizationContext,
    session,
    unboundSessionGeneration,
  ]);

  useEffect(() => {
    const tenant = authorizationContext?.tenantId ?? null;
    const previous = observedTenant.current;
    observedTenant.current = tenant;
    if (previous !== null && tenant !== previous) {
      requestGeneration.current += 1;
      const timer = window.setTimeout(() => {
        clearRows();
        setAccess(null);
        setForbidden(false);
      }, 0);
      return () => window.clearTimeout(timer);
    }
  }, [authorizationContext?.tenantId, clearRows]);

  const isCurrent = useCallback((snapshot: AccessSnapshot): boolean =>
    isActiveSession(snapshot.session) &&
    !snapshot.session.binding &&
    authSessionEpoch === snapshot.sessionEpoch &&
    authSessionGeneration === snapshot.sessionGeneration &&
    unboundSessionGeneration === snapshot.unboundSessionGeneration &&
    session?.accessToken === snapshot.session.accessToken &&
    confirmedTenant.current === snapshot.tenant,
  [
    authSessionEpoch,
    authSessionGeneration,
    isActiveSession,
    session,
    unboundSessionGeneration,
  ]);

  const boundCall = useCallback(async <T,>(
    snapshot: AccessSnapshot,
    operation: (token: string) => Promise<T>,
  ): Promise<T> => {
    if (!isCurrent(snapshot))
      throw new GatewayError(401, { code: 'platform_session_changed' }, 'The platform sign-in changed. Review the page and try again.');
    return apiCall(async (token, liveTenant) => {
      if (!isCurrent(snapshot) ||
          token !== snapshot.session.accessToken ||
          liveTenant !== snapshot.tenant)
        throw new GatewayError(401, { code: 'platform_session_changed' }, 'The platform sign-in changed. Review the page and try again.');
      return operation(token);
    }, null);
  }, [apiCall, isCurrent]);

  const loadPage = useCallback(async (
    snapshot = access,
    afterProviderId: string | null = null,
    append = false,
  ) => {
    if (!snapshot || !isCurrent(snapshot)) return;
    const generation = ++requestGeneration.current;
    if (!append) setProviders([]);
    setLoading(true);
    setLoadError(null);
    try {
      const page = await boundCall(snapshot, (token) =>
        gatewayClient.listPlatformByokProviderDrafts(
          token, PAGE_SIZE, afterProviderId, snapshot.tenant,
        ));
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;
      const rows = page.providers.map(requirePlatformProvider);
      setProviders((current) => append ? [...current, ...rows] : rows);
      setNextAfterProviderId(page.nextAfterProviderId);
      setSettingsRevision(page.settingsRevision);
      setMode(switchMode(page.switchMode));
      setActiveProviderId(page.activeProviderId);
      setForbidden(false);
    } catch (reason) {
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;
      if (!denyProviderControls(reason)) {
        setLoadError(reason);
      }
    } finally {
      if (generation === requestGeneration.current) setLoading(false);
    }
  }, [access, boundCall, denyProviderControls, isCurrent]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (access) void loadPage(access);
    }, 0);
    return () => window.clearTimeout(timer);
  }, [access, loadPage]);

  const switchPlatformSignIn = async () => {
    setAccessError(null);
    if (!await ensureSession(null, true)) await authorize(null);
  };

  const filteredTypes = useMemo(() => {
    const query = typeQuery.trim().toLowerCase();
    return PROVIDER_TYPES.filter((item) =>
      !query || item.label.toLowerCase().includes(query) || item.description.toLowerCase().includes(query));
  }, [typeQuery]);

  const closeEditor = () => {
    setEditor(null);
    setEditingProvider(null);
    setShowKey(false);
    setSaveError(null);
  };

  const startEdit = async (provider: PlatformModelProviderDraft) => {
    if (!access) return;
    setSaveError(null);
    setCredentialActionError(null);
    try {
      const latest = await boundCall(access, (token) =>
        gatewayClient.getPlatformByokProviderDraft(token, provider.id, access.tenant));
      if (!isCurrent(access)) return;
      const checked = requirePlatformProvider(latest);
      setEditingProvider(checked);
      setEditor(editorFromProvider(checked));
      setShowKey(false);
    } catch (reason) {
      if (!denyProviderControls(reason)) setLoadError(reason);
    }
  };

  const updateEditor = <K extends keyof Editor>(field: K, value: Editor[K]) => {
    setEditor((current) => current ? { ...current, [field]: value } : current);
    setSaveError(null);
  };

  const assertSaveReady = (value: Editor): string | null => {
    if (!value.name.trim() || !value.baseUrl.trim() || !value.model.trim())
      return 'Enter a display name, base URL, and model or deployment.';
    if (value.promptCapacityTokens.trim() &&
        (!Number.isSafeInteger(Number(value.promptCapacityTokens)) || Number(value.promptCapacityTokens) <= 0))
      return 'Prompt capacity must be a positive whole number.';
    if (editingProvider && !value.pricingChoice)
      return 'Choose whether to save manual prices, use the saved rate card, or clear it.';
    if (!value.pricingChoice)
      return 'Choose Save manual prices or Use a saved rate card.';
    if (value.pricingChoice === 'saved' && (!value.rateCardId.trim() || !value.rateCardVersion.trim()))
      return 'Enter the saved rate-card ID and version.';
    if (value.pricingChoice === 'manual' &&
        (!validPositiveDecimal(value.inputRate) || !validPositiveDecimal(value.outputRate) ||
         !value.priceSourceUrl.trim()))
      return 'Enter positive exact input and output rates and a source URL.';
    if (value.pricingChoice === 'clear' && !value.clearConfirmed)
      return 'Confirm that the saved rate-card link should be cleared.';
    return null;
  };

  const saveManualPricing = useCallback(async (
    snapshot: AccessSnapshot,
    value: Editor,
  ): Promise<{ rateCardId: string; rateCardVersion: string }> => {
    const id = value.rateCardId.trim() || `platform-byok-${crypto.randomUUID().replaceAll('-', '')}`;
    let version = value.rateCardVersion.trim() || '1';
    const retrievedOn = new Date().toISOString().slice(0, 10);
    for (let attempt = 0; attempt < 4; attempt += 1) {
      try {
        await boundCall(snapshot, (token) => gatewayClient.savePlatformManualRuntimeRateCard(
          token, id, version, rawManualRateCard(value, id, version, retrievedOn), snapshot.tenant,
        ));
        return { rateCardId: id, rateCardVersion: version };
      } catch (reason) {
        if (!(reason instanceof GatewayError) || reason.status !== 409) throw reason;
        version = nextVersion(version);
      }
    }
    throw new Error('The price-card version conflicts. Choose a new version.');
  }, [boundCall]);

  const saveProvider = async (event: FormEvent) => {
    event.preventDefault();
    if (!editor || !access || !isCurrent(access)) return;
    const invalid = assertSaveReady(editor);
    if (invalid) {
      setSaveError(new Error(invalid));
      return;
    }
    const snapshot = access;
    const source = editingProvider;
    const submittedApiKey = editor.apiKey;
    setSaving(true);
    setSaveError(null);
    setCredentialActionError(null);
    setNotice(null);
    setEditor((current) => current ? { ...current, apiKey: '' } : current);
    setShowKey(false);
    const keyWasSubmitted = Boolean(submittedApiKey.trim());
    try {
      let rateCardId: string | null | undefined;
      let rateCardVersion: string | null | undefined;
      if (editor.pricingChoice === 'manual') {
        const saved = await saveManualPricing(snapshot, editor);
        rateCardId = saved.rateCardId;
        rateCardVersion = saved.rateCardVersion;
      } else if (editor.pricingChoice === 'saved') {
        rateCardId = editor.rateCardId.trim();
        rateCardVersion = editor.rateCardVersion.trim();
      } else {
        rateCardId = null;
        rateCardVersion = null;
      }
      if (!isCurrent(snapshot)) return;

      const promptCapacity = editor.promptCapacityTokens.trim()
        ? Number(editor.promptCapacityTokens)
        : null;
      const body: ProjectModelProviderDraftSaveRequest = {
        operationId: '',
        expectedRevision: source?.revision ?? 0,
        name: editor.name.trim(),
        type: editor.type,
        base_url: editor.baseUrl.trim(),
        model: editor.model.trim(),
        wire_api: editor.type === 'anthropic' ? null : editor.wireApi,
        azure_api_version: editor.type === 'azure' ? editor.azureApiVersion.trim() || null : null,
        reasoning_efforts: editor.reasoningEfforts.split(',').map((item) => item.trim()).filter(Boolean),
        prompt_capacity_tokens: promptCapacity,
        rate_card_id: rateCardId,
        rate_card_version: rateCardVersion,
      };
      const fingerprint = JSON.stringify({ providerId: source?.id ?? null, body });
      const metadataOp = metadataOperation.current;
      body.operationId = metadataOp?.fingerprint === fingerprint
        ? metadataOp.operationId
        : crypto.randomUUID();
      metadataOperation.current = { fingerprint, operationId: body.operationId };

      const saved = await boundCall(snapshot, (token) => source
        ? gatewayClient.updatePlatformByokProviderDraft(token, source.id, body, snapshot.tenant)
        : gatewayClient.createPlatformByokProviderDraft(token, body, snapshot.tenant));
      if (!isCurrent(snapshot)) return;
      const checked = requirePlatformProvider(saved);

      if (keyWasSubmitted) {
        const credentialOperationId = crypto.randomUUID();
        const request = {
          operationId: credentialOperationId,
          expectedProviderRevision: checked.revision,
          expectedCredentialRevision: 0,
          apiKey: submittedApiKey,
        };
        try {
          const result = await boundCall(snapshot, (token) =>
            gatewayClient.setPlatformByokProviderCredential(
              token, checked.id, request, snapshot.tenant,
            ));
          if (!isCurrent(snapshot)) return;
          setCredentialStatus({
            status: result.status,
            providerId: result.providerId,
            providerState: result.providerState,
          });
          const credentialRevision = result.credentialRevision;
          if (credentialRevision !== null)
            setCredentialRevisions((current) => ({
              ...current,
              [checked.id]: credentialRevision,
            }));
          pendingCredential.current = null;
          setPendingCredentialState(null);
          setNotice('Provider settings and key were saved.');
        } catch (reason) {
          if (!isCurrent(snapshot)) return;
          if (reason instanceof GatewayError && reason.status === 503 &&
              reason.code === 'credential_publication_pending') {
            const pending = {
              providerId: checked.id,
              operationId: credentialOperationId,
              providerRevision: checked.revision,
              expectedCredentialRevision: 0,
              status: 'publication_pending',
            } as const;
            pendingCredential.current = pending;
            setPendingCredentialState(pending);
            setCredentialStatus({
              status: 'publication_pending',
              providerId: checked.id,
              providerState: 'Draft',
            });
            closeEditor();
          } else if (reason instanceof GatewayError && reason.status === 409 &&
              reason.code === 'credential_write_quarantined') {
            const revision = Number(reason.problem.credentialRevision);
            const pending = {
              providerId: checked.id,
              operationId: credentialOperationId,
              providerRevision: checked.revision,
              expectedCredentialRevision: Number.isFinite(revision) ? revision : 0,
              status: 'quarantined',
            } as const;
            pendingCredential.current = pending;
            setPendingCredentialState(pending);
            setCredentialStatus({
              status: 'quarantined',
              providerId: checked.id,
              providerState: 'Draft',
            });
            setSaveError(new Error('The key write was quarantined. The original operation must be recovered by an operator with the stored secret version.'));
          } else {
            throw reason;
          }
        }
      } else {
        setNotice('Provider settings were saved. Add a key before this provider can become Available.');
      }
      if (isCurrent(snapshot) && pendingCredential.current?.status !== 'publication_pending') {
        closeEditor();
        await loadPage(snapshot);
      }
    } catch (reason) {
      if (!isCurrent(snapshot)) return;
      if (denyProviderControls(reason)) return;
      setSaveError(reason);
      if (reason instanceof GatewayError && reason.status === 409)
        setNotice('The provider changed while it was open. Close it and reload the latest revision.');
    } finally {
      setSaving(false);
    }
  };

  const replayCredential = async () => {
    const pending = pendingCredential.current;
    if (!access || !pending || pending.status !== 'publication_pending')
      return;
    setSaving(true);
    setCredentialActionError(null);
    try {
      const result = await boundCall(access, (token) =>
        gatewayClient.setPlatformByokProviderCredential(token, pending.providerId, {
          operationId: pending.operationId,
          expectedProviderRevision: pending.providerRevision,
          expectedCredentialRevision: pending.expectedCredentialRevision,
          recheckExistingVersion: true,
        }, access.tenant));
      if (!isCurrent(access)) return;
      setCredentialStatus({
        status: result.status,
        providerId: result.providerId,
        providerState: result.providerState,
      });
      const credentialRevision = result.credentialRevision;
      if (credentialRevision !== null)
        setCredentialRevisions((current) => ({
          ...current,
          [pending.providerId]: credentialRevision,
        }));
      if (result.status === 'published') {
        pendingCredential.current = null;
        setPendingCredentialState(null);
      }
      await loadPage(access);
    } catch (reason) {
      if (isCurrent(access) && !denyProviderControls(reason)) setCredentialActionError(reason);
    } finally {
      setSaving(false);
    }
  };

  const recheckSavedKey = async (provider: PlatformModelProviderDraft) => {
    const credentialRevision = credentialRevisions[provider.id];
    if (!access || credentialRevision === undefined) return;
    setSaving(true);
    setCredentialActionError(null);
    try {
      const result = await boundCall(access, (token) =>
        gatewayClient.setPlatformByokProviderCredential(token, provider.id, {
          operationId: crypto.randomUUID(),
          expectedProviderRevision: provider.revision,
          expectedCredentialRevision: credentialRevision,
          recheckExistingVersion: true,
        }, access.tenant));
      if (!isCurrent(access)) return;
      const nextCredentialRevision = result.credentialRevision;
      if (nextCredentialRevision !== null)
        setCredentialRevisions((current) => ({
          ...current,
          [provider.id]: nextCredentialRevision,
        }));
      setCredentialStatus({
        status: result.status,
        providerId: result.providerId,
        providerState: result.providerState,
      });
      await loadPage(access);
    } catch (reason) {
      if (isCurrent(access) && !denyProviderControls(reason)) setCredentialActionError(reason);
    } finally {
      setSaving(false);
    }
  };

  const switchDefault = async (provider?: PlatformModelProviderDraft) => {
    if (!access || settingsRevision === null || settingsRevision <= 0) return;
    const selected = provider ? provider.id : 'copilot';
    setSwitching(selected);
    setLoadError(null);
    try {
      await boundCall(access, (token) => provider
        ? gatewayClient.activatePlatformByokProvider(token, provider.id, {
          expectedSettingsRevision: settingsRevision,
          expectedProviderRevision: provider.revision,
        }, access.tenant)
        : gatewayClient.deactivatePlatformByokProvider(token, {
          expectedSettingsRevision: settingsRevision,
        }, access.tenant));
      if (!isCurrent(access)) return;
      await loadPage(access);
    } catch (reason) {
      if (!isCurrent(access)) return;
      if (denyProviderControls(reason)) return;
      setLoadError(reason);
      if (reason instanceof GatewayError && reason.status === 409)
        await loadPage(access);
    } finally {
      setSwitching(null);
    }
  };

  const controlsDisabled = forbidden || !access || loading || saving;
  const activeSessionMessage = !session
    ? 'Sign in to manage platform providers.'
    : 'Switch to a platform sign-in before managing providers. Provider controls use an unbound Broker session.';

  return (
    <PageSection
      title="Model providers"
      description="Save provider metadata and exact prices, store a write-only key, and choose the default for new runs."
      actions={<Button appearance="primary" icon={<AddRegular />} disabled={controlsDisabled}
        onClick={() => { setPickerOpen(true); setTypeQuery(''); }}>Add provider</Button>}
    >
      {checkingAccess && <Loading label="Checking platform access" />}
      {!checkingAccess && !access && !accessError && (
        <div className="v1-stack">
          <Text>{activeSessionMessage}</Text>
          <Button onClick={() => void switchPlatformSignIn()}>
            {session ? 'Switch to platform sign-in' : 'Sign in'}
          </Button>
        </div>
      )}
      {accessError != null && <ErrorNotice>{errorMessage(accessError)}</ErrorNotice>}
      {forbidden && <MessageBar intent="warning"><MessageBarBody>
        Your role cannot manage platform providers. The rest of Platform Settings remains available.
      </MessageBarBody></MessageBar>}
      {loadError != null && <ErrorNotice>{errorMessage(loadError)}</ErrorNotice>}
      {notice && <MessageBar intent="success"><MessageBarBody>{notice}</MessageBarBody></MessageBar>}
      {credentialStatus && (
        <MessageBar intent={credentialStatus.status === 'published' ? 'success' : 'warning'}>
          <MessageBarBody>
            Key status: {credentialStatus.status.replaceAll('_', ' ')}.
            {' '}Provider state: {credentialStatus.providerState ?? 'Draft'}.
          </MessageBarBody>
        </MessageBar>
      )}
      {pendingCredentialState?.status === 'publication_pending' && (
        <div className="v1-actions">
          <Button onClick={() => void replayCredential()} disabled={saving}>Retry publication</Button>
          <Text>Replay uses the original operation and does not send the key again.</Text>
          {credentialActionError != null && <ErrorNotice>{errorMessage(credentialActionError)}</ErrorNotice>}
        </div>
      )}
      {pendingCredentialState?.status === 'quarantined' && (
        <MessageBar intent="warning"><MessageBarBody>
          Recovery handoff: give the platform operator operation ID
          {' '}<code>{pendingCredentialState.operationId}</code> and recover the stored
          secret version with the original expected revisions.
        </MessageBarBody></MessageBar>
      )}
      {access && !forbidden && (
        <>
          {mode === 'untouched' && settingsRevision === 0 && (
            <MessageBar intent="info"><MessageBarBody>
              Initialize platform model settings before changing the default for new runs.
            </MessageBarBody></MessageBar>
          )}
          {loading && providers.length === 0 ? <Loading label="Loading model providers" /> : null}
          {!loading && providers.length === 0 && <Text>No platform model providers are saved.</Text>}
          {providers.length > 0 && (
            <ul className="v1-permission-list" aria-label="Platform model providers">
              {providers.map((provider) => {
                const isDefault = mode === 'byok' && activeProviderId === provider.id;
                return (
                  <li key={provider.id}>
                    <div>
                      <strong>{provider.name}</strong>
                      <div>{TYPE_LABELS[provider.type as ProviderType] ?? provider.type} · {provider.model}</div>
                      <div className="v1-actions">
                        <Badge appearance="outline">{provider.state}</Badge>
                        <Badge appearance="outline" color={provider.hasCredential ? 'success' : 'warning'}>
                          {provider.hasCredential ? 'Key saved' : 'No key saved'}
                        </Badge>
                        {isDefault && <Badge appearance="tint" color="success">Default for new runs</Badge>}
                      </div>
                      {provider.rate_card_id && (
                        <Text className="v1-muted">
                          Rate card: {provider.rate_card_id} / {provider.rate_card_version}
                        </Text>
                      )}
                    </div>
                    <div className="v1-actions">
                      {provider.state === 'Draft' && (
                        <Button icon={<EditRegular />} onClick={() => void startEdit(provider)}
                          disabled={controlsDisabled}>Edit</Button>
                      )}
                      {provider.state === 'Draft' && provider.hasCredential &&
                        credentialRevisions[provider.id] !== undefined && (
                        <Button onClick={() => void recheckSavedKey(provider)}
                          disabled={controlsDisabled}>Recheck saved key</Button>
                      )}
                      <Button appearance={isDefault ? 'secondary' : 'primary'}
                        onClick={() => void switchDefault(provider)}
                        disabled={controlsDisabled || switching !== null ||
                          provider.state !== 'Available' || settingsRevision === null ||
                          settingsRevision === 0 || isDefault}>
                        {isDefault ? 'Default for new runs'
                          : switching === provider.id ? 'Switching…' : 'Use for new runs'}
                      </Button>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
          {nextAfterProviderId && (
            <Button disabled={controlsDisabled}
              onClick={() => void loadPage(access, nextAfterProviderId, true)}>Load more</Button>
          )}
          {!pendingCredentialState && credentialActionError != null &&
            <ErrorNotice>{errorMessage(credentialActionError)}</ErrorNotice>}
          <div className="v1-actions">
            <Button onClick={() => void switchDefault()} disabled={controlsDisabled ||
              switching !== null || settingsRevision === null || settingsRevision === 0 || mode === 'copilot'}>
              {mode === 'copilot' ? 'Default for new runs: Copilot'
                : switching === 'copilot' ? 'Switching…' : 'Use Copilot for new runs'}
            </Button>
            <Button onClick={() => void loadPage(access)} disabled={controlsDisabled}>Refresh</Button>
          </div>
        </>
      )}

      {pickerOpen && (
        <div className="v1-form">
          <Field label="Provider type">
            <Input value={typeQuery} placeholder="Search provider types"
              onChange={(_, data) => setTypeQuery(data.value)} />
          </Field>
          <div className="v1-stack">
            {filteredTypes.map((item) => (
              <Button key={item.type} appearance="secondary" onClick={() => {
                setPickerOpen(false);
                setEditor(emptyEditor(item.type));
                setEditingProvider(null);
                setSaveError(null);
              }}>
                <span><strong>{item.label}</strong><br />{item.description}</span>
              </Button>
            ))}
            {filteredTypes.length === 0 && <Text>No provider type matches “{typeQuery}”.</Text>}
          </div>
          <Button type="button" onClick={() => setPickerOpen(false)}>Cancel</Button>
        </div>
      )}

      {editor && (
        <form className="v1-form" onSubmit={(event) => void saveProvider(event)}>
          <h3>{editingProvider ? 'Edit' : 'Add'} {TYPE_LABELS[editor.type]}</h3>
          <Field label="Display name" required>
            <Input aria-label="Display name" value={editor.name} required maxLength={128} disabled={saving}
              onChange={(_, data) => updateEditor('name', data.value)} />
          </Field>
          <Field label="Base URL" required>
            <Input aria-label="Base URL" type="url" value={editor.baseUrl} required disabled={saving}
              onChange={(_, data) => updateEditor('baseUrl', data.value)}
              placeholder={editor.type === 'azure' ? 'https://resource.openai.azure.com' : 'https://api.example.com/v1'} />
          </Field>
          {editor.type === 'azure' && (
            <Field label="API version" required>
              <Input aria-label="API version" value={editor.azureApiVersion} required disabled={saving}
                onChange={(_, data) => updateEditor('azureApiVersion', data.value)} />
            </Field>
          )}
          <Field label="Model or deployment" required>
            <Input aria-label="Model or deployment" value={editor.model} required disabled={saving}
              onChange={(_, data) => updateEditor('model', data.value)} />
          </Field>
          {editor.type !== 'anthropic' && (
            <Field label="Wire API">
              <Dropdown aria-label="Wire API" value={editor.wireApi === 'responses' ? 'Responses' : 'Completions'}
                selectedOptions={[editor.wireApi]} disabled={saving}
                onOptionSelect={(_, data) => {
                  if (data.optionValue)
                    updateEditor('wireApi', data.optionValue as Editor['wireApi']);
                }}>
                <Option value="responses">Responses</Option>
                <Option value="completions">Completions</Option>
              </Dropdown>
            </Field>
          )}
          <Field label="Reasoning efforts" hint="Separate values with commas.">
            <Input aria-label="Reasoning efforts" value={editor.reasoningEfforts} disabled={saving}
              onChange={(_, data) => updateEditor('reasoningEfforts', data.value)} />
          </Field>
          <Field label="Prompt capacity tokens">
            <Input aria-label="Prompt capacity tokens" type="number" min={1} step={1}
              value={editor.promptCapacityTokens} disabled={saving}
              onChange={(_, data) => updateEditor('promptCapacityTokens', data.value)} />
          </Field>

          <h3>Pricing</h3>
          <RadioGroup value={editor.pricingChoice}
            onChange={(_, data) => updateEditor('pricingChoice', data.value as PricingChoice)}>
            <Radio value="manual" label="Save manual prices" />
            <Radio value="saved" label="Use a saved rate card" />
            {editingProvider?.rate_card_id && <Radio value="clear" label="Clear the saved rate card" />}
          </RadioGroup>
          {editor.pricingChoice === 'manual' && (
            <>
              <Field label="Rate-card ID" hint="Reuse the ID and choose a new version when prices change.">
                <Input aria-label="Rate-card ID" value={editor.rateCardId} disabled={saving}
                  onChange={(_, data) => updateEditor('rateCardId', data.value)} />
              </Field>
              <Field label="Rate-card version">
                <Input aria-label="Rate-card version" value={editor.rateCardVersion} disabled={saving}
                  onChange={(_, data) => updateEditor('rateCardVersion', data.value)} />
              </Field>
              <Field label="Input price per 1M tokens (USD)">
                <Input aria-label="Input price per 1M tokens (USD)" inputMode="decimal"
                  value={editor.inputRate} disabled={saving}
                  onChange={(_, data) => updateEditor('inputRate', data.value)} />
              </Field>
              <Field label="Output price per 1M tokens (USD)">
                <Input aria-label="Output price per 1M tokens (USD)" inputMode="decimal"
                  value={editor.outputRate} disabled={saving}
                  onChange={(_, data) => updateEditor('outputRate', data.value)} />
              </Field>
              <Field label="Price source URL" required>
                <Input aria-label="Price source URL" type="url" value={editor.priceSourceUrl} disabled={saving}
                  onChange={(_, data) => updateEditor('priceSourceUrl', data.value)} />
              </Field>
            </>
          )}
          {editor.pricingChoice === 'saved' && (
            <>
              <Field label="Rate-card ID" required>
                <Input aria-label="Rate-card ID" value={editor.rateCardId} disabled={saving}
                  onChange={(_, data) => updateEditor('rateCardId', data.value)} />
              </Field>
              <Field label="Rate-card version" required>
                <Input aria-label="Rate-card version" value={editor.rateCardVersion} disabled={saving}
                  onChange={(_, data) => updateEditor('rateCardVersion', data.value)} />
              </Field>
            </>
          )}
          {editor.pricingChoice === 'clear' && (
            <Checkbox checked={editor.clearConfirmed}
              onChange={(_, data) => updateEditor('clearConfirmed', Boolean(data.checked))}
              label="Clear the saved rate-card link when I save these changes." />
          )}

          <Field label="API key" hint="The key stays in this form only. Leave it blank to keep the saved key.">
            <Input aria-label="API key" type={showKey ? 'text' : 'password'} autoComplete="new-password"
              value={editor.apiKey} disabled={saving}
              onChange={(_, data) => updateEditor('apiKey', data.value)}
              contentAfter={<Button appearance="transparent" size="small"
                aria-label={showKey ? 'Hide API key' : 'Show API key'}
                icon={showKey ? <EyeOff24Regular /> : <Eye24Regular />}
                onClick={() => setShowKey((current) => !current)} />} />
          </Field>
          {saveError != null && <ErrorNotice>{errorMessage(saveError)}</ErrorNotice>}
          <div className="v1-actions">
            <Button type="button" disabled={saving} onClick={closeEditor}>Cancel</Button>
            <Button type="submit" appearance="primary" disabled={saving || controlsDisabled}>
              {saving ? 'Saving…' : editingProvider ? 'Save changes' : 'Save provider'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>
        </form>
      )}
    </PageSection>
  );
}
