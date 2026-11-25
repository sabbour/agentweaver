import { Badge, Button, Field, Input, Text } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type {
  ModelProviderCredentialOperationStatusResponse,
  ModelProviderCredentialResponse,
  ModelProviderEgressStatus,
  ProjectModelProviderDraft,
  ProjectModelProviderDraftSaveRequest,
} from './contracts';
import { errorMessage } from './errors';
import { Loading, ErrorNotice } from './pageParts';
import {
  createProjectCredentialOperationStore,
  type ProjectCredentialOperation,
  type ProjectCredentialOperationKey,
} from './projectCredentialOperations';

type ProviderEditor = {
  name: string;
  type: string;
  baseUrl: string;
  model: string;
  wireApi: string;
  azureApiVersion: string;
  reasoningEfforts: string;
  promptCapacityTokens: string;
};

type ProviderView = 'list' | 'detail' | 'create' | 'edit';

type ScopeSnapshot = {
  projectId: string;
  tenantId: string | null;
  actorId: string | null;
  issuer: string | null;
  authSessionEpoch: number;
  scopeGeneration: number;
};

type PendingSave = {
  fingerprint: string;
  operationId: string;
  projectId: string;
  tenantId: string | null;
  actorId: string | null;
  issuer: string | null;
};

class StaleRequestError extends Error {
  constructor() {
    super('The request scope changed before completion.');
  }
}

const PAGE_SIZE = 50;

function emptyEditor(): ProviderEditor {
  return {
    name: '',
    type: '',
    baseUrl: '',
    model: '',
    wireApi: '',
    azureApiVersion: '',
    reasoningEfforts: '',
    promptCapacityTokens: '',
  };
}

function editorFromDraft(provider: ProjectModelProviderDraft): ProviderEditor {
  return {
    name: provider.name,
    type: provider.type,
    baseUrl: provider.base_url,
    model: provider.model,
    wireApi: provider.wire_api ?? '',
    azureApiVersion: provider.azure_api_version ?? '',
    reasoningEfforts: provider.reasoning_efforts.join(', '),
    promptCapacityTokens: provider.prompt_capacity_tokens?.toString() ?? '',
  };
}

function requireProjectDraft(
  provider: ProjectModelProviderDraft,
  projectId: string,
  expectedProviderId?: string,
): ProjectModelProviderDraft {
  if (provider.scope !== 'project' || provider.projectId !== projectId || !provider.id)
    throw new Error('The service returned a provider draft for a different project.');
  if (expectedProviderId !== undefined && provider.id !== expectedProviderId)
    throw new Error('The service returned a different provider draft.');
  return provider;
}

function requireProjectCredentialResponse(
  response: ModelProviderCredentialResponse,
  projectId: string,
  providerId: string,
  expectedOperationId?: string,
): ModelProviderCredentialResponse {
  if (response.scope !== 'project' ||
      response.projectId !== projectId ||
      response.providerId !== providerId ||
      (expectedOperationId !== undefined && response.operationId !== expectedOperationId)) {
    throw new Error('The service returned a credential response for a different scope or provider.');
  }
  return response;
}

function requireProjectCredentialOperationStatus(
  response: ModelProviderCredentialOperationStatusResponse,
  projectId: string,
  providerId: string,
  expectedOperationId: string,
): ModelProviderCredentialOperationStatusResponse {
  if (response.scope !== 'project' ||
      response.projectId !== projectId ||
      response.providerId !== providerId ||
      response.operationId !== expectedOperationId) {
    throw new Error('The service returned a credential operation status for a different scope or provider.');
  }
  return response;
}

function isPreClaimCredentialRejection(reason: unknown): boolean {
  if (!(reason instanceof GatewayError)) return false;
  if (reason.status === 400 || reason.status === 413 || reason.status === 415) return true;
  if (reason.status === 404 && reason.code === 'model_provider_draft_not_found') return true;
  if (reason.status === 409 && reason.code === 'credential_slot_conflict') return true;
  if (reason.status === 503 && reason.code === 'credential_vault_unavailable') return true;
  return false;
}

function hostRequestStatusLabel(egress?: ModelProviderEgressStatus | null): string {
  if (!egress) return 'Host request status unavailable';
  if (egress.deliveryStatus === 'pending' && egress.destinationState === null)
    return 'Waiting to send host request';
  if (egress.deliveryStatus === 'delivered' && egress.destinationState === 'requested')
    return 'Waiting for platform admin approval';
  if (egress.deliveryStatus === 'failed' && egress.destinationState === null)
    return 'Host request delivery failed';
  return 'Host request status unavailable';
}

function hostRequestHostText(egress?: ModelProviderEgressStatus | null): string {
  return egress && typeof egress.host === 'string' && egress.host.trim().length > 0
    ? egress.host
    : 'Unavailable';
}

function hostRequestPortText(egress?: ModelProviderEgressStatus | null): string {
  return egress && Number.isSafeInteger(egress.port) && egress.port > 0
    ? String(egress.port)
    : 'Unavailable';
}

