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
  ModelProviderCredentialOperationStatusResponse,
  ModelProviderCredentialResponse,
  PlatformModelProviderDraft,
  ProjectModelProviderDraftSaveRequest,
} from './contracts';
import { errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';
import {
  createProjectCredentialOperationStore,
  type ProjectCredentialOperation,
  type ProjectCredentialOperationKey,
} from './projectCredentialOperations';

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
  sessionEpoch: number;
  tenant: string;
  actorId: string | null;
  issuer: string | null;
}

class StaleRequestError extends Error {
  constructor() {
    super('The platform session changed before the request was sent.');
    this.name = 'StaleRequestError';
  }
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

function requirePlatformCredentialResponse(
  response: ModelProviderCredentialResponse,
  providerId: string,
  expectedOperationId?: string,
): ModelProviderCredentialResponse {
  if (response.scope !== 'platform' ||
      response.projectId !== null ||
      response.providerId !== providerId ||
      (expectedOperationId !== undefined && response.operationId !== expectedOperationId)) {
    throw new Error('The service returned a credential response for a different scope or provider.');
  }
  return response;
}

function requirePlatformCredentialOperationStatus(
  response: ModelProviderCredentialOperationStatusResponse,
  providerId: string,
  expectedOperationId: string,
): ModelProviderCredentialOperationStatusResponse {
  if (response.scope !== 'platform' ||
      response.projectId !== null ||
      response.providerId !== providerId ||
      response.operationId !== expectedOperationId) {
    throw new Error('The service returned a credential operation status for a different scope or provider.');
  }
  return response;
}

function isPreClaimCredentialRejection(reason: unknown, isRecheck = false): boolean {
  if (!(reason instanceof GatewayError)) return false;
  if (reason.status === 400 || reason.status === 413 || reason.status === 415) return true;
  if (reason.status === 404 && reason.code === 'model_provider_draft_not_found') return true;
  if (isRecheck && reason.status === 404 && reason.code === 'credential_slot_not_found') return true;
  if (reason.status === 409 && reason.code === 'credential_slot_conflict') return true;
  if (reason.status === 503 && reason.code === 'credential_vault_unavailable') return true;
  return false;
}

/** Manages platform BYOK providers inside the existing Platform Settings page. */
export function PlatformModelProviders() {
  const {
    apiCall,
    apiCallOnce,
    projectCredentialOperations,
    session,
    authSessionEpoch = 0,
    authorizationContext,
    isActiveSession,
    ensureSession,
    authorize,
    resolveAuthorizationContext,
  } = useAuth();
  const [fallbackOperationStore] = useState(() => createProjectCredentialOperationStore());
  const operationStore = projectCredentialOperations ?? fallbackOperationStore;
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
  const [operationRevision, setOperationRevision] = useState(0);
  const [confirmingResetProviderId, setConfirmingResetProviderId] = useState<string | null>(null);
  const metadataOperation = useRef<{ fingerprint: string; operationId: string } | null>(null);
  const mountedRef = useRef(true);
  const clearPendingKeyRef = useRef<(() => void) | null>(null);
  const requestGeneration = useRef(0);
  const accessGeneration = useRef(0);
  const hasUnboundSession = Boolean(session && !session.binding);
  const sessionRef = useRef(session);
  const authSessionEpochRef = useRef(authSessionEpoch);
  const observedIdentity = useRef<{
    tenantId: string | null;
    actorId: string | null;
    issuer: string | null;
  } | null>(null);
  const confirmedIdentityRef = useRef<{
    tenantId: string | null;
    actorId: string | null;
    issuer: string | null;
  }>({
    tenantId: authorizationContext?.tenantId ?? null,
    actorId: authorizationContext?.actorId ?? null,
    issuer: authorizationContext?.issuer ?? null,
  });

  useLayoutEffect(() => {
    sessionRef.current = session;
  }, [session]);

  useLayoutEffect(() => {
    authSessionEpochRef.current = authSessionEpoch;
  }, [authSessionEpoch]);

  useLayoutEffect(() => {
    confirmedIdentityRef.current = {
      tenantId: authorizationContext?.tenantId ?? null,
      actorId: authorizationContext?.actorId ?? null,
      issuer: authorizationContext?.issuer ?? null,
    };
  }, [authorizationContext?.tenantId, authorizationContext?.actorId, authorizationContext?.issuer]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      clearPendingKeyRef.current?.();
      clearPendingKeyRef.current = null;
      requestGeneration.current += 1;
      accessGeneration.current += 1;
    };
  }, []);

  const clearRows = useCallback(() => {
    clearPendingKeyRef.current?.();
    clearPendingKeyRef.current = null;
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
    setConfirmingResetProviderId(null);
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

  const handleAccessFailure = useCallback((reason: unknown): boolean => {
    if (denyProviderControls(reason)) return true;
    if (!(reason instanceof GatewayError) || reason.status !== 401) return false;
    clearRows();
    setAccess(null);
    setAccessError(reason);
    return true;
  }, [clearRows, denyProviderControls]);

  useEffect(() => {
    const generation = ++accessGeneration.current;
    const timer = window.setTimeout(() => {
      clearRows();
      setAccess(null);
      setForbidden(false);
      setAccessError(null);
      setCheckingAccess(true);
      const currentSession = sessionRef.current;
      if (!hasUnboundSession || !currentSession || currentSession.binding) {
        setCheckingAccess(false);
        return;
      }
      void (async () => {
        try {
          const context = await resolveAuthorizationContext();
          const liveSession = sessionRef.current;
          if (generation !== accessGeneration.current ||
              !mountedRef.current ||
              !context || !liveSession || liveSession.binding || !isActiveSession(liveSession))
            return;
          confirmedIdentityRef.current = {
            tenantId: context.tenantId,
            actorId: context.actorId ?? null,
            issuer: context.issuer ?? null,
          };
          setAccess({
            sessionEpoch: authSessionEpochRef.current,
            tenant: context.tenantId,
            actorId: context.actorId ?? null,
            issuer: context.issuer ?? null,
          });
        } catch (reason) {
          if (generation === accessGeneration.current && mountedRef.current) setAccessError(reason);
        } finally {
          if (generation === accessGeneration.current && mountedRef.current) setCheckingAccess(false);
        }
      })();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [
    authSessionEpoch,
    clearRows,
    hasUnboundSession,
    isActiveSession,
    resolveAuthorizationContext,
  ]);

  useEffect(() => {
    const tenantId = authorizationContext?.tenantId ?? null;
    if (tenantId === null) return;
    const current = {
      tenantId,
      actorId: authorizationContext?.actorId ?? null,
      issuer: authorizationContext?.issuer ?? null,
    };
    const previous = observedIdentity.current;
    observedIdentity.current = current;
    if (previous !== null && (
      current.tenantId !== previous.tenantId ||
      (previous.actorId !== null && current.actorId !== previous.actorId) ||
      (previous.issuer !== null && current.issuer !== previous.issuer)
    )) {
      requestGeneration.current += 1;
      const timer = window.setTimeout(() => {
        clearRows();
        setAccess(null);
        setForbidden(false);
      }, 0);
      return () => window.clearTimeout(timer);
    }
  }, [
    authorizationContext?.actorId,
    authorizationContext?.issuer,
    authorizationContext?.tenantId,
    clearRows,
  ]);

  const isSameConfirmedIdentity = useCallback((snapshot: AccessSnapshot): boolean => {
    const liveSession = sessionRef.current;
    const liveIdentity = confirmedIdentityRef.current;
    return (
      Boolean(liveSession && !liveSession.binding && isActiveSession(liveSession)) &&
      authSessionEpochRef.current === snapshot.sessionEpoch &&
      liveIdentity.tenantId === snapshot.tenant &&
      (liveIdentity.actorId === null || snapshot.actorId === null || liveIdentity.actorId === snapshot.actorId) &&
      (liveIdentity.issuer === null || snapshot.issuer === null || liveIdentity.issuer === snapshot.issuer)
    );
  }, [isActiveSession]);

  const isCurrent = useCallback((snapshot: AccessSnapshot): boolean => {
    return mountedRef.current && isSameConfirmedIdentity(snapshot);
  }, [isSameConfirmedIdentity]);

  const boundCall = useCallback(async <T,>(
    snapshot: AccessSnapshot,
    operation: (token: string) => Promise<T>,
  ): Promise<T> => {
    if (!isCurrent(snapshot))
      throw new GatewayError(401, { code: 'platform_session_changed' }, 'The platform sign-in changed. Review the page and try again.');
    return apiCall(async (token, liveTenant) => {
      if (!isCurrent(snapshot) || liveTenant !== snapshot.tenant)
        throw new GatewayError(401, { code: 'platform_session_changed' }, 'The platform sign-in changed. Review the page and try again.');
      return operation(token);
    }, null);
  }, [apiCall, isCurrent]);

  const buildOperationKey = useCallback((
    snapshot: AccessSnapshot | null,
    providerId: string,
  ): ProjectCredentialOperationKey | null => {
    if (!snapshot || !snapshot.issuer || !snapshot.actorId || !snapshot.tenant || !providerId)
      return null;
    return {
      issuer: snapshot.issuer,
      actorId: snapshot.actorId,
      tenantId: snapshot.tenant,
      scope: 'platform',
      projectId: null,
      providerId,
    };
  }, []);

  const setStoredOperation = useCallback((
    key: ProjectCredentialOperationKey,
    operation: ProjectCredentialOperation,
  ) => {
    operationStore.set(key, operation);
    if (mountedRef.current) setOperationRevision((revision) => revision + 1);
  }, [operationStore]);

  const deleteStoredOperation = useCallback((key: ProjectCredentialOperationKey) => {
    operationStore.delete(key);
    if (mountedRef.current) setOperationRevision((revision) => revision + 1);
  }, [operationStore]);

  const loadPage = useCallback(async (
    snapshot = access,
    afterProviderId: string | null = null,
    append = false,
  ): Promise<boolean> => {
    if (!snapshot || !isCurrent(snapshot)) return false;
    const generation = ++requestGeneration.current;
    if (!append) setProviders([]);
    setLoading(true);
    setLoadError(null);
    try {
      const page = await boundCall(snapshot, (token) =>
        gatewayClient.listPlatformByokProviderDrafts(
          token, PAGE_SIZE, afterProviderId, snapshot.tenant,
        ));
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return false;
      const rows = page.providers.map(requirePlatformProvider);
      setProviders((current) => append ? [...current, ...rows] : rows);
      setNextAfterProviderId(page.nextAfterProviderId);
      setSettingsRevision(page.settingsRevision);
      setMode(switchMode(page.switchMode));
      setActiveProviderId(page.activeProviderId);
      setForbidden(false);
      return true;
    } catch (reason) {
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return false;
      if (!handleAccessFailure(reason)) {
        setLoadError(reason);
      }
      return false;
    } finally {
      if (generation === requestGeneration.current && mountedRef.current) setLoading(false);
    }
  }, [access, boundCall, handleAccessFailure, isCurrent]);

  useEffect(() => {
    const timer = window.setTimeout(() => {
      if (access) void loadPage(access);
    }, 0);
    return () => window.clearTimeout(timer);
  }, [access, loadPage]);

  const switchPlatformSignIn = async (forceAuthorize = false) => {
    setAccessError(null);
    if (forceAuthorize) {
      await authorize(null);
      return;
    }
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
    const snapshot = access;
    const generation = requestGeneration.current;
    setSaveError(null);
    setCredentialActionError(null);
    try {
      const latest = await boundCall(snapshot, (token) =>
        gatewayClient.getPlatformByokProviderDraft(token, provider.id, snapshot.tenant));
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;
      const checked = requirePlatformProvider(latest);
      setEditingProvider(checked);
      setEditor(editorFromProvider(checked));
      setShowKey(false);
    } catch (reason) {
      if (generation === requestGeneration.current && isCurrent(snapshot) && !handleAccessFailure(reason))
        setLoadError(reason);
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

  const handleCredentialPutOutcome = useCallback((
    opKey: ProjectCredentialOperationKey | null,
    providerId: string,
    operationId: string,
    expectedProviderRevision: number,
    expectedCredentialRevision: number,
    checked: ModelProviderCredentialResponse,
    isRecheck: boolean,
    updateUi: boolean,
  ): ModelProviderCredentialResponse['status'] => {
    if (checked.status === 'published' ||
        checked.status === 'cancelled' ||
        checked.status === 'disabled') {
      if (opKey) deleteStoredOperation(opKey);
    } else if (opKey) {
      setStoredOperation(opKey, {
        operationId,
        expectedProviderRevision,
        expectedCredentialRevision,
        status: checked.status,
      });
    }
    if (!updateUi) return checked.status;
    setCredentialStatus({
      status: checked.status,
      providerId: checked.providerId,
      providerState: checked.providerState,
    });
    const credentialRevision = checked.credentialRevision;
    if (credentialRevision !== null) {
      setCredentialRevisions((current) => ({
        ...current,
        [providerId]: credentialRevision,
      }));
    }
    if (checked.status === 'published') {
      setNotice(isRecheck ? 'Saved key verified.' : 'Provider settings and key were saved.');
    } else if (checked.status === 'cancelled' || checked.status === 'disabled') {
      setNotice(`Key operation ${checked.status}.`);
    }
    return checked.status;
  }, [deleteStoredOperation, setStoredOperation]);

  const saveProvider = async (event: FormEvent) => {
    event.preventDefault();
    if (!editor || !access || !isCurrent(access)) return;
    const invalid = assertSaveReady(editor);
    if (invalid) {
      setSaveError(new Error(invalid));
      return;
    }
    const snapshot = access;
    const generation = requestGeneration.current;
    const source = editingProvider;
    const existingOpKey = source ? buildOperationKey(snapshot, source.id) : null;
    const hasUnresolvedOp = Boolean(existingOpKey && operationStore.get(existingOpKey));
    const metadataEditor: Editor = { ...editor, apiKey: '' };
    let pendingKey: string | null = editor.apiKey;
    clearPendingKeyRef.current = () => { pendingKey = null; };
    setSaving(true);
    setSaveError(null);
    setCredentialActionError(null);
    setNotice(null);
    setConfirmingResetProviderId(null);
    setEditor((current) => current ? { ...current, apiKey: '' } : current);
    setShowKey(false);
    const keyWasSubmitted = !hasUnresolvedOp && Boolean(pendingKey.trim());
    if (!keyWasSubmitted) {
      pendingKey = null;
      clearPendingKeyRef.current = null;
    }
    try {
      let rateCardId: string | null | undefined;
      let rateCardVersion: string | null | undefined;
      if (metadataEditor.pricingChoice === 'manual') {
        const saved = await saveManualPricing(snapshot, metadataEditor);
        rateCardId = saved.rateCardId;
        rateCardVersion = saved.rateCardVersion;
      } else if (metadataEditor.pricingChoice === 'saved') {
        rateCardId = metadataEditor.rateCardId.trim();
        rateCardVersion = metadataEditor.rateCardVersion.trim();
      } else {
        rateCardId = null;
        rateCardVersion = null;
      }
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;

      const promptCapacity = metadataEditor.promptCapacityTokens.trim()
        ? Number(metadataEditor.promptCapacityTokens)
        : null;
      const body: ProjectModelProviderDraftSaveRequest = {
        operationId: '',
        expectedRevision: source?.revision ?? 0,
        name: metadataEditor.name.trim(),
        type: metadataEditor.type,
        base_url: metadataEditor.baseUrl.trim(),
        model: metadataEditor.model.trim(),
        wire_api: metadataEditor.type === 'anthropic' ? null : metadataEditor.wireApi,
        azure_api_version: metadataEditor.type === 'azure' ? metadataEditor.azureApiVersion.trim() || null : null,
        reasoning_efforts: metadataEditor.reasoningEfforts.split(',').map((item) => item.trim()).filter(Boolean),
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
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;
      const checked = requirePlatformProvider(saved);

      if (keyWasSubmitted && pendingKey !== null) {
        const credentialOperationId = crypto.randomUUID();
        const opKey = buildOperationKey(snapshot, checked.id);
        const expectedProviderRevision = checked.revision;
        const expectedCredentialRevision = 0;
        let dispatched = false;
        try {
          const result = await apiCallOnce(
            (token, liveTenant) => {
              const apiKey = pendingKey;
              pendingKey = null;
              if (generation !== requestGeneration.current ||
                  !isCurrent(snapshot) ||
                  liveTenant !== snapshot.tenant ||
                  apiKey === null) {
                return Promise.reject(new StaleRequestError());
              }
              dispatched = true;
              if (opKey) {
                setStoredOperation(opKey, {
                  operationId: credentialOperationId,
                  expectedProviderRevision,
                  expectedCredentialRevision,
                  status: 'uncertain',
                });
              }
              return gatewayClient.setPlatformByokProviderCredential(
                token,
                checked.id,
                {
                  operationId: credentialOperationId,
                  expectedProviderRevision,
                  expectedCredentialRevision,
                  apiKey,
                },
                liveTenant,
              );
            },
            null,
            {
              tenantId: snapshot.tenant,
              actorId: snapshot.actorId,
              issuer: snapshot.issuer,
            },
          );
          if (!isSameConfirmedIdentity(snapshot)) return;
          if (opKey && operationStore.get(opKey)?.operationId !== credentialOperationId) return;
          const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
          const checkedCredential = requirePlatformCredentialResponse(
            result,
            checked.id,
            credentialOperationId,
          );
          handleCredentialPutOutcome(
            opKey,
            checked.id,
            credentialOperationId,
            expectedProviderRevision,
            expectedCredentialRevision,
            checkedCredential,
            false,
            canUpdateUi,
          );
          if (canUpdateUi) {
            closeEditor();
            await loadPage(snapshot);
          }
        } catch (reason) {
          if (reason instanceof StaleRequestError) return;
          if (!dispatched) {
            if (generation === requestGeneration.current && isCurrent(snapshot) && !handleAccessFailure(reason)) {
              setEditingProvider(checked);
              setSaveError(reason);
            }
            return;
          }
          if (!isSameConfirmedIdentity(snapshot)) return;
          if (opKey && operationStore.get(opKey)?.operationId !== credentialOperationId) return;
          const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
          if (isPreClaimCredentialRejection(reason, false)) {
            if (opKey) deleteStoredOperation(opKey);
            if (canUpdateUi) {
              setEditingProvider(checked);
              if (reason instanceof GatewayError && reason.status === 409 &&
                  reason.code === 'credential_slot_conflict') {
                setSaveError(new Error(
                  'This provider already has an existing or disabled credential slot and cannot accept a first key from this form.',
                ));
              } else {
                setSaveError(reason);
              }
            }
            return;
          }
          if (reason instanceof GatewayError && (reason.status === 401 || reason.status === 403)) {
            if (canUpdateUi) handleAccessFailure(reason);
            return;
          }
          if (reason instanceof GatewayError && reason.status === 503 &&
              reason.code === 'credential_publication_pending') {
            if (opKey) {
              setStoredOperation(opKey, {
                operationId: credentialOperationId,
                expectedProviderRevision,
                expectedCredentialRevision,
                status: 'publication_pending',
              });
            }
            if (canUpdateUi) {
              setCredentialStatus({
                status: 'publication_pending',
                providerId: checked.id,
                providerState: 'Draft',
              });
            }
          } else if (reason instanceof GatewayError && (
            (reason.status === 409 && reason.code === 'credential_write_quarantined') ||
            (reason.status === 503 && reason.code === 'credential_write_uncertain')
          )) {
            if (opKey) {
              setStoredOperation(opKey, {
                operationId: credentialOperationId,
                expectedProviderRevision,
                expectedCredentialRevision,
                status: 'quarantined',
              });
            }
            if (canUpdateUi) {
              setCredentialStatus({
                status: 'quarantined',
                providerId: checked.id,
                providerState: 'Draft',
              });
            }
          } else {
            if (opKey) {
              setStoredOperation(opKey, {
                operationId: credentialOperationId,
                expectedProviderRevision,
                expectedCredentialRevision,
                status: 'uncertain',
              });
            }
            if (canUpdateUi) setCredentialActionError(reason);
          }
          if (canUpdateUi) {
            closeEditor();
            await loadPage(snapshot);
          }
        }
      } else {
        setNotice(hasUnresolvedOp
          ? 'Provider settings were saved. The pending key operation remains active.'
          : 'Provider settings were saved. Add a key before this provider can become Available.');
        closeEditor();
        await loadPage(snapshot);
      }
    } catch (reason) {
      if (generation !== requestGeneration.current || !isCurrent(snapshot)) return;
      if (handleAccessFailure(reason)) return;
      setSaveError(reason);
      if (reason instanceof GatewayError && reason.status === 409)
        setNotice('The provider changed while it was open. Close it and reload the latest revision.');
    } finally {
      pendingKey = null;
      clearPendingKeyRef.current = null;
      if (mountedRef.current) setSaving(false);
    }
  };

  const replayCredential = async (
    providerId: string,
    op: ProjectCredentialOperation,
  ) => {
    if (!access || !isCurrent(access) ||
        op.status !== 'publication_pending' || op.stalePublicationConflict)
      return;
    const snapshot = access;
    const generation = requestGeneration.current;
    const opKey = buildOperationKey(snapshot, providerId);
    const expectedCredentialRevision = op.expectedCredentialRevision ?? 0;
    setSaving(true);
    setCredentialActionError(null);
    setConfirmingResetProviderId(null);
    let dispatched = false;
    try {
      const result = await apiCallOnce(
        (token, liveTenant) => {
          if (generation !== requestGeneration.current ||
              !isCurrent(snapshot) ||
              liveTenant !== snapshot.tenant)
            return Promise.reject(new StaleRequestError());
          const liveOp = opKey ? operationStore.get(opKey) : null;
          if (opKey && (!liveOp || liveOp.operationId !== op.operationId ||
              liveOp.status !== 'publication_pending' || liveOp.stalePublicationConflict))
            return Promise.reject(new StaleRequestError());
          dispatched = true;
          if (opKey) {
            setStoredOperation(opKey, {
              ...op,
              expectedCredentialRevision,
              status: 'uncertain',
            });
          }
          return gatewayClient.setPlatformByokProviderCredential(
            token,
            providerId,
            {
              operationId: op.operationId,
              expectedProviderRevision: op.expectedProviderRevision,
              expectedCredentialRevision,
              recheckExistingVersion: true,
            },
            liveTenant,
          );
        },
        null,
        {
          tenantId: snapshot.tenant,
          actorId: snapshot.actorId,
          issuer: snapshot.issuer,
        },
      );
      if (!isSameConfirmedIdentity(snapshot)) return;
      if (opKey && operationStore.get(opKey)?.operationId !== op.operationId) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      const checked = requirePlatformCredentialResponse(result, providerId, op.operationId);
      handleCredentialPutOutcome(
        opKey,
        providerId,
        op.operationId,
        op.expectedProviderRevision,
        expectedCredentialRevision,
        checked,
        false,
        canUpdateUi,
      );
      if (canUpdateUi) await loadPage(snapshot);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!dispatched) {
        if (generation === requestGeneration.current && isCurrent(snapshot) && !handleAccessFailure(reason))
          setCredentialActionError(reason);
        return;
      }
      if (!isSameConfirmedIdentity(snapshot)) return;
      if (opKey && operationStore.get(opKey)?.operationId !== op.operationId) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      if (reason instanceof GatewayError && (reason.status === 401 || reason.status === 403)) {
        if (canUpdateUi) handleAccessFailure(reason);
        return;
      }
      if (reason instanceof GatewayError && reason.status === 409 &&
          reason.code === 'model_provider_draft_conflict') {
        if (opKey) {
          setStoredOperation(opKey, {
            ...op,
            expectedCredentialRevision,
            status: 'publication_pending',
            stalePublicationConflict: true,
          });
        }
        return;
      }
      if (reason instanceof GatewayError && reason.status === 503 &&
          reason.code === 'credential_publication_pending') {
        if (opKey) {
          setStoredOperation(opKey, {
            ...op,
            expectedCredentialRevision,
            status: 'publication_pending',
          });
        }
        return;
      }
      if (reason instanceof GatewayError && (
        (reason.status === 409 && reason.code === 'credential_write_quarantined') ||
        (reason.status === 503 && reason.code === 'credential_write_uncertain')
      )) {
        if (opKey) {
          setStoredOperation(opKey, {
            ...op,
            expectedCredentialRevision,
            status: 'quarantined',
          });
        }
        return;
      }
      if (opKey) {
        setStoredOperation(opKey, {
          ...op,
          expectedCredentialRevision,
          status: 'uncertain',
        });
      }
      if (canUpdateUi) setCredentialActionError(reason);
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  };

  const checkKeyStatus = async (
    providerId: string,
    op: ProjectCredentialOperation,
  ) => {
    if (!access || !isCurrent(access)) return;
    const snapshot = access;
    const generation = requestGeneration.current;
    const opKey = buildOperationKey(snapshot, providerId);
    const expectedCredentialRevision = op.expectedCredentialRevision ?? 0;
    setSaving(true);
    setCredentialActionError(null);
    setConfirmingResetProviderId(null);
    try {
      const status = await apiCallOnce(
        (token, liveTenant) => {
          if (generation !== requestGeneration.current ||
              !isCurrent(snapshot) ||
              liveTenant !== snapshot.tenant)
            return Promise.reject(new StaleRequestError());
          return gatewayClient.getPlatformByokProviderCredentialOperation(
            token,
            providerId,
            op.operationId,
            liveTenant,
          );
        },
        null,
        {
          tenantId: snapshot.tenant,
          actorId: snapshot.actorId,
          issuer: snapshot.issuer,
        },
      );
      if (!isSameConfirmedIdentity(snapshot)) return;
      const liveOp = opKey ? operationStore.get(opKey) : null;
      if (opKey && (!liveOp || liveOp.operationId !== op.operationId)) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      const checked = requirePlatformCredentialOperationStatus(
        status,
        providerId,
        op.operationId,
      );
      if (checked.status === 'published' ||
          checked.status === 'cancelled' ||
          checked.status === 'disabled') {
        if (opKey) deleteStoredOperation(opKey);
        if (canUpdateUi) {
          setNotice(checked.status === 'published'
            ? 'API key saved.'
            : `Key operation ${checked.status}.`);
          await loadPage(snapshot);
        }
        return;
      }
      if (opKey) {
        setStoredOperation(opKey, {
          operationId: op.operationId,
          expectedProviderRevision: op.expectedProviderRevision,
          expectedCredentialRevision,
          status: checked.status,
          ...((liveOp?.stalePublicationConflict || op.stalePublicationConflict)
            ? { stalePublicationConflict: true }
            : {}),
        });
      }
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!isSameConfirmedIdentity(snapshot)) return;
      const liveOp = opKey ? operationStore.get(opKey) : null;
      if (opKey && (!liveOp || liveOp.operationId !== op.operationId)) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      if (reason instanceof GatewayError &&
          reason.status === 404 &&
          reason.code === 'credential_operation_not_found') {
        // 404 credential_operation_not_found is always nonterminal because claim creation can lag.
        if (opKey) {
          setStoredOperation(opKey, {
            operationId: op.operationId,
            expectedProviderRevision: op.expectedProviderRevision,
            expectedCredentialRevision,
            status: 'pending',
            ...((liveOp?.stalePublicationConflict || op.stalePublicationConflict)
              ? { stalePublicationConflict: true }
              : {}),
          });
        }
        return;
      }
      if (canUpdateUi && !handleAccessFailure(reason)) setCredentialActionError(reason);
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  };

  const confirmResetPendingOperation = async (
    providerId: string,
    op: ProjectCredentialOperation,
  ) => {
    if (!access || !isCurrent(access) ||
        (op.status !== 'uncertain' && op.status !== 'pending'))
      return;
    const snapshot = access;
    const generation = requestGeneration.current;
    const opKey = buildOperationKey(snapshot, providerId);
    setSaving(true);
    setCredentialActionError(null);
    try {
      const latest = await boundCall(snapshot, (token) =>
        gatewayClient.getPlatformByokProviderDraft(token, providerId, snapshot.tenant));
      if (generation !== requestGeneration.current || !isCurrent(snapshot))
        return;
      const checked = requirePlatformProvider(latest);
      setProviders((current) => current.map((item) => item.id === providerId ? checked : item));
      if (opKey) deleteStoredOperation(opKey);
      setConfirmingResetProviderId(null);
    } catch (reason) {
      if (generation === requestGeneration.current && isCurrent(snapshot) && !handleAccessFailure(reason))
        setCredentialActionError(reason);
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  };

  const recheckSavedKey = async (provider: PlatformModelProviderDraft) => {
    const credentialRevision = credentialRevisions[provider.id];
    if (!access || !isCurrent(access) || credentialRevision === undefined) return;
    const snapshot = access;
    const generation = requestGeneration.current;
    const opKey = buildOperationKey(snapshot, provider.id);
    const operationId = crypto.randomUUID();
    const expectedProviderRevision = provider.revision;
    const expectedCredentialRevision = credentialRevision;
    setSaving(true);
    setCredentialActionError(null);
    setConfirmingResetProviderId(null);
    let dispatched = false;
    try {
      const result = await apiCallOnce(
        (token, liveTenant) => {
          if (generation !== requestGeneration.current ||
              !isCurrent(snapshot) ||
              liveTenant !== snapshot.tenant)
            return Promise.reject(new StaleRequestError());
          dispatched = true;
          if (opKey) {
            setStoredOperation(opKey, {
              operationId,
              expectedProviderRevision,
              expectedCredentialRevision,
              status: 'uncertain',
            });
          }
          return gatewayClient.setPlatformByokProviderCredential(
            token,
            provider.id,
            {
              operationId,
              expectedProviderRevision,
              expectedCredentialRevision,
              recheckExistingVersion: true,
            },
            liveTenant,
          );
        },
        null,
        {
          tenantId: snapshot.tenant,
          actorId: snapshot.actorId,
          issuer: snapshot.issuer,
        },
      );
      if (!isSameConfirmedIdentity(snapshot)) return;
      if (opKey && operationStore.get(opKey)?.operationId !== operationId) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      const checked = requirePlatformCredentialResponse(result, provider.id, operationId);
      handleCredentialPutOutcome(
        opKey,
        provider.id,
        operationId,
        expectedProviderRevision,
        expectedCredentialRevision,
        checked,
        true,
        canUpdateUi,
      );
      if (canUpdateUi) await loadPage(snapshot);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!dispatched) {
        if (generation === requestGeneration.current && isCurrent(snapshot) && !handleAccessFailure(reason))
          setCredentialActionError(reason);
        return;
      }
      if (!isSameConfirmedIdentity(snapshot)) return;
      if (opKey && operationStore.get(opKey)?.operationId !== operationId) return;
      const canUpdateUi = generation === requestGeneration.current && isCurrent(snapshot);
      if (isPreClaimCredentialRejection(reason, true)) {
        if (opKey) deleteStoredOperation(opKey);
        if (canUpdateUi) setCredentialActionError(reason);
        return;
      }
      if (reason instanceof GatewayError && (reason.status === 401 || reason.status === 403)) {
        if (canUpdateUi) handleAccessFailure(reason);
        return;
      }
      if (reason instanceof GatewayError && reason.status === 503 &&
          reason.code === 'credential_publication_pending') {
        if (opKey) {
          setStoredOperation(opKey, {
            operationId,
            expectedProviderRevision,
            expectedCredentialRevision,
            status: 'publication_pending',
          });
        }
        return;
      }
      if (reason instanceof GatewayError && (
        (reason.status === 409 && reason.code === 'credential_write_quarantined') ||
        (reason.status === 503 && reason.code === 'credential_write_uncertain')
      )) {
        if (opKey) {
          setStoredOperation(opKey, {
            operationId,
            expectedProviderRevision,
            expectedCredentialRevision,
            status: 'quarantined',
          });
        }
        return;
      }
      if (opKey) {
        setStoredOperation(opKey, {
          operationId,
          expectedProviderRevision,
          expectedCredentialRevision,
          status: 'uncertain',
        });
      }
      if (canUpdateUi) setCredentialActionError(reason);
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  };

  const switchDefault = async (provider?: PlatformModelProviderDraft) => {
    if (!access || settingsRevision === null || settingsRevision <= 0) return;
    const snapshot = access;
    const selected = provider ? provider.id : 'copilot';
    setSwitching(selected);
    setLoadError(null);
    try {
      await boundCall(snapshot, (token) => provider
        ? gatewayClient.activatePlatformByokProvider(token, provider.id, {
          expectedSettingsRevision: settingsRevision,
          expectedProviderRevision: provider.revision,
        }, snapshot.tenant)
        : gatewayClient.deactivatePlatformByokProvider(token, {
          expectedSettingsRevision: settingsRevision,
        }, snapshot.tenant));
      if (!isCurrent(snapshot)) return;
      await loadPage(snapshot);
    } catch (reason) {
      if (!isCurrent(snapshot)) return;
      if (handleAccessFailure(reason)) return;
      setLoadError(reason);
      if (reason instanceof GatewayError && reason.status === 409)
        await loadPage(snapshot);
    } finally {
      if (mountedRef.current) setSwitching(null);
    }
  };

  const editingOperation = useMemo(() => {
    void operationRevision;
    if (!access || !editingProvider) return null;
    const key = buildOperationKey(access, editingProvider.id);
    return key ? operationStore.get(key) : null;
  }, [access, buildOperationKey, editingProvider, operationRevision, operationStore]);

  const contextConfirmed = authorizationContext !== null && (
    !access || (
      authorizationContext.tenantId === access.tenant &&
      (authorizationContext.actorId == null || access.actorId === null || authorizationContext.actorId === access.actorId) &&
      (authorizationContext.issuer == null || access.issuer === null || authorizationContext.issuer === access.issuer)
    )
  );
  const controlsDisabled = forbidden || !access || !contextConfirmed || loading || saving;
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
      {accessError != null && (
        <div className="v1-stack">
          <ErrorNotice>{errorMessage(accessError)}</ErrorNotice>
          {accessError instanceof GatewayError && accessError.status === 401 && (
            <Button onClick={() => void switchPlatformSignIn(true)}>
              {session ? 'Switch to platform sign-in' : 'Sign in'}
            </Button>
          )}
        </div>
      )}
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
      {access && contextConfirmed && !forbidden && (
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
                void operationRevision;
                const opKey = buildOperationKey(access, provider.id);
                const op = opKey ? operationStore.get(opKey) : null;
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
                      {op && (
                        <div className="v1-stack">
                          {op.status === 'uncertain' && (
                            <Text role="status">
                              The key write did not finish. Check key status before trying again. Operation ID:{' '}
                              <code>{op.operationId}</code>
                            </Text>
                          )}
                          {op.status === 'pending' && (
                            <Text role="status">
                              The key operation is still pending. Check key status again or share this operation ID with an operator:{' '}
                              <code>{op.operationId}</code>
                            </Text>
                          )}
                          {op.status === 'publication_pending' && !op.stalePublicationConflict && (
                            <Text role="status">
                              Replay uses the original operation and does not send the key again. Operation ID:{' '}
                              <code>{op.operationId}</code>
                            </Text>
                          )}
                          {op.status === 'publication_pending' && op.stalePublicationConflict && (
                            <Text role="status">
                              The provider changed while publication was pending. Share this operation ID with an operator for recovery:{' '}
                              <code>{op.operationId}</code>
                            </Text>
                          )}
                          {op.status === 'disable_pending' && (
                            <Text role="status">
                              Credential disable is pending. Check key status until it finishes. Operation ID:{' '}
                              <code>{op.operationId}</code>
                            </Text>
                          )}
                          {op.status === 'quarantined' && (
                            <MessageBar intent="warning"><MessageBarBody>
                              Recovery handoff: give the platform operator operation ID
                              {' '}<code>{op.operationId}</code> and recover the stored
                              secret version with the original expected revisions.
                            </MessageBarBody></MessageBar>
                          )}
                          <div className="v1-actions">
                            {op.status === 'publication_pending' && !op.stalePublicationConflict && (
                              <Button onClick={() => void replayCredential(provider.id, op)} disabled={controlsDisabled}>
                                Retry publication
                              </Button>
                            )}
                            {(op.status === 'uncertain' ||
                              op.status === 'pending' ||
                              op.status === 'publication_pending' ||
                              op.status === 'disable_pending') && (
                              <Button onClick={() => void checkKeyStatus(provider.id, op)} disabled={controlsDisabled}>
                                Check key status
                              </Button>
                            )}
                            {(op.status === 'uncertain' || op.status === 'pending') &&
                              confirmingResetProviderId !== provider.id && (
                              <Button onClick={() => setConfirmingResetProviderId(provider.id)} disabled={controlsDisabled}>
                                Reset pending check
                              </Button>
                            )}
                          </div>
                          {confirmingResetProviderId === provider.id &&
                            (op.status === 'uncertain' || op.status === 'pending') && (
                            <div className="v1-actions">
                              <Text role="status">
                                Do not reset unless an operator confirmed this attempt is closed. Resetting reloads the provider before allowing a new key.
                              </Text>
                              <Button onClick={() => void confirmResetPendingOperation(provider.id, op)} disabled={controlsDisabled}>
                                Confirm reset
                              </Button>
                              <Button onClick={() => setConfirmingResetProviderId(null)} disabled={controlsDisabled}>
                                Cancel reset
                              </Button>
                            </div>
                          )}
                        </div>
                      )}
                    </div>
                    <div className="v1-actions">
                      {provider.state === 'Draft' && (
                        <Button icon={<EditRegular />} onClick={() => void startEdit(provider)}
                          disabled={controlsDisabled}>Edit</Button>
                      )}
                      {provider.state === 'Draft' && provider.hasCredential &&
                        credentialRevisions[provider.id] !== undefined && !op && (
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
          {credentialActionError != null &&
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

      {access && contextConfirmed && !forbidden && pickerOpen && (
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

      {access && contextConfirmed && !forbidden && editor && (
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

          {editingOperation ? (
            <MessageBar intent="warning"><MessageBarBody>
              Key operation is {editingOperation.status.replaceAll('_', ' ')} (operation ID:{' '}
              <code>{editingOperation.operationId}</code>). New key entry is blocked until this operation is resolved. You can still save metadata changes.
            </MessageBarBody></MessageBar>
          ) : (
            <Field label="API key" hint="The key stays in this form only. Leave it blank to keep the saved key.">
              <Input aria-label="API key" type={showKey ? 'text' : 'password'} autoComplete="new-password"
                value={editor.apiKey} disabled={saving}
                onChange={(_, data) => updateEditor('apiKey', data.value)}
                contentAfter={<Button appearance="transparent" size="small"
                  aria-label={showKey ? 'Hide API key' : 'Show API key'}
                  icon={showKey ? <EyeOff24Regular /> : <Eye24Regular />}
                  onClick={() => setShowKey((current) => !current)} />} />
            </Field>
          )}
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
