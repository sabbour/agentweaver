import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useLocation } from 'react-router-dom';
import { GatewayError } from './api';
import { useAuth } from './AuthContext';
import type {
  PlatformPolicyIdentity,
  PlatformPolicyRefreshResult,
  ProjectPolicyAccess,
} from './AuthContext';
import type { ProjectSummary } from './contracts';
import {
  buildPlatformPolicyValidationArtifact,
  createPlatformPolicyTextBuffer,
  parsePlatformPolicyValidationRequestFile,
  PLATFORM_POLICY_SCOPE_ID,
} from './platformPolicyContracts';
import type {
  PlatformPolicyTextBuffer,
  PlatformPolicyValidationResult,
  PlatformPolicyRevisionPage,
  PlatformPolicyRevisionReadResult,
  PlatformPolicyRevisionSummary,
} from './platformPolicyContracts';

type PolicyPageStatus = 'idle' | 'checking' | 'needsTenant' | 'ready' | 'denied' | 'unavailable';
export type PolicyScope = 'platform' | 'project';

interface PolicyRevisionSelection {
  policyId: string;
  snapshotRevision: number;
  revision: PlatformPolicyRevisionSummary;
}

interface PlatformPolicyDraftFile {
  id: number;
  relativePath: string;
  content: PlatformPolicyTextBuffer;
}

type RetryRead =
  | { kind: 'revision'; selection: PolicyRevisionSelection }
  | { kind: 'continuation'; cursor: string; previousPage: PlatformPolicyRevisionPage };

interface ExpectedRefreshChange {
  previousAuthSessionGeneration: number;
  previousUnboundSessionGeneration: number;
  authSessionGeneration: number;
  unboundSessionGeneration: number;
  tenantId: string;
  issuer: string | null;
  actorId: string | null;
  contextTenantId: string | null;
}

export interface PlatformPoliciesControllerValue {
  scope: PolicyScope;
  setScope: (scope: PolicyScope) => void;
  tenantId: string;
  setTenantId: (tenantId: string) => void;
  projectId: string;
  setProjectId: (projectId: string) => void;
  projects: ProjectSummary[];
  status: PolicyPageStatus;
  authorized: boolean;
  platformNavigationAuthorized: boolean;
  history: PlatformPolicyRevisionPage | null;
  historyLoading: boolean;
  historyError: string | null;
  detail: PlatformPolicyRevisionReadResult | null;
  detailLoading: boolean;
  detailError: string | null;
  draftManifest: PlatformPolicyTextBuffer;
  draftFiles: PlatformPolicyDraftFile[];
  draftGeneration: number;
  draftError: string | null;
  validation: PlatformPolicyValidationResult | null;
  validationLoading: boolean;
  retry: () => void;
  viewRevision: (revision: PlatformPolicyRevisionSummary) => void;
  setDraftManifest: (buffer: PlatformPolicyTextBuffer) => void;
  addDraftFile: () => void;
  removeDraftFile: (id: number) => void;
  setDraftFilePath: (id: number, value: string) => void;
  setDraftFileContent: (id: number, content: PlatformPolicyTextBuffer) => void;
  importDraft: (file: File) => Promise<void>;
  loadDraftFromSavedSource: () => void;
  validateDraft: () => void;
  loadMore: () => void;
}

const PlatformPoliciesControllerContext = createContext<PlatformPoliciesControllerValue | null>(null);

function sameIdentity(left: PlatformPolicyIdentity, right: PlatformPolicyIdentity): boolean {
  return left.issuer === right.issuer &&
    left.actorId === right.actorId &&
    left.tenantId === right.tenantId;
}

function sameLineage(left: PlatformPolicyRevisionPage, right: PlatformPolicyRevisionPage): boolean {
  return left.scopeType === right.scopeType &&
    left.scopeId === right.scopeId &&
    left.policyId === right.policyId &&
    left.snapshotRevision === right.snapshotRevision;
}

function validateFirstPage(page: PlatformPolicyRevisionPage): void {
  if (page.scopeType !== 'platform' ||
      page.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      ((page.policyId === null) !== (page.snapshotRevision === null)))
    throw new Error('The saved Platform Policy snapshot is unavailable.');
  if (page.policyId === null) {
    if (page.items.length !== 0 || page.nextCursor !== null)
      throw new Error('The saved Platform Policy snapshot is unavailable.');
    return;
  }
  if (page.snapshotRevision === null ||
      !page.items.some((item) => item.revision === page.snapshotRevision))
    throw new Error('The saved Platform Policy snapshot is unavailable.');
}

function verifyRevision(
  result: PlatformPolicyRevisionReadResult,
  selection: PolicyRevisionSelection,
): void {
  if (result.scopeType !== 'platform' ||
      result.scopeId !== PLATFORM_POLICY_SCOPE_ID ||
      result.policyId !== selection.policyId ||
      result.revision !== selection.revision.revision ||
      result.contentSha256 !== selection.revision.contentSha256 ||
      result.artifact.contentSha256 !== selection.revision.contentSha256)
    throw new Error('The selected Platform Policy revision is unavailable.');
}

function safeFailure(reason: unknown): { status: PolicyPageStatus; message: string } {
  if (reason instanceof GatewayError && reason.status === 403)
    return { status: 'denied', message: 'You do not have permission to access Platform Policies.' };
  return { status: 'unavailable', message: 'Platform Policies could not load. Try again.' };
}