/** Lists and edits project provider metadata through the exact project-only Broker session. */
export function ProjectModelProviderDrafts({
  projectId,
  tenantSelector,
}: {
  projectId: string;
  tenantSelector?: string | null;
}) {
  const {
    apiCall,
    apiCallOnce,
    projectCredentialOperations,
    session,
    authSessionGeneration,
    authSessionEpoch = 0,
    authorizationContext,
    authorizationContextError,
    authorizationContextLoading,
    getAuthorizationContextError,
    authorize,
    ensureSession,
    resolveAuthorizationContext,
    busy,
    configurationError,
  } = useAuth();
  const [fallbackOperationStore] = useState(() => createProjectCredentialOperationStore());
  const operationStore = projectCredentialOperations ?? fallbackOperationStore;
  const binding = useMemo(() => ({ projectId }), [projectId]);
  const [resolvedTenant, setResolvedTenant] = useState<string | null>(null);
  const selectedTenant = tenantSelector ?? resolvedTenant;
  const [accessState, setAccessState] = useState<'checking' | 'ready' | 'needs-sign-in' | 'error'>('checking');
  const [accessError, setAccessError] = useState<unknown>(null);
  const [providers, setProviders] = useState<ProjectModelProviderDraft[]>([]);
  const [nextAfterProviderId, setNextAfterProviderId] = useState<string | null>(null);
  const [listLoading, setListLoading] = useState(false);
  const [listError, setListError] = useState<unknown>(null);
  const [view, setView] = useState<ProviderView>('list');
  const [selectedProvider, setSelectedProvider] = useState<ProjectModelProviderDraft | null>(null);
  const [selectedProviderId, setSelectedProviderId] = useState<string | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<unknown>(null);
  const [editor, setEditor] = useState<ProviderEditor>(emptyEditor);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<unknown>(null);
  const [saveConflict, setSaveConflict] = useState(false);
  const [savedMessage, setSavedMessage] = useState<string | null>(null);
  const [apiKeyInput, setApiKeyInput] = useState('');
  const [showKey, setShowKey] = useState(false);
  const [credentialBusy, setCredentialBusy] = useState(false);
  const [credentialError, setCredentialError] = useState<unknown>(null);
  const [credentialMessage, setCredentialMessage] = useState<string | null>(null);
  const [confirmingOperationReset, setConfirmingOperationReset] = useState(false);
  const [operationRevision, setOperationRevision] = useState(0);

  const mountedRef = useRef(true);
  const pendingSave = useRef<PendingSave | null>(null);
  const listNeedsReload = useRef(false);
  const scopeGeneration = useRef(0);
  const listGeneration = useRef(0);
  const detailGeneration = useRef(0);
  const saveGeneration = useRef(0);
  const credentialGeneration = useRef(0);
  const accessCheckGeneration = useRef(0);
  const activeViewRef = useRef<ProviderView>(view);
  const activeProviderIdRef = useRef<string | null>(selectedProviderId);
  const observedSessionGeneration = useRef(authSessionGeneration);
  const observedSessionEpoch = useRef(authSessionEpoch);
  const authSessionEpochRef = useRef(authSessionEpoch);
  const accessCheckInFlightRef = useRef(false);
  const lastProjectIdRef = useRef<string | null>(null);
  const lastTenantSelectorRef = useRef<string | null>(tenantSelector ?? null);
  const lastConfirmedIdentityRef = useRef<{
    projectId: string;
    tenantId: string;
    actorId: string | null;
    issuer: string | null;
  } | null>(null);
  const [confirmedScope, setConfirmedScope] = useState<{
    projectId: string;
    tenantId: string | null;
    actorId: string | null;
    issuer: string | null;
  } | null>(null);

  const projectSessionActive = session?.binding?.projectId === projectId && !session.binding.runId;
  const scopeMatchesConfirmed = confirmedScope !== null &&
    confirmedScope.projectId === projectId &&
    (selectedTenant == null || confirmedScope.tenantId === selectedTenant) &&
    (!authorizationContext || (
      confirmedScope.tenantId === authorizationContext.tenantId &&
      confirmedScope.actorId === (authorizationContext.actorId ?? null) &&
      confirmedScope.issuer === (authorizationContext.issuer ?? null)
    ));
  const projectContextReady = projectSessionActive &&
    authorizationContext !== null &&
    (selectedTenant == null || authorizationContext.tenantId === selectedTenant) &&
    scopeMatchesConfirmed;

  const currentScopeRef = useRef<ScopeSnapshot>({
    projectId,
    tenantId: selectedTenant ?? null,
    actorId: null,
    issuer: null,
    authSessionEpoch,
    scopeGeneration: 0,
  });

  useEffect(() => {
    activeViewRef.current = view;
    activeProviderIdRef.current = selectedProviderId;
    authSessionEpochRef.current = authSessionEpoch;
    currentScopeRef.current = {
      projectId,
      tenantId: selectedTenant ?? null,
      actorId: authorizationContext?.actorId ?? lastConfirmedIdentityRef.current?.actorId ?? null,
      issuer: authorizationContext?.issuer ?? lastConfirmedIdentityRef.current?.issuer ?? null,
      authSessionEpoch,
      scopeGeneration: scopeGeneration.current,
    };
  }, [authSessionEpoch, authorizationContext, projectId, selectedProviderId, selectedTenant, view]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      scopeGeneration.current += 1;
      listGeneration.current += 1;
      detailGeneration.current += 1;
      saveGeneration.current += 1;
      credentialGeneration.current += 1;
      accessCheckGeneration.current += 1;
    };
  }, []);

  const isSameConfirmedScope = useCallback((captured: ScopeSnapshot): boolean => {
    const current = currentScopeRef.current;
    return (
      current.scopeGeneration === captured.scopeGeneration &&
      current.projectId === captured.projectId &&
      current.tenantId === captured.tenantId &&
      current.actorId === captured.actorId &&
      current.issuer === captured.issuer &&
      current.authSessionEpoch === captured.authSessionEpoch
    );
  }, []);

  const isSameScope = useCallback((captured: ScopeSnapshot): boolean => {
    return mountedRef.current && isSameConfirmedScope(captured);
  }, [isSameConfirmedScope]);

  const clearUiStateForAccessLoss = useCallback(() => {
    listGeneration.current += 1;
    detailGeneration.current += 1;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    activeViewRef.current = 'list';
    activeProviderIdRef.current = null;
    listNeedsReload.current = false;
    setProviders([]);
    setNextAfterProviderId(null);
    setListLoading(false);
    setListError(null);
    setView('list');
    setSelectedProvider(null);
    setSelectedProviderId(null);
    setDetailLoading(false);
    setDetailError(null);
    setEditor(emptyEditor());
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
  }, []);

  const invalidateScopeGenerations = useCallback(() => {
    scopeGeneration.current += 1;
    currentScopeRef.current = {
      ...currentScopeRef.current,
      scopeGeneration: scopeGeneration.current,
    };
    listGeneration.current += 1;
    detailGeneration.current += 1;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    accessCheckGeneration.current += 1;
    pendingSave.current = null;
  }, []);

  const handleAccessFailure = useCallback((reason: unknown): boolean => {
    if (!(reason instanceof GatewayError) || (reason.status !== 401 && reason.status !== 403))
      return false;
    clearUiStateForAccessLoss();
    setAccessError(reason);
    setAccessState(reason.status === 401 ? 'needs-sign-in' : 'error');
    return true;
  }, [clearUiStateForAccessLoss]);

  const loadPage = useCallback(async (
    afterProviderId: string | null = null,
    append = false,
    tenant = selectedTenant,
  ) => {
    const generation = ++listGeneration.current;
    const capturedScope: ScopeSnapshot = {
      ...currentScopeRef.current,
      tenantId: tenant ?? currentScopeRef.current.tenantId,
    };
    if (!append) {
      setProviders([]);
      setNextAfterProviderId(null);
    }
    setListLoading(true);
    setListError(null);
    try {
      const page = await apiCall(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              (capturedScope.tenantId !== null && liveTenant !== null && liveTenant !== capturedScope.tenantId))
            return Promise.reject(new StaleRequestError());
          return gatewayClient.listProjectByokProviderDrafts(
            token, projectId, PAGE_SIZE, afterProviderId, capturedScope.tenantId,
          );
        },
        binding,
      );
      if (!isSameScope(capturedScope) || generation !== listGeneration.current) return;
      const pageProviders = page.providers.map((provider) => requireProjectDraft(provider, projectId));
      setProviders((current) => append ? [...current, ...pageProviders] : pageProviders);
      setNextAfterProviderId(page.nextAfterProviderId);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (isSameScope(capturedScope) && generation === listGeneration.current) {
        if (!handleAccessFailure(reason)) {
          setProviders([]);
          setNextAfterProviderId(null);
          setListError(reason);
        }
      }
    } finally {
      if (mountedRef.current && generation === listGeneration.current)
        setListLoading(false);
    }
  }, [apiCall, binding, handleAccessFailure, isSameScope, projectId, selectedTenant]);

  const prepareAccess = useCallback(async (
    interactive: boolean,
    tenantOverride?: string | null,
  ): Promise<string | null> => {
    const generation = ++accessCheckGeneration.current;
    const targetTenant = tenantOverride !== undefined ? tenantOverride : selectedTenant;
    const startProjectId = projectId;
    accessCheckInFlightRef.current = interactive;
    try {
      if (interactive) {
        await authorize(binding);
      } else if (!await ensureSession(binding, true)) {
        if (mountedRef.current &&
            lastProjectIdRef.current === startProjectId &&
            generation === accessCheckGeneration.current) {
          clearUiStateForAccessLoss();
          setAccessState('needs-sign-in');
        }
        return null;
      }
      const context = await resolveAuthorizationContext(targetTenant ?? undefined);
      if (!context) {
        throw getAuthorizationContextError?.() ??
          authorizationContextError ??
          new Error('Could not confirm access to this project. Try again.');
      }
      if (!mountedRef.current ||
          lastProjectIdRef.current !== startProjectId ||
          generation !== accessCheckGeneration.current)
        return null;

      const confirmedActor = (context as { actorId?: string | null }).actorId ?? null;
      const confirmedIssuer = (context as { issuer?: string | null }).issuer ?? null;
      const previous = lastConfirmedIdentityRef.current;
      if (previous && (
        previous.projectId !== startProjectId ||
        previous.tenantId !== context.tenantId ||
        previous.actorId !== confirmedActor ||
        previous.issuer !== confirmedIssuer
      )) {
        pendingSave.current = null;
        clearUiStateForAccessLoss();
      }
      lastConfirmedIdentityRef.current = {
        projectId: startProjectId,
        tenantId: context.tenantId,
        actorId: confirmedActor,
        issuer: confirmedIssuer,
      };
      setConfirmedScope({
        projectId: startProjectId,
        tenantId: context.tenantId,
        actorId: confirmedActor,
        issuer: confirmedIssuer,
      });
      currentScopeRef.current = {
        ...currentScopeRef.current,
        tenantId: context.tenantId,
        actorId: confirmedActor,
        issuer: confirmedIssuer,
        authSessionEpoch: authSessionEpochRef.current,
      };
      setResolvedTenant(context.tenantId);
      setAccessState('ready');
      return context.tenantId;
    } catch (reason) {
      if (!mountedRef.current ||
          lastProjectIdRef.current !== startProjectId ||
          generation !== accessCheckGeneration.current)
        return null;
      clearUiStateForAccessLoss();
      setAccessError(reason);
      setAccessState(reason instanceof GatewayError && reason.status === 401 ? 'needs-sign-in' : 'error');
      return null;
    } finally {
      if (interactive && generation === accessCheckGeneration.current)
        accessCheckInFlightRef.current = false;
    }
  }, [
    authorizationContextError,
    authorize,
    binding,
    clearUiStateForAccessLoss,
    ensureSession,
    getAuthorizationContextError,
    projectId,
    resolveAuthorizationContext,
    selectedTenant,
  ]);

  const initialize = useCallback(async (tenantOverride?: string | null) => {
    const tenant = await prepareAccess(false, tenantOverride);
    if (tenant) await loadPage(null, false, tenant);
  }, [loadPage, prepareAccess]);

  useEffect(() => {
    const nextTenantSelector = tenantSelector ?? null;
    const projectChanged = lastProjectIdRef.current !== projectId;
    const tenantSelectorChanged =
      nextTenantSelector !== null &&
      nextTenantSelector !== lastTenantSelectorRef.current &&
      nextTenantSelector !== resolvedTenant;
    const confirmedActor = authorizationContext?.actorId ?? null;
    const confirmedIssuer = authorizationContext?.issuer ?? null;
    const confirmedIdentityChanged = Boolean(
      authorizationContext &&
      lastConfirmedIdentityRef.current &&
      (lastConfirmedIdentityRef.current.projectId !== projectId ||
        lastConfirmedIdentityRef.current.tenantId !== authorizationContext.tenantId ||
        lastConfirmedIdentityRef.current.actorId !== confirmedActor ||
        lastConfirmedIdentityRef.current.issuer !== confirmedIssuer),
    );

    if (projectChanged) {
      lastProjectIdRef.current = projectId;
      lastTenantSelectorRef.current = nextTenantSelector;
      lastConfirmedIdentityRef.current = null;
      invalidateScopeGenerations();
      currentScopeRef.current = {
        ...currentScopeRef.current,
        projectId,
        tenantId: nextTenantSelector,
      };
      queueMicrotask(() => {
        if (!mountedRef.current || lastProjectIdRef.current !== projectId) return;
        clearUiStateForAccessLoss();
        setConfirmedScope(null);
        setResolvedTenant(null);
        setAccessError(null);
        setAccessState('checking');
        void initialize(nextTenantSelector);
      });
      return;
    }

    if (tenantSelectorChanged) {
      lastTenantSelectorRef.current = nextTenantSelector;
      lastConfirmedIdentityRef.current = null;
      invalidateScopeGenerations();
      currentScopeRef.current = {
        ...currentScopeRef.current,
        tenantId: nextTenantSelector,
      };
      queueMicrotask(() => {
        if (!mountedRef.current || lastProjectIdRef.current !== projectId) return;
        clearUiStateForAccessLoss();
        setConfirmedScope(null);
        setResolvedTenant(nextTenantSelector);
        setAccessError(null);
        setAccessState('checking');
        void initialize(nextTenantSelector);
      });
      return;
    }

    lastTenantSelectorRef.current = nextTenantSelector;

    if (confirmedIdentityChanged && authorizationContext) {
      lastConfirmedIdentityRef.current = {
        projectId,
        tenantId: authorizationContext.tenantId,
        actorId: confirmedActor,
        issuer: confirmedIssuer,
      };
      invalidateScopeGenerations();
      currentScopeRef.current = {
        ...currentScopeRef.current,
        tenantId: authorizationContext.tenantId,
        actorId: confirmedActor,
        issuer: confirmedIssuer,
      };
      queueMicrotask(() => {
        if (!mountedRef.current || lastProjectIdRef.current !== projectId) return;
        clearUiStateForAccessLoss();
        setConfirmedScope({
          projectId,
          tenantId: authorizationContext.tenantId,
          actorId: confirmedActor,
          issuer: confirmedIssuer,
        });
        setResolvedTenant(authorizationContext.tenantId);
        setAccessError(null);
        if (projectSessionActive) {
          setAccessState('ready');
          void loadPage(null, false, authorizationContext.tenantId);
        } else {
          setAccessState('checking');
          void initialize(authorizationContext.tenantId);
        }
      });
    }
  }, [
    authorizationContext,
    clearUiStateForAccessLoss,
    initialize,
    invalidateScopeGenerations,
    loadPage,
    projectId,
    projectSessionActive,
    resolvedTenant,
    tenantSelector,
  ]);

  useEffect(() => {
    const epochChanged = observedSessionEpoch.current !== authSessionEpoch;
    const generationChanged = observedSessionGeneration.current !== authSessionGeneration;
    if (!epochChanged && !generationChanged) return;
    observedSessionEpoch.current = authSessionEpoch;
    observedSessionGeneration.current = authSessionGeneration;

    if (epochChanged) {
      if (accessCheckInFlightRef.current) {
        currentScopeRef.current = {
          ...currentScopeRef.current,
          authSessionEpoch,
        };
        return;
      }
      invalidateScopeGenerations();
      currentScopeRef.current = {
        ...currentScopeRef.current,
        authSessionEpoch,
      };
      queueMicrotask(() => {
        if (!mountedRef.current) return;
        clearUiStateForAccessLoss();
        setConfirmedScope(null);
        setAccessError(null);
        setAccessState('checking');
        void initialize(selectedTenant);
      });
      return;
    }

    if (!projectSessionActive) {
      queueMicrotask(() => {
        if (!mountedRef.current) return;
        clearUiStateForAccessLoss();
        if (!authorizationContextLoading)
          void initialize(selectedTenant);
      });
      return;
    }

    if (accessState !== 'ready' || projectContextReady || authorizationContextLoading)
      return;

    // Same-session token renewal: reconfirm the tenant without invalidating active detail/save work.
    queueMicrotask(() => {
      if (!mountedRef.current) return;
      void prepareAccess(false, selectedTenant);
    });
  }, [
    accessState,
    authSessionEpoch,
    authSessionGeneration,
    authorizationContextLoading,
    clearUiStateForAccessLoss,
    initialize,
    invalidateScopeGenerations,
    prepareAccess,
    projectContextReady,
    projectSessionActive,
    selectedTenant,
  ]);

  const retryAccess = () => {
    setAccessError(null);
    setAccessState('checking');
    void initialize();
  };

  const signInForProject = async () => {
    setAccessError(null);
    setAccessState('checking');
    const tenant = await prepareAccess(true);
    if (tenant) await loadPage(null, false, tenant);
  };

  const openProvider = useCallback(async (
    providerId: string,
    options?: { preserveSavedMessage?: boolean; preserveCredentialMessage?: boolean },
  ) => {
    const generation = ++detailGeneration.current;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    const capturedScope = currentScopeRef.current;
    activeViewRef.current = 'detail';
    activeProviderIdRef.current = providerId;
    setView('detail');
    setSelectedProviderId(providerId);
    setSelectedProvider(null);
    setDetailLoading(true);
    setDetailError(null);
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setConfirmingOperationReset(false);
    if (!options?.preserveCredentialMessage) setCredentialMessage(null);
    if (!options?.preserveSavedMessage) setSavedMessage(null);
    try {
      const provider = await apiCall(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              (capturedScope.tenantId !== null && liveTenant !== null && liveTenant !== capturedScope.tenantId))
            return Promise.reject(new StaleRequestError());
          return gatewayClient.getProjectByokProviderDraft(
            token, projectId, providerId, capturedScope.tenantId,
          );
        },
        binding,
      );
      if (!isSameScope(capturedScope) ||
          generation !== detailGeneration.current ||
          activeViewRef.current !== 'detail' ||
          activeProviderIdRef.current !== providerId)
        return;
      const checked = requireProjectDraft(provider, projectId, providerId);
      setSelectedProvider(checked);
      setProviders((current) => current.map((item) => item.id === providerId ? checked : item));
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (isSameScope(capturedScope) &&
          generation === detailGeneration.current &&
          activeViewRef.current === 'detail' &&
          activeProviderIdRef.current === providerId &&
          !handleAccessFailure(reason))
        setDetailError(reason);
    } finally {
      if (mountedRef.current && generation === detailGeneration.current)
        setDetailLoading(false);
    }
  }, [apiCall, binding, handleAccessFailure, isSameScope, projectId]);

  const goBackToList = () => {
    detailGeneration.current += 1;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    activeViewRef.current = 'list';
    activeProviderIdRef.current = null;
    setView('list');
    setSelectedProvider(null);
    setSelectedProviderId(null);
    setDetailLoading(false);
    setDetailError(null);
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    if (listNeedsReload.current) {
      listNeedsReload.current = false;
      void loadPage(null, false, selectedTenant);
    }
  };

  const cancelEdit = () => {
    saveGeneration.current += 1;
    detailGeneration.current += 1;
    credentialGeneration.current += 1;
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setEditor(emptyEditor());
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    if (selectedProvider) {
      activeViewRef.current = 'detail';
      activeProviderIdRef.current = selectedProvider.id;
      setView('detail');
    } else {
      activeViewRef.current = 'list';
      activeProviderIdRef.current = null;
      setView('list');
      setSelectedProviderId(null);
      if (listNeedsReload.current) {
        listNeedsReload.current = false;
        void loadPage(null, false, selectedTenant);
      }
    }
  };

  const startCreate = () => {
    detailGeneration.current += 1;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    activeViewRef.current = 'create';
    activeProviderIdRef.current = null;
    setSelectedProvider(null);
    setSelectedProviderId(null);
    setEditor(emptyEditor());
    setDetailLoading(false);
    setDetailError(null);
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    setView('create');
  };

  const startEdit = () => {
    if (!selectedProvider || detailLoading) return;
    saveGeneration.current += 1;
    credentialGeneration.current += 1;
    activeViewRef.current = 'edit';
    activeProviderIdRef.current = selectedProvider.id;
    setEditor(editorFromDraft(selectedProvider));
    setSaving(false);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    setApiKeyInput('');
    setShowKey(false);
    setCredentialBusy(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    setView('edit');
  };

  const saveDraft = async (event: FormEvent) => {
    event.preventDefault();
    if (![editor.name, editor.type, editor.baseUrl, editor.model].every((value) => value.trim())) {
      setSaveError(new Error('Enter a name, provider type, base URL, and model.'));
      return;
    }
    const promptCapacity = editor.promptCapacityTokens.trim()
      ? Number(editor.promptCapacityTokens)
      : null;
    if (promptCapacity !== null && (!Number.isSafeInteger(promptCapacity) || promptCapacity <= 0)) {
      setSaveError(new Error('Prompt capacity must be a positive whole number.'));
      return;
    }
    const requestBody: Omit<ProjectModelProviderDraftSaveRequest, 'operationId'> = {
      expectedRevision: view === 'edit' ? selectedProvider?.revision ?? 0 : 0,
      name: editor.name.trim(),
      type: editor.type.trim(),
      base_url: editor.baseUrl.trim(),
      model: editor.model.trim(),
      wire_api: editor.wireApi.trim() || null,
      azure_api_version: editor.azureApiVersion.trim() || null,
      reasoning_efforts: editor.reasoningEfforts.split(',').map((effort) => effort.trim()).filter(Boolean),
      prompt_capacity_tokens: promptCapacity,
    };
    const isUpdate = view === 'edit' && selectedProvider !== null;
    const expectedSaveView = view;
    const expectedProviderId = isUpdate ? selectedProvider.id : null;
    const capturedScope = currentScopeRef.current;
    const fingerprint = JSON.stringify({
      projectId,
      providerId: expectedProviderId,
      requestBody,
    });
    const canReusePending =
      pendingSave.current !== null &&
      pendingSave.current.fingerprint === fingerprint &&
      pendingSave.current.projectId === capturedScope.projectId &&
      pendingSave.current.tenantId === capturedScope.tenantId &&
      pendingSave.current.actorId === capturedScope.actorId &&
      pendingSave.current.issuer === capturedScope.issuer;
    const operationId = canReusePending
      ? pendingSave.current!.operationId
      : crypto.randomUUID();
    pendingSave.current = {
      fingerprint,
      operationId,
      projectId: capturedScope.projectId,
      tenantId: capturedScope.tenantId,
      actorId: capturedScope.actorId,
      issuer: capturedScope.issuer,
    };
    const request: ProjectModelProviderDraftSaveRequest = { operationId, ...requestBody };
    const generation = ++saveGeneration.current;

    setSaving(true);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    try {
      const saved = await apiCall(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              generation !== saveGeneration.current ||
              activeViewRef.current !== expectedSaveView ||
              activeProviderIdRef.current !== expectedProviderId ||
              (capturedScope.tenantId !== null && liveTenant !== null && liveTenant !== capturedScope.tenantId))
            return Promise.reject(new StaleRequestError());
          return isUpdate && expectedProviderId
            ? gatewayClient.updateProjectByokProviderDraft(
              token, projectId, expectedProviderId, request, capturedScope.tenantId,
            )
            : gatewayClient.createProjectByokProviderDraft(
              token, projectId, request, capturedScope.tenantId,
            );
        },
        binding,
      );
      if (!isSameScope(capturedScope) ||
          generation !== saveGeneration.current ||
          activeViewRef.current !== expectedSaveView ||
          activeProviderIdRef.current !== expectedProviderId)
        return;
      const checked = requireProjectDraft(saved, projectId, expectedProviderId ?? undefined);
      pendingSave.current = null;
      listNeedsReload.current = true;
      setSaving(false);
      setSavedMessage('Provider draft saved.');
      void openProvider(checked.id, { preserveSavedMessage: true });
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (isSameScope(capturedScope) &&
          generation === saveGeneration.current &&
          activeViewRef.current === expectedSaveView &&
          activeProviderIdRef.current === expectedProviderId) {
        if (!handleAccessFailure(reason)) {
          setSaveError(reason);
          setSaveConflict(isUpdate && reason instanceof GatewayError && reason.status === 409);
        }
      }
    } finally {
      if (mountedRef.current && generation === saveGeneration.current)
        setSaving(false);
    }
  };

  const updateEditor = (field: keyof ProviderEditor, value: string) => {
    setEditor((current) => ({ ...current, [field]: value }));
    setSaveError(null);
  };

  const buildOperationKey = useCallback((
    providerId: string,
    scope: { tenantId: string | null; actorId: string | null; issuer: string | null } | null = confirmedScope,
  ): ProjectCredentialOperationKey | null => {
    if (!scope?.issuer || !scope.actorId || !scope.tenantId) return null;
    return {
      issuer: scope.issuer,
      actorId: scope.actorId,
      tenantId: scope.tenantId,
      projectId,
      providerId,
    };
  }, [confirmedScope, projectId]);

  const setStoredOperation = useCallback((
    key: ProjectCredentialOperationKey,
    operation: ProjectCredentialOperation,
  ) => {
    operationStore.set(key, operation);
    setOperationRevision((revision) => revision + 1);
  }, [operationStore]);

  const deleteStoredOperation = useCallback((key: ProjectCredentialOperationKey) => {
    operationStore.delete(key);
    setOperationRevision((revision) => revision + 1);
  }, [operationStore]);

  const activeOperationKey = useMemo(() => {
    if (view !== 'detail' || !selectedProvider) return null;
    return buildOperationKey(selectedProvider.id);
  }, [buildOperationKey, selectedProvider, view]);

  const activeCredentialOp = useMemo(() => {
    if (!activeOperationKey) return null;
    void operationRevision;
    return operationStore.get(activeOperationKey);
  }, [activeOperationKey, operationRevision, operationStore]);

  const handleCredentialPutOutcome = useCallback((
    opKey: ProjectCredentialOperationKey,
    operationId: string,
    expectedProviderRevision: number,
    response: ModelProviderCredentialResponse,
    updateUi: boolean,
  ) => {
    if (response.status === 'published' ||
        response.status === 'cancelled' ||
        response.status === 'disabled') {
      operationStore.delete(opKey);
      if (mountedRef.current) setOperationRevision((revision) => revision + 1);
      if (response.status === 'published' && mountedRef.current)
        listNeedsReload.current = true;
      if (!updateUi) return;
      if (response.status === 'published') {
        setCredentialMessage('API key saved.');
      } else {
        setCredentialMessage(`Key operation ${response.status}.`);
      }
      void openProvider(opKey.providerId, {
        preserveSavedMessage: Boolean(savedMessage),
        preserveCredentialMessage: true,
      });
      return;
    }
    if (response.status === 'publication_pending') {
      operationStore.set(opKey, {
        operationId,
        expectedProviderRevision,
        status: 'publication_pending',
      });
      if (mountedRef.current) setOperationRevision((revision) => revision + 1);
      if (updateUi) setCredentialMessage(null);
      return;
    }
    if (response.status === 'quarantined') {
      operationStore.set(opKey, {
        operationId,
        expectedProviderRevision,
        status: 'quarantined',
      });
      if (mountedRef.current) setOperationRevision((revision) => revision + 1);
      if (updateUi) setCredentialMessage(null);
    }
  }, [openProvider, operationStore, savedMessage]);

  const submitFirstKey = async (event: FormEvent) => {
    event.preventDefault();
    const submittedApiKey = apiKeyInput;
    setApiKeyInput('');
    setShowKey(false);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);

    if (!selectedProvider || selectedProvider.hasCredential || activeCredentialOp) return;
    if (!submittedApiKey.trim()) {
      setCredentialError(new Error('Enter an API key.'));
      return;
    }
    const opKey = buildOperationKey(selectedProvider.id);
    if (!opKey) {
      setCredentialError(new GatewayError(
        403,
        { code: 'forbidden' },
        'Confirm project access before saving an API key.',
      ));
      return;
    }

    const providerId = selectedProvider.id;
    const expectedProviderRevision = selectedProvider.revision;
    const operationId = crypto.randomUUID();
    const capturedScope = currentScopeRef.current;
    const generation = ++credentialGeneration.current;
    let dispatched = false;

    setCredentialBusy(true);
    try {
      const response = await apiCallOnce(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              generation !== credentialGeneration.current ||
              activeViewRef.current !== 'detail' ||
              activeProviderIdRef.current !== providerId ||
              liveTenant !== opKey.tenantId) {
            return Promise.reject(new StaleRequestError());
          }
          dispatched = true;
          return gatewayClient.setProjectByokProviderCredential(
            token,
            projectId,
            providerId,
            {
              operationId,
              expectedProviderRevision,
              expectedCredentialRevision: 0,
              apiKey: submittedApiKey,
            },
            liveTenant,
          );
        },
        binding,
        {
          tenantId: opKey.tenantId,
          actorId: opKey.actorId,
          issuer: opKey.issuer,
        },
      );
      if (!isSameConfirmedScope(capturedScope)) return;
      const checked = requireProjectCredentialResponse(response, projectId, providerId, operationId);
      const canUpdateUi =
        mountedRef.current &&
        generation === credentialGeneration.current &&
        activeViewRef.current === 'detail' &&
        activeProviderIdRef.current === providerId;
      handleCredentialPutOutcome(opKey, operationId, expectedProviderRevision, checked, canUpdateUi);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!dispatched) {
        if (isSameScope(capturedScope) && generation === credentialGeneration.current) {
          if (!handleAccessFailure(reason))
            setCredentialError(reason);
        }
        return;
      }
      if (!isSameConfirmedScope(capturedScope)) return;
      const canUpdateUi =
        mountedRef.current &&
        generation === credentialGeneration.current &&
        activeViewRef.current === 'detail' &&
        activeProviderIdRef.current === providerId;
      if (isPreClaimCredentialRejection(reason)) {
        if (canUpdateUi) {
          if (reason instanceof GatewayError && reason.status === 409 &&
              reason.code === 'credential_slot_conflict') {
            setCredentialError(new Error(
              'This provider already has an existing or disabled credential slot and cannot accept a first key from this form.',
            ));
          } else {
            setCredentialError(reason);
          }
        }
        return;
      }
      if (reason instanceof GatewayError && (reason.status === 401 || reason.status === 403)) {
        if (isSameScope(capturedScope) && generation === credentialGeneration.current)
          handleAccessFailure(reason);
        return;
      }
      if (reason instanceof GatewayError && reason.status === 503 &&
          reason.code === 'credential_publication_pending') {
        setStoredOperation(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'publication_pending',
        });
        return;
      }
      if (reason instanceof GatewayError && (
        (reason.status === 409 && reason.code === 'credential_write_quarantined') ||
        (reason.status === 503 && reason.code === 'credential_write_uncertain')
      )) {
        setStoredOperation(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'quarantined',
        });
        return;
      }
      // 409 model_provider_draft_conflict, network error, timeout, or unexpected 5xx:
      setStoredOperation(opKey, {
        operationId,
        expectedProviderRevision,
        status: 'uncertain',
      });
      if (canUpdateUi)
        setCredentialError(reason);
    } finally {
      if (mountedRef.current && generation === credentialGeneration.current)
        setCredentialBusy(false);
    }
  };

  const checkKeyStatus = async () => {
    if (!selectedProvider || !activeCredentialOp || !activeOperationKey) return;
    const opKey = activeOperationKey;
    const providerId = selectedProvider.id;
    const { operationId, expectedProviderRevision } = activeCredentialOp;
    const capturedScope = currentScopeRef.current;
    const generation = ++credentialGeneration.current;

    setCredentialBusy(true);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    try {
      const status = await apiCallOnce(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              generation !== credentialGeneration.current ||
              activeViewRef.current !== 'detail' ||
              activeProviderIdRef.current !== providerId ||
              liveTenant !== opKey.tenantId) {
            return Promise.reject(new StaleRequestError());
          }
          return gatewayClient.getProjectByokProviderCredentialOperation(
            token,
            projectId,
            providerId,
            operationId,
            liveTenant,
          );
        },
        binding,
        {
          tenantId: opKey.tenantId,
          actorId: opKey.actorId,
          issuer: opKey.issuer,
        },
      );
      if (!isSameConfirmedScope(capturedScope)) return;
      const checked = requireProjectCredentialOperationStatus(
        status,
        projectId,
        providerId,
        operationId,
      );
      const canUpdateUi =
        mountedRef.current &&
        generation === credentialGeneration.current &&
        activeViewRef.current === 'detail' &&
        activeProviderIdRef.current === providerId;
      if (checked.status === 'published' ||
          checked.status === 'cancelled' ||
          checked.status === 'disabled') {
        operationStore.delete(opKey);
        if (mountedRef.current) setOperationRevision((revision) => revision + 1);
        if (checked.status === 'published' && mountedRef.current)
          listNeedsReload.current = true;
        if (!canUpdateUi) return;
        if (checked.status === 'published') {
          setCredentialMessage('API key saved.');
        } else {
          setCredentialMessage(`Key operation ${checked.status}.`);
        }
        void openProvider(providerId, {
          preserveSavedMessage: Boolean(savedMessage),
          preserveCredentialMessage: true,
        });
        return;
      }
      operationStore.set(opKey, {
        operationId,
        expectedProviderRevision,
        status: checked.status,
      });
      if (mountedRef.current) setOperationRevision((revision) => revision + 1);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!isSameConfirmedScope(capturedScope)) return;
      if (reason instanceof GatewayError &&
          reason.status === 404 &&
          reason.code === 'credential_operation_not_found') {
        // 404 credential_operation_not_found is always nonterminal because claim creation can lag.
        operationStore.set(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'pending',
        });
        if (mountedRef.current) setOperationRevision((revision) => revision + 1);
        return;
      }
      if (isSameScope(capturedScope) && generation === credentialGeneration.current) {
        if (!handleAccessFailure(reason))
          setCredentialError(reason);
      }
    } finally {
      if (mountedRef.current && generation === credentialGeneration.current)
        setCredentialBusy(false);
    }
  };

  const retryPublication = async () => {
    if (!selectedProvider || !activeCredentialOp || !activeOperationKey ||
        activeCredentialOp.status !== 'publication_pending' ||
        activeCredentialOp.stalePublicationConflict)
      return;
    const opKey = activeOperationKey;
    const providerId = selectedProvider.id;
    const { operationId, expectedProviderRevision } = activeCredentialOp;
    const capturedScope = currentScopeRef.current;
    const generation = ++credentialGeneration.current;

    setCredentialBusy(true);
    setCredentialError(null);
    setCredentialMessage(null);
    setConfirmingOperationReset(false);
    try {
      const response = await apiCallOnce(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              generation !== credentialGeneration.current ||
              activeViewRef.current !== 'detail' ||
              activeProviderIdRef.current !== providerId ||
              liveTenant !== opKey.tenantId) {
            return Promise.reject(new StaleRequestError());
          }
          return gatewayClient.setProjectByokProviderCredential(
            token,
            projectId,
            providerId,
            {
              operationId,
              expectedProviderRevision,
              expectedCredentialRevision: 0,
              recheckExistingVersion: true,
            },
            liveTenant,
          );
        },
        binding,
        {
          tenantId: opKey.tenantId,
          actorId: opKey.actorId,
          issuer: opKey.issuer,
        },
      );
      if (!isSameConfirmedScope(capturedScope)) return;
      const checked = requireProjectCredentialResponse(response, projectId, providerId, operationId);
      const canUpdateUi =
        mountedRef.current &&
        generation === credentialGeneration.current &&
        activeViewRef.current === 'detail' &&
        activeProviderIdRef.current === providerId;
      handleCredentialPutOutcome(opKey, operationId, expectedProviderRevision, checked, canUpdateUi);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (!isSameConfirmedScope(capturedScope)) return;
      if (reason instanceof GatewayError && reason.status === 409 &&
          reason.code === 'model_provider_draft_conflict') {
        operationStore.set(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'publication_pending',
          stalePublicationConflict: true,
        });
        if (mountedRef.current) setOperationRevision((revision) => revision + 1);
        return;
      }
      if (reason instanceof GatewayError && reason.status === 503 &&
          reason.code === 'credential_publication_pending') {
        operationStore.set(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'publication_pending',
        });
        if (mountedRef.current) setOperationRevision((revision) => revision + 1);
        return;
      }
      if (reason instanceof GatewayError && (
        (reason.status === 409 && reason.code === 'credential_write_quarantined') ||
        (reason.status === 503 && reason.code === 'credential_write_uncertain')
      )) {
        operationStore.set(opKey, {
          operationId,
          expectedProviderRevision,
          status: 'quarantined',
        });
        if (mountedRef.current) setOperationRevision((revision) => revision + 1);
        return;
      }
      if (isSameScope(capturedScope) && generation === credentialGeneration.current) {
        if (!handleAccessFailure(reason))
          setCredentialError(reason);
      }
    } finally {
      if (mountedRef.current && generation === credentialGeneration.current)
        setCredentialBusy(false);
    }
  };

  const confirmResetPendingOperation = async () => {
    if (!selectedProvider || !activeCredentialOp || !activeOperationKey ||
        (activeCredentialOp.status !== 'uncertain' && activeCredentialOp.status !== 'pending'))
      return;
    const opKey = activeOperationKey;
    const providerId = selectedProvider.id;
    const capturedScope = currentScopeRef.current;
    const generation = ++credentialGeneration.current;

    setCredentialBusy(true);
    setCredentialError(null);
    setCredentialMessage(null);
    try {
      const latest = await apiCall(
        (token, liveTenant) => {
          if (!isSameScope(capturedScope) ||
              generation !== credentialGeneration.current ||
              activeViewRef.current !== 'detail' ||
              activeProviderIdRef.current !== providerId ||
              (capturedScope.tenantId !== null && liveTenant !== null && liveTenant !== capturedScope.tenantId)) {
            return Promise.reject(new StaleRequestError());
          }
          return gatewayClient.getProjectByokProviderDraft(
            token,
            projectId,
            providerId,
            capturedScope.tenantId,
          );
        },
        binding,
      );
      if (!isSameScope(capturedScope) ||
          generation !== credentialGeneration.current ||
          activeViewRef.current !== 'detail' ||
          activeProviderIdRef.current !== providerId)
        return;
      const checked = requireProjectDraft(latest, projectId, providerId);
      setSelectedProvider(checked);
      setProviders((current) => current.map((item) => item.id === providerId ? checked : item));
      deleteStoredOperation(opKey);
      setConfirmingOperationReset(false);
    } catch (reason) {
      if (reason instanceof StaleRequestError) return;
      if (isSameScope(capturedScope) && generation === credentialGeneration.current) {
        if (!handleAccessFailure(reason))
          setCredentialError(reason);
      }
    } finally {
      if (mountedRef.current && generation === credentialGeneration.current)
        setCredentialBusy(false);
    }
  };

  const accessFailure = accessState === 'error' && (
    <>
      <ErrorNotice>{errorMessage(accessError)}</ErrorNotice>
      <Button onClick={retryAccess}>
        Retry
      </Button>
    </>
  );

  return (
    <PageSection
      title="BYOK provider drafts"
      description="Manage provider metadata and add an initial API key for this project."
    >
      {accessState === 'checking' || (accessState === 'ready' && !projectContextReady)
        ? <Loading label="Checking project access…" />
        : null}
      {accessState === 'needs-sign-in' && (
        <div className="v1-stack">
          <Text role="status">Sign in for access to this project’s provider drafts.</Text>
          {accessError != null && <ErrorNotice>{errorMessage(accessError)}</ErrorNotice>}
          <Button appearance="primary" onClick={() => void signInForProject()} disabled={busy}>
            Sign in for project access
          </Button>
          {configurationError && <ErrorNotice>{configurationError}</ErrorNotice>}
        </div>
      )}
      {accessFailure}
      {accessState === 'ready' && projectContextReady && (
        <div className="v1-stack">
          {view === 'list' && (
            <>
              <div className="v1-actions">
                <Button appearance="primary" onClick={startCreate}>Add provider draft</Button>
                <Button onClick={() => void loadPage()}>Refresh</Button>
              </div>
              {listError != null && (
                <>
                  <ErrorNotice>{errorMessage(listError)}</ErrorNotice>
                  <Button onClick={() => void loadPage()}>Retry</Button>
                </>
              )}
              {listLoading && <Loading label="Loading provider drafts…" />}
              {!listLoading && listError == null && providers.length === 0 && (
                <Text role="status">No provider drafts are set up for this project.</Text>
              )}
              {providers.length > 0 && (
                <ul className="v1-permission-list" aria-label="Project provider drafts">
                  {providers.map((provider) => (
                    <li key={provider.id}>
                      <Button appearance="subtle" onClick={() => void openProvider(provider.id)}>
                        {provider.name}
                      </Button>
                      <span>{provider.type} · {provider.state}</span>
                      <Badge appearance="tint" color={provider.hasCredential ? 'success' : 'subtle'}>
                        {provider.hasCredential ? 'Key saved' : 'No key saved'}
                      </Badge>
                    </li>
                  ))}
                </ul>
              )}
              {nextAfterProviderId && (
                <Button onClick={() => void loadPage(nextAfterProviderId, true)} disabled={listLoading}>
                  Load more
                </Button>
              )}
            </>
          )}
          {view === 'detail' && (
            <>
              <div className="v1-actions">
                <Button onClick={goBackToList}>Back to provider drafts</Button>
                <Button
                  onClick={() => selectedProviderId && void openProvider(selectedProviderId)}
                  disabled={!selectedProviderId || detailLoading}
                >
                  Refresh status
                </Button>
                <Button appearance="primary" onClick={startEdit} disabled={!selectedProvider || detailLoading}>
                  Edit metadata
                </Button>
              </div>
              {savedMessage && <Text role="status">{savedMessage}</Text>}
              {detailLoading && <Loading label="Loading provider draft…" />}
              {detailError != null && (
                <>
                  <ErrorNotice>{errorMessage(detailError)}</ErrorNotice>
                  <Button
                    onClick={() => selectedProviderId && void openProvider(selectedProviderId, {
                      preserveSavedMessage: Boolean(savedMessage),
                    })}
                    disabled={detailLoading}
                  >
                    Retry
                  </Button>
                </>
              )}
              {selectedProvider && (
                <>
                  <dl className="v1-detail-list">
                    <div><dt>Provider ID</dt><dd><code>{selectedProvider.id}</code></dd></div>
                    <div><dt>Name</dt><dd>{selectedProvider.name}</dd></div>
                    <div><dt>Type</dt><dd>{selectedProvider.type}</dd></div>
                    <div><dt>Base URL</dt><dd>{selectedProvider.base_url}</dd></div>
                    <div><dt>Model</dt><dd>{selectedProvider.model}</dd></div>
                    <div><dt>Wire API</dt><dd>{selectedProvider.wire_api ?? 'Not set'}</dd></div>
                    <div><dt>Azure API version</dt><dd>{selectedProvider.azure_api_version ?? 'Not set'}</dd></div>
                    <div><dt>Reasoning efforts</dt><dd>{selectedProvider.reasoning_efforts.join(', ') || 'Not set'}</dd></div>
                    <div><dt>Prompt capacity</dt><dd>{selectedProvider.prompt_capacity_tokens ?? 'Not set'}</dd></div>
                    <div><dt>State</dt><dd>{selectedProvider.state}</dd></div>
                    <div><dt>Revision</dt><dd>{selectedProvider.revision}</dd></div>
                    <div>
                      <dt>Credential</dt>
                      <dd>
                        <Badge appearance="tint" color={selectedProvider.hasCredential ? 'success' : 'subtle'}>
                          {selectedProvider.hasCredential ? 'Key saved' : 'No key saved'}
                        </Badge>
                      </dd>
                    </div>
                    <div><dt>Host request status</dt><dd>{hostRequestStatusLabel(selectedProvider.egress)}</dd></div>
                    <div><dt>Host request host</dt><dd>{hostRequestHostText(selectedProvider.egress)}</dd></div>
                    <div><dt>Host request port</dt><dd>{hostRequestPortText(selectedProvider.egress)}</dd></div>
                  </dl>
                  {credentialMessage && <Text role="status">{credentialMessage}</Text>}
                  {credentialError != null && <ErrorNotice>{errorMessage(credentialError)}</ErrorNotice>}
                  {!selectedProvider.hasCredential && !activeCredentialOp && (
                    <form className="v1-form" onSubmit={(event) => void submitFirstKey(event)}>
                      <Field label="Add API key" required>
                        <div className="v1-actions">
                          <Input
                            type={showKey ? 'text' : 'password'}
                            autoComplete="new-password"
                            value={apiKeyInput}
                            onChange={(_, data) => {
                              setApiKeyInput(data.value);
                              setCredentialError(null);
                              setCredentialMessage(null);
                            }}
                            disabled={credentialBusy}
                          />
                          <Button
                            type="button"
                            onClick={() => setShowKey((current) => !current)}
                            disabled={credentialBusy}
                          >
                            {showKey ? 'Hide key' : 'Show key'}
                          </Button>
                        </div>
                      </Field>
                      <div className="v1-actions">
                        <Button
                          appearance="primary"
                          type="submit"
                          disabled={credentialBusy || !apiKeyInput.trim()}
                        >
                          {credentialBusy ? 'Saving key…' : 'Save key'}
                        </Button>
                      </div>
                    </form>
                  )}
                  {!selectedProvider.hasCredential && activeCredentialOp && (
                    <div className="v1-stack">
                      {activeCredentialOp.status === 'uncertain' && (
                        <Text role="status">
                          The key write did not finish. Check key status before trying again. Operation ID:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      {activeCredentialOp.status === 'pending' && (
                        <Text role="status">
                          The key operation is still pending. Check key status again or share this operation ID with an operator:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      {activeCredentialOp.status === 'publication_pending' && !activeCredentialOp.stalePublicationConflict && (
                        <Text role="status">
                          The key was stored, but provider publication is pending. Retry publication without re-entering the key. Operation ID:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      {activeCredentialOp.status === 'publication_pending' && activeCredentialOp.stalePublicationConflict && (
                        <Text role="status">
                          The provider changed while publication was pending. Share this operation ID with an operator for recovery:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      {activeCredentialOp.status === 'disable_pending' && (
                        <Text role="status">
                          Credential disable is pending. Check key status until it finishes. Operation ID:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      {activeCredentialOp.status === 'quarantined' && (
                        <Text role="status">
                          The key write was quarantined. Share this operation ID with an operator for recovery:{' '}
                          <code>{activeCredentialOp.operationId}</code>
                        </Text>
                      )}
                      <div className="v1-actions">
                        {activeCredentialOp.status === 'publication_pending' && !activeCredentialOp.stalePublicationConflict && (
                          <Button
                            appearance="primary"
                            type="button"
                            onClick={() => void retryPublication()}
                            disabled={credentialBusy}
                          >
                            Retry publication
                          </Button>
                        )}
                        {(activeCredentialOp.status === 'uncertain' ||
                          activeCredentialOp.status === 'pending' ||
                          (activeCredentialOp.status === 'publication_pending' && !activeCredentialOp.stalePublicationConflict) ||
                          activeCredentialOp.status === 'disable_pending') && (
                          <Button
                            type="button"
                            onClick={() => void checkKeyStatus()}
                            disabled={credentialBusy}
                          >
                            Check key status
                          </Button>
                        )}
                        {(activeCredentialOp.status === 'uncertain' || activeCredentialOp.status === 'pending') &&
                          !confirmingOperationReset && (
                          <Button
                            type="button"
                            onClick={() => setConfirmingOperationReset(true)}
                            disabled={credentialBusy}
                          >
                            Reset pending check
                          </Button>
                        )}
                      </div>
                      {confirmingOperationReset && activeOperationKey &&
                        (activeCredentialOp.status === 'uncertain' || activeCredentialOp.status === 'pending') && (
                        <div className="v1-actions">
                          <Text role="status">
                            Do not reset unless an operator confirmed this attempt is closed. Resetting reloads the provider before allowing a new key.
                          </Text>
                          <Button
                            type="button"
                            onClick={() => void confirmResetPendingOperation()}
                            disabled={credentialBusy}
                          >
                            Confirm reset
                          </Button>
                          <Button
                            type="button"
                            onClick={() => setConfirmingOperationReset(false)}
                            disabled={credentialBusy}
                          >
                            Cancel reset
                          </Button>
                        </div>
                      )}
                    </div>
                  )}
                </>
              )}
            </>
          )}
          {(view === 'create' || view === 'edit') && (
            <form className="v1-form" onSubmit={(event) => void saveDraft(event)}>
              <div className="v1-actions">
                <Button type="button" onClick={cancelEdit}>
                  Cancel
                </Button>
              </div>
              {saveError != null && <ErrorNotice>{errorMessage(saveError)}</ErrorNotice>}
              {saveConflict && selectedProvider && (
                <Button
                  type="button"
                  onClick={() => void openProvider(selectedProvider.id)}
                  disabled={saving}
                >
                  Discard edits and reload latest
                </Button>
              )}
              <Field label="Name" required>
                <Input value={editor.name} maxLength={128} required onChange={(_, data) => updateEditor('name', data.value)} disabled={saving} />
              </Field>
              <Field label="Provider type" required>
                <Input value={editor.type} required onChange={(_, data) => updateEditor('type', data.value)} disabled={saving} />
              </Field>
              <Field label="Base URL" required>
                <Input type="url" value={editor.baseUrl} required onChange={(_, data) => updateEditor('baseUrl', data.value)} disabled={saving} />
              </Field>
              <Field label="Model or deployment" required>
                <Input value={editor.model} required onChange={(_, data) => updateEditor('model', data.value)} disabled={saving} />
              </Field>
              <Field label="Wire API">
                <Input value={editor.wireApi} onChange={(_, data) => updateEditor('wireApi', data.value)} disabled={saving} />
              </Field>
              <Field label="Azure API version">
                <Input value={editor.azureApiVersion} onChange={(_, data) => updateEditor('azureApiVersion', data.value)} disabled={saving} />
              </Field>
              <Field label="Reasoning efforts" hint="Separate values with commas.">
                <Input value={editor.reasoningEfforts} onChange={(_, data) => updateEditor('reasoningEfforts', data.value)} disabled={saving} />
              </Field>
              <Field label="Prompt capacity tokens">
                <Input
                  type="number"
                  min={1}
                  step={1}
                  value={editor.promptCapacityTokens}
                  onChange={(_, data) => updateEditor('promptCapacityTokens', data.value)}
                  disabled={saving}
                />
              </Field>
              <div className="v1-actions">
                <Button appearance="primary" type="submit" disabled={saving}>
                  {saving ? 'Saving…' : view === 'edit' ? 'Save metadata' : 'Create draft'}
                </Button>
              </div>
            </form>
          )}
        </div>
      )}
    </PageSection>
  );
}