export function PlatformPoliciesControllerProvider({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const {
    establishPlatformPolicyAccess,
    getPlatformPolicyRevisionHistory,
    readPlatformPolicyRevision,
    validatePlatformPolicyArtifact,
    establishProjectPolicyAccess,
    validateProjectPolicyArtifact,
    refreshPlatformPolicySession,
  } = auth;
  const location = useLocation();
  const [scope, setScopeState] = useState<PolicyScope>('platform');
  const [tenantId, setTenantIdState] = useState(auth.authorizationContext?.tenantId ?? '');
  const [projectId, setProjectIdState] = useState('');
  const [projects, setProjects] = useState<ProjectSummary[]>([]);
  const [status, setStatus] = useState<PolicyPageStatus>('idle');
  const [authorized, setAuthorizedState] = useState(false);
  const [platformNavigationAuthorized, setPlatformNavigationAuthorized] = useState(false);
  const [history, setHistoryState] = useState<PlatformPolicyRevisionPage | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyError, setHistoryError] = useState<string | null>(null);
  const [detail, setDetail] = useState<PlatformPolicyRevisionReadResult | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [draftManifest, setDraftManifestState] = useState(() => createPlatformPolicyTextBuffer(''));
  const [draftFiles, setDraftFiles] = useState<PlatformPolicyDraftFile[]>([]);
  const [draftGeneration, setDraftGeneration] = useState(0);
  const [draftError, setDraftError] = useState<string | null>(null);
  const [validation, setValidation] = useState<PlatformPolicyValidationResult | null>(null);
  const [validationLoading, setValidationLoading] = useState(false);
  const [visible, setVisible] = useState(() => document.visibilityState !== 'hidden');
  const requestGenerationRef = useRef(0);
  const validationGenerationRef = useRef(0);
  const importGenerationRef = useRef(0);
  const validationControllerRef = useRef<AbortController | null>(null);
  const draftFileIdRef = useRef(0);
  const identityEpochRef = useRef(0);
  const currentRequestRef = useRef<AbortController | null>(null);
  const policyIdentityRef = useRef<PlatformPolicyIdentity | null>(null);
  const revisionSelectionRef = useRef<PolicyRevisionSelection | null>(null);
  const draftIdentityRef = useRef<PlatformPolicyIdentity | null>(null);
  const draftScopeRef = useRef<PolicyScope | null>(null);
  const draftProjectRef = useRef<string | null>(null);
  const draftAuthSessionEpochRef = useRef<number | null>(null);
  const historyRef = useRef<PlatformPolicyRevisionPage | null>(null);
  const tenantRef = useRef(tenantId);
  const scopeRef = useRef<PolicyScope>(scope);
  const projectRef = useRef(projectId);
  const tenantWasSelectedRef = useRef(false);
  const authorizedRef = useRef(false);
  const statusRef = useRef(status);
  const lastAuthSignatureRef = useRef('');
  const expectedRefreshChangeRef = useRef<ExpectedRefreshChange | null>(null);
  const latestAuthRef = useRef(auth);
  const lastActiveContextTenantRef = useRef(auth.authorizationContext?.tenantId ?? null);
  const lastActiveContextSessionGenerationRef = useRef(auth.authSessionGeneration);
  const focusAfterHideRef = useRef(false);
  const runCheckRef = useRef<((
    requestedTenant: string,
    expectedIdentity?: PlatformPolicyIdentity | null,
    retryCount?: number,
    retryRead?: RetryRead,
  ) => Promise<void>) | null>(null);

  useEffect(() => {
    latestAuthRef.current = auth;
  }, [auth]);

  const capturePolicyLinkFocus = useCallback(() => {
    const activeElement = document.activeElement;
    if ((activeElement instanceof HTMLAnchorElement &&
         activeElement.getAttribute('href') === '/policies') ||
        (activeElement instanceof HTMLElement &&
         (activeElement.closest('[data-platform-policy-comparison]') ||
          activeElement.closest('[data-platform-policy-compare]'))))
      focusAfterHideRef.current = true;
  }, []);

  const setAuthorized = useCallback((value: boolean) => {
    if (!value) capturePolicyLinkFocus();
    authorizedRef.current = value;
    setAuthorizedState(value);
  }, [capturePolicyLinkFocus]);

  const setHistory = useCallback((value: PlatformPolicyRevisionPage | null) => {
    historyRef.current = value;
    setHistoryState(value);
  }, []);

  const setPageStatus = useCallback((value: PolicyPageStatus) => {
    statusRef.current = value;
    setStatus(value);
  }, []);

  const bindDraftToCurrentIdentity = useCallback(() => {
    if (policyIdentityRef.current) {
      draftIdentityRef.current = policyIdentityRef.current;
      draftScopeRef.current = scopeRef.current;
      draftProjectRef.current = scopeRef.current === 'project' ? projectRef.current : null;
      draftAuthSessionEpochRef.current = scopeRef.current === 'project'
        ? latestAuthRef.current.authSessionEpoch
        : null;
    }
  }, []);

  const invalidateDraftComparison = useCallback(() => {
    setDraftGeneration((generation) => generation + 1);
  }, []);

  const clearDraftValidation = useCallback((
    preserveError = false,
    canceledMessage?: string,
  ) => {
    const wasValidating = validationControllerRef.current !== null;
    validationGenerationRef.current += 1;
    validationControllerRef.current?.abort();
    validationControllerRef.current = null;
    setValidationLoading(false);
    setValidation(null);
    if (wasValidating && canceledMessage)
      setDraftError(canceledMessage);
    else if (!preserveError)
      setDraftError(null);
  }, []);

  const clearPolicyState = useCallback((
    nextStatus: PolicyPageStatus,
    preserveDraftError = false,
    preserveDraft = false,
    preservePlatformNavigation = false,
  ) => {
    const validationCanceled = validationControllerRef.current !== null;
    capturePolicyLinkFocus();
    identityEpochRef.current += 1;
    requestGenerationRef.current += 1;
    clearDraftValidation(preserveDraftError);
    invalidateDraftComparison();
    if (!preserveDraft) {
      setDraftManifestState(createPlatformPolicyTextBuffer(''));
      setDraftFiles([]);
      draftIdentityRef.current = null;
      draftScopeRef.current = null;
      draftProjectRef.current = null;
      draftAuthSessionEpochRef.current = null;
      importGenerationRef.current += 1;
    }
    if (validationCanceled && !preserveDraftError && nextStatus === 'checking')
      setDraftError('Platform Policy access changed. Validate the draft again when access is ready.');
    currentRequestRef.current?.abort();
    currentRequestRef.current = null;
    policyIdentityRef.current = null;
    revisionSelectionRef.current = null;
    if (!preservePlatformNavigation)
      setPlatformNavigationAuthorized(false);
    setProjects([]);
    setAuthorized(false);
    setHistory(null);
    setHistoryLoading(false);
    setHistoryError(null);
    setDetail(null);
    setDetailLoading(false);
    setDetailError(null);
    setPageStatus(nextStatus);
  }, [
    capturePolicyLinkFocus,
    clearDraftValidation,
    invalidateDraftComparison,
    setAuthorized,
    setHistory,
    setPageStatus,
  ]);

  const updateTenantId = useCallback((value: string, userSelected: boolean) => {
    const changed = tenantRef.current.trim() !== value.trim();
    tenantRef.current = value;
    setTenantIdState(value);
    if (userSelected && changed) tenantWasSelectedRef.current = true;
    if (changed) {
      projectRef.current = '';
      setProjectIdState('');
      clearPolicyState(value.trim() ? 'idle' : 'needsTenant');
    }
  }, [clearPolicyState]);

  const setTenantId = useCallback((value: string) => {
    updateTenantId(value, true);
  }, [updateTenantId]);

  const setScope = useCallback((value: PolicyScope) => {
    if (scopeRef.current === value) return;
    scopeRef.current = value;
    setScopeState(value);
    clearPolicyState(
      tenantRef.current.trim() ? 'idle' : 'needsTenant',
      false,
      false,
      value === 'project',
    );
    if (tenantRef.current.trim())
      window.setTimeout(() => { void runCheckRef.current?.(tenantRef.current); }, 0);
  }, [clearPolicyState]);

  const setProjectId = useCallback((value: string) => {
    if (projectRef.current === value) return;
    projectRef.current = value;
    setProjectIdState(value);
    clearPolicyState(
      tenantRef.current.trim() ? 'idle' : 'needsTenant',
      false,
      false,
      true,
    );
    if (tenantRef.current.trim())
      window.setTimeout(() => { void runCheckRef.current?.(tenantRef.current); }, 0);
  }, [clearPolicyState]);

  const setDraftManifest = useCallback((buffer: PlatformPolicyTextBuffer) => {
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    setDraftManifestState(buffer);
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const addDraftFile = useCallback(() => {
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    const id = ++draftFileIdRef.current;
    setDraftFiles((current) => [
      ...current,
      {
        id,
        relativePath: `rules/policy-${id}.yaml`,
        content: createPlatformPolicyTextBuffer(''),
      },
    ]);
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const removeDraftFile = useCallback((id: number) => {
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    setDraftFiles((current) => current.filter((file) => file.id !== id));
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const setDraftFilePath = useCallback((id: number, value: string) => {
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    setDraftFiles((current) => current.map((file) =>
      file.id === id ? { ...file, relativePath: value } : file));
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const setDraftFileContent = useCallback((
    id: number,
    content: PlatformPolicyTextBuffer,
  ) => {
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    setDraftFiles((current) => current.map((file) =>
      file.id === id
        ? { ...file, content }
        : file));
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const importDraft = useCallback(async (file: File) => {
    if (scopeRef.current !== 'project' ||
        !authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !policyIdentityRef.current ||
        !projectRef.current)
      return;
    const importIdentityEpoch = identityEpochRef.current;
    const importScope = scopeRef.current;
    const importTenant = tenantRef.current;
    const importProject = projectRef.current;
    const importGeneration = ++importGenerationRef.current;
    try {
      if (file.size > 5 * 1024 * 1024)
        throw new Error('The Policy validation request exceeds 5,242,880 bytes.');
      const bytes = new Uint8Array(await file.arrayBuffer());
      const imported = await parsePlatformPolicyValidationRequestFile(bytes);
      if (identityEpochRef.current !== importIdentityEpoch ||
          scopeRef.current !== importScope ||
          tenantRef.current !== importTenant ||
          projectRef.current !== importProject ||
          importGenerationRef.current !== importGeneration ||
          !authorizedRef.current)
        return;
      bindDraftToCurrentIdentity();
      clearDraftValidation();
      invalidateDraftComparison();
      setDraftManifestState(imported.manifest);
      setDraftFiles(imported.files.map((importedFile) => ({
        id: ++draftFileIdRef.current,
        relativePath: importedFile.relativePath,
        content: importedFile.content,
      })));
    } catch (reason) {
      if (identityEpochRef.current !== importIdentityEpoch ||
          scopeRef.current !== importScope ||
          tenantRef.current !== importTenant ||
          projectRef.current !== importProject ||
          importGenerationRef.current !== importGeneration)
        return;
      setDraftError(reason instanceof Error
        ? reason.message
        : 'The selected Policy validation request could not be imported.');
    }
  }, [bindDraftToCurrentIdentity, clearDraftValidation, invalidateDraftComparison]);

  const loadDraftFromSavedSource = useCallback(() => {
    if (scopeRef.current !== 'platform' || !authorizedRef.current || !detail) return;
    importGenerationRef.current += 1;
    bindDraftToCurrentIdentity();
    clearDraftValidation();
    invalidateDraftComparison();
    setDraftManifestState(createPlatformPolicyTextBuffer(detail.artifact.manifest));
    setDraftFiles(detail.artifact.files.map((file) => ({
      id: ++draftFileIdRef.current,
      relativePath: file.relativePath,
      content: createPlatformPolicyTextBuffer(file.content),
    })));
  }, [bindDraftToCurrentIdentity, clearDraftValidation, detail, invalidateDraftComparison]);

  const expectPolicyRefreshChange = useCallback((selectedTenant: string): ExpectedRefreshChange => {
    const current = latestAuthRef.current;
    const expected: ExpectedRefreshChange = {
      previousAuthSessionGeneration: current.authSessionGeneration,
      previousUnboundSessionGeneration: current.unboundSessionGeneration,
      authSessionGeneration: current.authSessionGeneration +
        (current.session?.binding === null ? 1 : 0),
      unboundSessionGeneration: current.unboundSessionGeneration + 1,
      tenantId: selectedTenant,
      issuer: current.authorizationContext?.issuer ?? null,
      actorId: current.authorizationContext?.actorId ?? null,
      contextTenantId: current.authorizationContext?.tenantId ?? null,
    };
    expectedRefreshChangeRef.current = expected;
    return expected;
  }, []);

  const isCurrentRecovery = useCallback((
    identityEpoch: number,
    requestGeneration: number,
    selectedTenant: string,
    expected: ExpectedRefreshChange,
    result: PlatformPolicyRefreshResult,
  ): boolean => {
    const current = latestAuthRef.current;
    const sameIdentity = !current.authorizationContext ||
      (current.authorizationContext.issuer === expected.issuer &&
       current.authorizationContext.actorId === expected.actorId &&
       current.authorizationContext.tenantId === expected.contextTenantId);
    const isCurrent = identityEpochRef.current === identityEpoch &&
      requestGenerationRef.current === requestGeneration &&
      tenantRef.current.trim() === selectedTenant &&
      result.authSessionGeneration === expected.authSessionGeneration &&
      result.unboundSessionGeneration === expected.unboundSessionGeneration &&
      (current.authSessionGeneration === expected.previousAuthSessionGeneration ||
       current.authSessionGeneration === expected.authSessionGeneration) &&
      (current.unboundSessionGeneration === expected.previousUnboundSessionGeneration ||
       current.unboundSessionGeneration === expected.unboundSessionGeneration) &&
      sameIdentity;
    if (!isCurrent && expectedRefreshChangeRef.current === expected)
      expectedRefreshChangeRef.current = null;
    return isCurrent;
  }, []);

  const isCurrentRequest = useCallback((
    identityEpoch: number,
    requestGeneration: number,
    controller: AbortController,
  ) => identityEpoch === identityEpochRef.current &&
    requestGeneration === requestGenerationRef.current &&
    currentRequestRef.current === controller &&
    !controller.signal.aborted, []);

  const runCheck = useCallback(async (
    requestedTenant: string,
    expectedIdentity: PlatformPolicyIdentity | null = null,
    retryCount = 0,
    retryRead?: RetryRead,
  ): Promise<void> => {
    const selectedTenant = requestedTenant.trim();
    updateTenantId(selectedTenant, false);
    if (!selectedTenant) {
      clearPolicyState('needsTenant');
      return;
    }
    if (scopeRef.current === 'project') {
      const selectedProject = projectRef.current;
      currentRequestRef.current?.abort();
      const controller = new AbortController();
      currentRequestRef.current = controller;
      const identityEpoch = ++identityEpochRef.current;
      const requestGeneration = ++requestGenerationRef.current;
      clearDraftValidation(true, 'Project Policy access changed. Validate the draft again when access is ready.');
      policyIdentityRef.current = null;
      setAuthorized(false);
      setHistory(null);
      setHistoryLoading(false);
      setHistoryError(null);
      setDetail(null);
      setDetailLoading(false);
      setDetailError(null);
      setProjects([]);
      setPageStatus('checking');

      let resolvedIdentity: PlatformPolicyIdentity | null = expectedIdentity;
      try {
        const access: ProjectPolicyAccess = await establishProjectPolicyAccess(
          selectedTenant,
          selectedProject || null,
          controller.signal,
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (expectedIdentity && !sameIdentity(access.identity, expectedIdentity))
          throw new GatewayError(403, { code: 'policy_identity_changed' },
            'Project Policy access was denied.');
        resolvedIdentity = access.identity;
        policyIdentityRef.current = access.identity;
        setProjects(access.projects);
        setAuthorized(Boolean(selectedProject));
        setPageStatus('ready');
      } catch (reason) {
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (reason instanceof GatewayError &&
            reason.status === 401 &&
            retryCount === 0) {
          clearPolicyState('checking', false, true, true);
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const expectedRefresh = expectPolicyRefreshChange(selectedTenant);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            selectedTenant,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed) {
            await runCheckRef.current?.(selectedTenant, resolvedIdentity, 1);
            return;
          }
        }
        if (reason instanceof GatewayError &&
            (reason.status === 403 || reason.status === 404)) {
          clearPolicyState('denied', false, false, true);
          if (reason.status === 404)
            setDraftError('The selected Project is unavailable. Choose another Project.');
        } else {
          clearPolicyState('unavailable', false, true, true);
        }
      } finally {
        if (currentRequestRef.current === controller) {
          currentRequestRef.current = null;
        }
      }
      return;
    }

    if (statusRef.current === 'checking' &&
        tenantRef.current === selectedTenant &&
        currentRequestRef.current)
      return;

    currentRequestRef.current?.abort();
    const controller = new AbortController();
    currentRequestRef.current = controller;
    const identityEpoch = ++identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    clearDraftValidation(true, 'Platform Policy access changed. Validate the draft again when access is ready.');
    policyIdentityRef.current = null;
    setAuthorized(false);
    setHistory(null);
    setHistoryLoading(false);
    setHistoryError(null);
    setDetail(null);
    setDetailLoading(false);
    setDetailError(null);
    setPageStatus('checking');

    let resolvedIdentity = expectedIdentity;
    let historyAuthorized = false;
    try {
      const identity = await establishPlatformPolicyAccess(selectedTenant, controller.signal);
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      if (expectedIdentity && !sameIdentity(identity, expectedIdentity))
        throw new GatewayError(403, { code: 'policy_identity_changed' },
          'Platform Policy access was denied.');
      resolvedIdentity = identity;

      const firstPage = await getPlatformPolicyRevisionHistory(
        selectedTenant,
        { signal: controller.signal },
      );
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      validateFirstPage(firstPage);
      policyIdentityRef.current = identity;
      setHistory(firstPage);
      setAuthorized(true);
      setPlatformNavigationAuthorized(true);
      setPageStatus('ready');
      historyAuthorized = true;

      if (retryRead?.kind === 'revision') {
        if (firstPage.policyId !== retryRead.selection.policyId)
          throw new Error('The selected Platform Policy revision is unavailable.');
        setDetailLoading(true);
        const result = await readPlatformPolicyRevision(
          selectedTenant,
          retryRead.selection.revision.revision,
          controller.signal,
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        verifyRevision(result, retryRead.selection);
        revisionSelectionRef.current = retryRead.selection;
        setDetail(result);
        setDetailLoading(false);
      } else if (retryRead?.kind === 'continuation') {
        const page = await getPlatformPolicyRevisionHistory(
          selectedTenant,
          { cursor: retryRead.cursor, signal: controller.signal },
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (!sameLineage(page, retryRead.previousPage))
          throw new Error('The Platform Policy history changed. Reload the page.');
        const seen = new Set(retryRead.previousPage.items.map((item) => item.revision));
        if (page.items.some((item) => seen.has(item.revision)))
          throw new Error('The Platform Policy history is unavailable.');
        setHistory({
          ...retryRead.previousPage,
          items: [...retryRead.previousPage.items, ...page.items],
          nextCursor: page.nextCursor,
        });
      }
    } catch (reason) {
      if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
      if (reason instanceof GatewayError &&
          reason.status === 401 &&
          reason.code === 'platform_policy_request_failed' &&
          retryCount === 0) {
        clearPolicyState('checking', false, true);
        const recoveryIdentityEpoch = identityEpochRef.current;
        const recoveryRequestGeneration = requestGenerationRef.current;
        const recoveryTenant = tenantRef.current.trim();
        const expectedRefresh = expectPolicyRefreshChange(selectedTenant);
        const refreshResult = await refreshPlatformPolicySession();
        if (!isCurrentRecovery(
          recoveryIdentityEpoch,
          recoveryRequestGeneration,
          recoveryTenant,
          expectedRefresh,
          refreshResult,
        )) return;
        if (refreshResult.refreshed) {
          await runCheckRef.current?.(selectedTenant, resolvedIdentity, 1, retryRead);
          return;
        }
      }
      if (historyAuthorized &&
          retryRead?.kind === 'revision' &&
          !(reason instanceof GatewayError && (reason.status === 401 || reason.status === 403))) {
        setDetailError('The selected Platform Policy revision is unavailable. Try again.');
        return;
      }
      const failure = safeFailure(reason);
      clearPolicyState(failure.status, false, failure.status !== 'denied');
      if (retryRead?.kind === 'revision' && failure.status !== 'denied')
        setDetailError('The selected Platform Policy revision is unavailable. Try again.');
    } finally {
      if (currentRequestRef.current === controller) {
        currentRequestRef.current = null;
        setDetailLoading(false);
      }
    }
  }, [
    establishPlatformPolicyAccess,
    establishProjectPolicyAccess,
    getPlatformPolicyRevisionHistory,
    readPlatformPolicyRevision,
    refreshPlatformPolicySession,
    clearPolicyState,
    isCurrentRequest,
    clearDraftValidation,
    expectPolicyRefreshChange,
    isCurrentRecovery,
    setAuthorized,
    setHistory,
    setPageStatus,
    updateTenantId,
  ]);

  useEffect(() => {
    runCheckRef.current = runCheck;
  }, [runCheck]);

  const validateDraft = useCallback(() => {
    const identity = policyIdentityRef.current;
    const activeProject = projectRef.current;
    const authSessionEpoch = latestAuthRef.current.authSessionEpoch;
    if (!authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !identity ||
        tenantRef.current.trim() !== identity.tenantId ||
        (scopeRef.current === 'project' && !activeProject))
      return;

    clearDraftValidation();
    const generation = ++validationGenerationRef.current;
    const identityEpoch = identityEpochRef.current;
    const authSessionGeneration = latestAuthRef.current.authSessionGeneration;
    const unboundSessionGeneration = latestAuthRef.current.unboundSessionGeneration;
    const controller = new AbortController();
    validationControllerRef.current = controller;
    setValidationLoading(true);

    const isCurrentValidation = () => {
      return (
        validationGenerationRef.current === generation &&
        identityEpochRef.current === identityEpoch &&
        validationControllerRef.current === controller &&
        !controller.signal.aborted &&
        authorizedRef.current &&
        statusRef.current === 'ready' &&
        policyIdentityRef.current === identity &&
        tenantRef.current.trim() === identity.tenantId
      );
    };
    const isCurrentProjectActivation = () =>
      identityEpochRef.current === identityEpoch &&
      scopeRef.current === 'project' &&
      authorizedRef.current &&
      statusRef.current === 'ready' &&
      policyIdentityRef.current !== null &&
      sameIdentity(policyIdentityRef.current, identity) &&
      tenantRef.current.trim() === identity.tenantId &&
      projectRef.current === activeProject &&
      latestAuthRef.current.authSessionEpoch === authSessionEpoch;
    const isCurrentProjectValidation = () =>
      isCurrentProjectActivation() &&
      validationGenerationRef.current === generation &&
      validationControllerRef.current === controller &&
      !controller.signal.aborted;

    if (scopeRef.current === 'project') {
      void (async () => {
        let artifact: Awaited<ReturnType<typeof buildPlatformPolicyValidationArtifact>>;
        try {
          artifact = await buildPlatformPolicyValidationArtifact(
            draftManifest,
            draftFiles.map(({ relativePath, content }) => ({ relativePath, content })),
          );
          if (!isCurrentProjectValidation()) return;
          let result: PlatformPolicyValidationResult | undefined;
          try {
            result = await validateProjectPolicyArtifact(
              identity.tenantId,
              activeProject,
              artifact,
              controller.signal,
            );
          } catch (reason) {
            if (reason instanceof DOMException && reason.name === 'AbortError') {
              if (!isCurrentProjectActivation()) return;
              try {
                const access = await establishProjectPolicyAccess(
                  identity.tenantId,
                  activeProject,
                );
                if (!isCurrentProjectActivation() || !sameIdentity(access.identity, identity))
                  return;
                policyIdentityRef.current = access.identity;
                setProjects(access.projects);
                setPageStatus('ready');
              } catch (accessReason) {
                if (!isCurrentProjectActivation()) return;
                if (accessReason instanceof GatewayError &&
                    (accessReason.status === 403 || accessReason.status === 404))
                  clearPolicyState('denied', false, false, true);
                else
                  setDraftError('Project Policy access could not be checked. Try again.');
              }
              return;
            }
            if (reason instanceof GatewayError &&
                (reason.status === 403 || reason.status === 404)) {
              if (isCurrentProjectActivation())
                clearPolicyState('denied', false, false, true);
              return;
            }
            if (!isCurrentProjectValidation()) return;
            if (reason instanceof GatewayError && reason.status === 401) {
              const recoveryIdentityEpoch = identityEpochRef.current;
              const recoveryRequestGeneration = requestGenerationRef.current;
              const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
              const refreshResult = await refreshPlatformPolicySession();
              if (!isCurrentRecovery(
                recoveryIdentityEpoch,
                recoveryRequestGeneration,
                identity.tenantId,
                expectedRefresh,
                refreshResult,
              ) || !isCurrentProjectValidation()) return;
              if (refreshResult.refreshed) {
                let access: ProjectPolicyAccess;
                try {
                  access = await establishProjectPolicyAccess(
                    identity.tenantId,
                    activeProject,
                  );
                } catch (accessReason) {
                  if (isCurrentProjectActivation() &&
                      accessReason instanceof GatewayError &&
                      (accessReason.status === 403 || accessReason.status === 404)) {
                    clearPolicyState('denied', false, false, true);
                  } else if (isCurrentProjectValidation()) {
                    setDraftError('Project Policy access could not be checked. Try again.');
                  }
                  return;
                }
                if (!isCurrentProjectValidation() ||
                    !sameIdentity(access.identity, identity))
                  return;
                policyIdentityRef.current = access.identity;
                setProjects(access.projects);
                try {
                  result = await validateProjectPolicyArtifact(
                    identity.tenantId,
                    activeProject,
                    artifact,
                    controller.signal,
                  );
                } catch (retryReason) {
                  if (retryReason instanceof GatewayError &&
                      (retryReason.status === 403 || retryReason.status === 404)) {
                    if (isCurrentProjectActivation())
                      clearPolicyState('denied', false, false, true);
                    return;
                  }
                  if (!isCurrentProjectValidation()) return;
                  setDraftError(retryReason instanceof GatewayError && retryReason.status === 401
                    ? 'Project Policy access expired. Check access, then validate again.'
                    : retryReason instanceof Error
                      ? retryReason.message
                      : 'Project Policy validation could not complete. Try again.');
                  return;
                }
              } else {
                setDraftError('Project Policy access expired. Check access, then validate again.');
                return;
              }
            } else {
              setDraftError(reason instanceof Error
                ? reason.message
                : 'Project Policy validation could not complete. Try again.');
              return;
            }
          }
          if (!isCurrentProjectValidation()) return;
          if (result) setValidation(result);
        } catch (reason) {
          if (!isCurrentProjectValidation()) return;
          if (reason instanceof GatewayError &&
              (reason.status === 403 || reason.status === 404)) {
            if (isCurrentProjectActivation())
              clearPolicyState('denied', false, false, true);
            return;
          }
          setDraftError(reason instanceof Error
            ? reason.message
            : 'Project Policy validation could not complete. Try again.');
        } finally {
          if (validationControllerRef.current === controller) {
            validationControllerRef.current = null;
            setValidationLoading(false);
          }
        }
      })();
      return;
    }

    void (async () => {
      try {
        const artifact = await buildPlatformPolicyValidationArtifact(
          draftManifest,
          draftFiles.map(({ relativePath, content }) => ({ relativePath, content })),
        );
        if (!isCurrentValidation()) return;
        const result = await validatePlatformPolicyArtifact(
          identity.tenantId,
          artifact,
          controller.signal,
        );
        if (!isCurrentValidation()) return;
        setValidation(result);
      } catch (reason) {
        if (reason instanceof GatewayError &&
            reason.status === 403 &&
            scopeRef.current === 'platform' &&
            identityEpochRef.current === identityEpoch &&
            policyIdentityRef.current === identity &&
            tenantRef.current.trim() === identity.tenantId &&
            latestAuthRef.current.authSessionGeneration === authSessionGeneration &&
            latestAuthRef.current.unboundSessionGeneration === unboundSessionGeneration) {
          clearPolicyState('denied');
          return;
        }
        if (!isCurrentValidation()) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          clearPolicyState('denied');
        } else if (reason instanceof GatewayError &&
            reason.status === 401 &&
            reason.code === 'platform_policy_request_failed') {
          const selection = revisionSelectionRef.current;
          clearPolicyState('checking', true, true);
          setDraftError('Platform Policy access expired. Check access, then validate again.');
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const recoveryTenant = tenantRef.current.trim();
          const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            recoveryTenant,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed) {
            await runCheckRef.current?.(
              identity.tenantId,
              identity,
              1,
              selection ? { kind: 'revision', selection } : undefined,
            );
          }
          else
            setPageStatus('unavailable');
        } else {
          setDraftError(reason instanceof Error
            ? reason.message
            : 'Platform Policy validation could not complete. Try again.');
        }
      } finally {
        if (validationControllerRef.current === controller) {
          validationControllerRef.current = null;
          setValidationLoading(false);
        }
      }
    })();
  }, [
    clearDraftValidation,
    clearPolicyState,
    draftFiles,
    draftManifest,
    expectPolicyRefreshChange,
    isCurrentRecovery,
    establishProjectPolicyAccess,
    refreshPlatformPolicySession,
    setPageStatus,
    validateProjectPolicyArtifact,
    validatePlatformPolicyArtifact,
  ]);

  const retry = useCallback(() => {
    const selection = revisionSelectionRef.current;
    void runCheck(
      tenantRef.current,
      policyIdentityRef.current,
      0,
      selection ? { kind: 'revision', selection } : undefined,
    );
  }, [runCheck]);

  const viewRevision = useCallback((revision: PlatformPolicyRevisionSummary) => {
    const currentPage = historyRef.current;
    const identity = policyIdentityRef.current;
    if (scopeRef.current !== 'platform' ||
        !authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !currentPage?.policyId ||
        currentPage.snapshotRevision === null ||
        !identity ||
        !currentPage.items.some((item) =>
          item.revision === revision.revision && item.contentSha256 === revision.contentSha256))
      return;

    const selection: PolicyRevisionSelection = {
      policyId: currentPage.policyId,
      snapshotRevision: currentPage.snapshotRevision,
      revision,
    };
    revisionSelectionRef.current = selection;
    const controller = new AbortController();
    currentRequestRef.current?.abort();
    currentRequestRef.current = controller;
    const identityEpoch = identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    setDetail(null);
    setDetailError(null);
    setDetailLoading(true);
    void (async () => {
      try {
        const result = await readPlatformPolicyRevision(
          identity.tenantId,
          revision.revision,
          controller.signal,
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        verifyRevision(result, selection);
        setDetail(result);
      } catch (reason) {
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          clearPolicyState('denied');
        } else if (reason instanceof GatewayError &&
            reason.status === 401 &&
            reason.code === 'platform_policy_request_failed') {
          clearPolicyState('checking', false, true);
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            identity.tenantId,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed)
            await runCheckRef.current?.(identity.tenantId, identity, 1, { kind: 'revision', selection });
          else {
            setPageStatus('unavailable');
            setDetailError('The selected Platform Policy revision is unavailable. Try again.');
          }
        } else {
          setDetailError('The selected Platform Policy revision is unavailable. Try again.');
        }
      } finally {
        if (currentRequestRef.current === controller) {
          currentRequestRef.current = null;
          setDetailLoading(false);
        }
      }
    })();
  }, [
    clearPolicyState,
    expectPolicyRefreshChange,
    isCurrentRecovery,
    isCurrentRequest,
    readPlatformPolicyRevision,
    refreshPlatformPolicySession,
    setPageStatus,
  ]);

  const loadMore = useCallback(() => {
    const currentPage = historyRef.current;
    const identity = policyIdentityRef.current;
    if (scopeRef.current !== 'platform' ||
        !authorizedRef.current ||
        statusRef.current !== 'ready' ||
        !currentPage?.nextCursor ||
        !currentPage.policyId ||
        currentPage.snapshotRevision === null ||
        !identity ||
        currentRequestRef.current)
      return;
    const cursor = currentPage.nextCursor;
    const controller = new AbortController();
    currentRequestRef.current = controller;
    const identityEpoch = identityEpochRef.current;
    const requestGeneration = ++requestGenerationRef.current;
    setHistoryLoading(true);
    setHistoryError(null);
    void (async () => {
      try {
        const page = await getPlatformPolicyRevisionHistory(
          identity.tenantId,
          { cursor, signal: controller.signal },
        );
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (!sameLineage(page, currentPage))
          throw new Error('The Platform Policy history changed. Reload the page.');
        const seen = new Set(currentPage.items.map((item) => item.revision));
        if (page.items.some((item) => seen.has(item.revision)))
          throw new Error('The Platform Policy history is unavailable.');
        setHistory({
          ...currentPage,
          items: [...currentPage.items, ...page.items],
          nextCursor: page.nextCursor,
        });
      } catch (reason) {
        if (!isCurrentRequest(identityEpoch, requestGeneration, controller)) return;
        if (reason instanceof GatewayError && reason.status === 403) {
          clearPolicyState('denied');
          return;
        }
        if (reason instanceof GatewayError &&
            reason.status === 401 &&
            reason.code === 'platform_policy_request_failed') {
          clearPolicyState('checking', false, true);
          const recoveryIdentityEpoch = identityEpochRef.current;
          const recoveryRequestGeneration = requestGenerationRef.current;
          const expectedRefresh = expectPolicyRefreshChange(identity.tenantId);
          const refreshResult = await refreshPlatformPolicySession();
          if (!isCurrentRecovery(
            recoveryIdentityEpoch,
            recoveryRequestGeneration,
            identity.tenantId,
            expectedRefresh,
            refreshResult,
          )) return;
          if (refreshResult.refreshed) {
            await runCheckRef.current?.(identity.tenantId, identity, 1, {
              kind: 'continuation',
              cursor,
              previousPage: currentPage,
            });
            return;
          }
        }
        if (reason instanceof TypeError) {
          setHistoryError('Additional Platform Policy revisions could not load. Try again.');
          return;
        }
        clearPolicyState('unavailable', false, true);
      } finally {
        if (currentRequestRef.current === controller) {
          currentRequestRef.current = null;
          setHistoryLoading(false);
        }
      }
    })();
  }, [
    getPlatformPolicyRevisionHistory,
    expectPolicyRefreshChange,
    refreshPlatformPolicySession,
    clearPolicyState,
    isCurrentRecovery,
    isCurrentRequest,
    setHistory,
  ]);

  const authSignature = JSON.stringify([
    auth.authSessionEpoch,
    auth.authSessionGeneration,
    auth.unboundSessionGeneration,
    Boolean(auth.session),
    auth.authorizationContext?.issuer ?? '',
    auth.authorizationContext?.actorId ?? '',
    auth.authorizationContext?.tenantId ?? '',
    auth.authorizationContext?.membershipRevision ?? 0,
  ]);

  useEffect(() => {
    const activeTenant = auth.authorizationContext?.tenantId ?? null;
    if (lastActiveContextSessionGenerationRef.current !== auth.authSessionGeneration) {
      lastActiveContextSessionGenerationRef.current = auth.authSessionGeneration;
      lastActiveContextTenantRef.current = activeTenant;
      if (activeTenant && !tenantWasSelectedRef.current)
        updateTenantId(activeTenant, false);
      return;
    }
    if (!activeTenant) return;
    const previousTenant = lastActiveContextTenantRef.current;
    lastActiveContextTenantRef.current = activeTenant;
    if (previousTenant && previousTenant !== activeTenant)
      updateTenantId(activeTenant, false);
    else if (!previousTenant && !tenantWasSelectedRef.current)
      updateTenantId(activeTenant, false);
  }, [
    auth.authSessionGeneration,
    auth.authorizationContext?.tenantId,
    updateTenantId,
  ]);

  useEffect(() => {
    if (!lastAuthSignatureRef.current) {
      lastAuthSignatureRef.current = authSignature;
      const selectedTenant = tenantRef.current.trim() || auth.authorizationContext?.tenantId || '';
      if (auth.session && selectedTenant && location.pathname !== '/policies')
        void runCheck(selectedTenant);
      return;
    }
    if (lastAuthSignatureRef.current === authSignature) return;
    lastAuthSignatureRef.current = authSignature;
    const expectedRefresh = expectedRefreshChangeRef.current;
    const refreshNotification =
      expectedRefresh &&
      expectedRefresh.authSessionGeneration === auth.authSessionGeneration &&
      expectedRefresh.unboundSessionGeneration === auth.unboundSessionGeneration &&
      expectedRefresh.tenantId === tenantRef.current.trim() &&
      (!auth.authorizationContext ||
        (expectedRefresh.issuer === auth.authorizationContext.issuer &&
         expectedRefresh.actorId === auth.authorizationContext.actorId &&
         expectedRefresh.contextTenantId === auth.authorizationContext.tenantId));
    if (refreshNotification) {
      expectedRefreshChangeRef.current = null;
      return;
    }
    expectedRefreshChangeRef.current = null;
    const selectedTenant = tenantRef.current.trim() || auth.authorizationContext?.tenantId || '';
    const previousPolicyIdentity = draftIdentityRef.current;
    const preserveDraft = !!auth.session &&
      !!previousPolicyIdentity &&
      draftScopeRef.current === scopeRef.current &&
      (scopeRef.current !== 'project' || draftProjectRef.current === projectRef.current) &&
      (scopeRef.current !== 'project' ||
        draftAuthSessionEpochRef.current === auth.authSessionEpoch) &&
      previousPolicyIdentity.tenantId === selectedTenant &&
      (!auth.authorizationContext ||
        (previousPolicyIdentity.issuer === auth.authorizationContext.issuer &&
         previousPolicyIdentity.actorId === auth.authorizationContext.actorId));
    clearPolicyState(auth.session ? 'checking' : 'idle', false, preserveDraft);
    if (auth.session && selectedTenant)
      void runCheck(selectedTenant, preserveDraft ? previousPolicyIdentity : null);
  }, [
    auth.authSessionGeneration,
    auth.authSessionEpoch,
    auth.authorizationContext,
    auth.authorizationContext?.actorId,
    auth.authorizationContext?.issuer,
    auth.authorizationContext?.membershipRevision,
    auth.authorizationContext?.tenantId,
    auth.session,
    authSignature,
    auth.unboundSessionGeneration,
    clearPolicyState,
    location.pathname,
    runCheck,
  ]);

  useEffect(() => {
    if (location.pathname !== '/policies') return;
    if (tenantRef.current)
      void runCheck(tenantRef.current);
    else
      clearPolicyState('needsTenant');
  }, [clearPolicyState, location.pathname, runCheck]);

  useEffect(() => {
    if (location.pathname === '/policies') return;
    revisionSelectionRef.current = null;
    identityEpochRef.current += 1;
    requestGenerationRef.current += 1;
    currentRequestRef.current?.abort();
    currentRequestRef.current = null;
  }, [location.pathname]);

  useEffect(() => {
    const onVisibilityChange = () => {
      const nextVisible = document.visibilityState !== 'hidden';
      setVisible(nextVisible);
      if (nextVisible) {
        const selectedTenant = tenantRef.current.trim();
        const selection = revisionSelectionRef.current;
        if (selectedTenant) void runCheck(
          selectedTenant,
          policyIdentityRef.current,
          0,
          selection ? { kind: 'revision', selection } : undefined,
        );
      }
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => document.removeEventListener('visibilitychange', onVisibilityChange);
  }, [runCheck]);

  useEffect(() => {
    if (!visible) return;
    const timer = window.setInterval(() => {
      const selectedTenant = tenantRef.current.trim();
      const selection = revisionSelectionRef.current;
      if (selectedTenant) void runCheck(
        selectedTenant,
        policyIdentityRef.current,
        0,
        selection ? { kind: 'revision', selection } : undefined,
      );
    }, 60_000);
    return () => window.clearInterval(timer);
  }, [runCheck, visible]);

  useEffect(() => {
    if (!focusAfterHideRef.current || authorized) return;
    focusAfterHideRef.current = false;
    window.setTimeout(() => {
      const retry = document.querySelector<HTMLButtonElement>('[data-platform-policies-retry]');
      const target = retry && !retry.disabled
        ? retry
        : document.querySelector<HTMLElement>('[data-platform-policies-heading]') ??
        document.querySelector<HTMLElement>('main[aria-label="Main content"]');
      target?.focus();
    }, 0);
  }, [authorized, status]);

  const value = useMemo<PlatformPoliciesControllerValue>(() => ({
    scope,
    setScope,
    tenantId,
    setTenantId,
    projectId,
    setProjectId,
    projects,
    status,
    authorized,
    platformNavigationAuthorized,
    history,
    historyLoading,
    historyError,
    detail,
    detailLoading,
    detailError,
    draftManifest,
    draftFiles,
    draftGeneration,
    draftError,
    validation,
    validationLoading,
    retry,
    viewRevision,
    setDraftManifest,
    addDraftFile,
    removeDraftFile,
    setDraftFilePath,
    setDraftFileContent,
    importDraft,
    loadDraftFromSavedSource,
    validateDraft,
    loadMore,
  }), [
    addDraftFile,
    authorized,
    platformNavigationAuthorized,
    detail,
    detailError,
    detailLoading,
    draftError,
    draftFiles,
    draftGeneration,
    draftManifest,
    history,
    historyError,
    historyLoading,
    loadMore,
    retry,
    removeDraftFile,
    setDraftFileContent,
    setDraftFilePath,
    setDraftManifest,
    setTenantId,
    status,
    tenantId,
    importDraft,
    loadDraftFromSavedSource,
    validateDraft,
    validation,
    validationLoading,
    viewRevision,
    scope,
    setScope,
    projectId,
    setProjectId,
    projects,
  ]);

  return (
    <PlatformPoliciesControllerContext.Provider value={value}>
      {children}
    </PlatformPoliciesControllerContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function usePlatformPoliciesController(): PlatformPoliciesControllerValue {
  const value = useContext(PlatformPoliciesControllerContext);
  if (!value) throw new Error('Platform Policies requires its application controller.');
  return value;
}
