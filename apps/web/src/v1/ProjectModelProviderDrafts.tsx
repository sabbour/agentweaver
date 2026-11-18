import { Button, Field, Input, Text } from '@fluentui/react-components';
import { useCallback, useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { PageSection } from '../components/ui/PageSection';
import { useAuth } from './AuthContext';
import { GatewayError, gatewayClient } from './api';
import type {
  ProjectModelProviderDraft,
  ProjectModelProviderDraftSaveRequest,
} from './contracts';
import { errorMessage } from './errors';
import { ErrorNotice, Loading } from './pageParts';

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
): ProjectModelProviderDraft {
  if (provider.scope !== 'project' || provider.projectId !== projectId || !provider.id)
    throw new Error('The service returned a provider draft for a different project.');
  return provider;
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
    session,
    authSessionGeneration,
    authorizationContext,
    authorizationContextLoading,
    authorize,
    ensureSession,
    resolveAuthorizationContext,
    busy,
    configurationError,
  } = useAuth();
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
  const pendingSave = useRef<{ fingerprint: string; operationId: string } | null>(null);
  const listGeneration = useRef(0);
  const detailGeneration = useRef(0);
  const observedSessionGeneration = useRef(authSessionGeneration);
  const accessCheckGeneration = useRef(0);
  const initializationStarted = useRef(false);
  const projectSessionActive = session?.binding?.projectId === projectId && !session.binding.runId;
  const projectContextReady = projectSessionActive &&
    authorizationContext !== null &&
    (selectedTenant == null || authorizationContext.tenantId === selectedTenant);

  const handleAccessFailure = useCallback((reason: unknown): boolean => {
    if (!(reason instanceof GatewayError) || (reason.status !== 401 && reason.status !== 403))
      return false;
    listGeneration.current += 1;
    detailGeneration.current += 1;
    setListLoading(false);
    setDetailLoading(false);
    setListError(null);
    setDetailError(null);
    setAccessError(reason);
    setAccessState(reason.status === 401 ? 'needs-sign-in' : 'error');
    return true;
  }, []);

  const loadPage = useCallback(async (
    afterProviderId: string | null = null,
    append = false,
    tenant = selectedTenant,
  ) => {
    const generation = ++listGeneration.current;
    if (!append) {
      setProviders([]);
      setNextAfterProviderId(null);
    }
    setListLoading(true);
    setListError(null);
    try {
      const page = await apiCall(
        (token) => gatewayClient.listProjectByokProviderDrafts(
          token, projectId, PAGE_SIZE, afterProviderId, tenant,
        ),
        binding,
      );
      if (generation !== listGeneration.current) return;
      const pageProviders = page.providers.map((provider) => requireProjectDraft(provider, projectId));
      setProviders((current) => append ? [...current, ...pageProviders] : pageProviders);
      setNextAfterProviderId(page.nextAfterProviderId);
    } catch (reason) {
      if (generation === listGeneration.current) {
        if (!handleAccessFailure(reason)) {
          setProviders([]);
          setNextAfterProviderId(null);
          setListError(reason);
        }
      }
    } finally {
      if (generation === listGeneration.current) setListLoading(false);
    }
  }, [apiCall, binding, handleAccessFailure, projectId, selectedTenant]);

  const prepareAccess = useCallback(async (interactive: boolean): Promise<string | null> => {
    try {
      if (interactive) {
        await authorize(binding);
      } else if (!await ensureSession(binding, true)) {
        setAccessState('needs-sign-in');
        return null;
      }
      const context = await resolveAuthorizationContext(selectedTenant ?? undefined);
      if (!context)
        throw new Error('Could not confirm access to this project. Try again.');
      setResolvedTenant(context.tenantId);
      setAccessState('ready');
      return context.tenantId;
    } catch (reason) {
      setAccessError(reason);
      setAccessState('error');
      return null;
    }
  }, [authorize, ensureSession, resolveAuthorizationContext, binding, selectedTenant]);

  const initialize = useCallback(async () => {
    const tenant = await prepareAccess(false);
    if (tenant) await loadPage(null, false, tenant);
  }, [loadPage, prepareAccess]);

  useEffect(() => {
    if (initializationStarted.current) return;
    initializationStarted.current = true;
    void initialize();
  }, [initialize]);

  useEffect(() => {
    if (observedSessionGeneration.current === authSessionGeneration) return;
    observedSessionGeneration.current = authSessionGeneration;
    if (accessState !== 'ready' || projectContextReady || authorizationContextLoading)
      return;

    const generation = ++accessCheckGeneration.current;
    void (async () => {
      const tenant = await prepareAccess(false);
      if (generation !== accessCheckGeneration.current || !tenant) return;
      await loadPage(null, false, tenant);
    })();
  }, [
    accessState,
    authSessionGeneration,
    authorizationContextLoading,
    loadPage,
    prepareAccess,
    projectContextReady,
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

  const openProvider = useCallback(async (providerId: string) => {
    const generation = ++detailGeneration.current;
    setView('detail');
    setSelectedProviderId(providerId);
    setSelectedProvider(null);
    setDetailLoading(true);
    setDetailError(null);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    try {
      const provider = await apiCall(
        (token) => gatewayClient.getProjectByokProviderDraft(
          token, projectId, providerId, selectedTenant,
        ),
        binding,
      );
      if (generation !== detailGeneration.current) return;
      const checked = requireProjectDraft(provider, projectId);
      setSelectedProvider(checked);
      setProviders((current) => current.map((item) => item.id === providerId ? checked : item));
    } catch (reason) {
      if (generation === detailGeneration.current && !handleAccessFailure(reason))
        setDetailError(reason);
    } finally {
      if (generation === detailGeneration.current) setDetailLoading(false);
    }
  }, [apiCall, binding, handleAccessFailure, projectId, selectedTenant]);

  const startCreate = () => {
    setSelectedProvider(null);
    setSelectedProviderId(null);
    setEditor(emptyEditor());
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    setView('create');
  };

  const startEdit = () => {
    if (!selectedProvider) return;
    setEditor(editorFromDraft(selectedProvider));
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
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
    const fingerprint = JSON.stringify({
      projectId,
      providerId: isUpdate ? selectedProvider?.id ?? null : null,
      requestBody,
    });
    const operationId = pendingSave.current?.fingerprint === fingerprint
      ? pendingSave.current.operationId
      : crypto.randomUUID();
    pendingSave.current = { fingerprint, operationId };
    const request: ProjectModelProviderDraftSaveRequest = { operationId, ...requestBody };

    setSaving(true);
    setSaveError(null);
    setSaveConflict(false);
    setSavedMessage(null);
    try {
      const saved = await apiCall(
        (token) => isUpdate && selectedProvider
          ? gatewayClient.updateProjectByokProviderDraft(
            token, projectId, selectedProvider.id, request, selectedTenant,
          )
          : gatewayClient.createProjectByokProviderDraft(token, projectId, request, selectedTenant),
        binding,
      );
      const checked = requireProjectDraft(saved, projectId);
      setSelectedProvider(checked);
      setSelectedProviderId(checked.id);
      setView('detail');
      pendingSave.current = null;
      void openProvider(checked.id);
      setSavedMessage('Provider draft saved.');
      void loadPage(null, false, selectedTenant);
    } catch (reason) {
      if (!handleAccessFailure(reason)) {
        setSaveError(reason);
        setSaveConflict(isUpdate && reason instanceof GatewayError && reason.status === 409);
      }
    } finally {
      setSaving(false);
    }
  };

  const updateEditor = (field: keyof ProviderEditor, value: string) => {
    setEditor((current) => ({ ...current, [field]: value }));
    setSaveError(null);
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
      description="Manage provider metadata for this project. This form does not accept or store API keys."
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
                <Button onClick={() => setView('list')}>Back to provider drafts</Button>
                <Button appearance="primary" onClick={startEdit} disabled={!selectedProvider || detailLoading}>
                  Edit metadata
                </Button>
              </div>
              {savedMessage && <Text role="status">{savedMessage}</Text>}
              {detailLoading && <Loading label="Loading provider draft…" />}
              {detailError != null && (
                <>
                  <ErrorNotice>{errorMessage(detailError)}</ErrorNotice>
                  <Button onClick={() => selectedProviderId && void openProvider(selectedProviderId)}>
                    Retry
                  </Button>
                </>
              )}
              {selectedProvider && (
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
                </dl>
              )}
            </>
          )}
          {(view === 'create' || view === 'edit') && (
            <form className="v1-form" onSubmit={(event) => void saveDraft(event)}>
              <div className="v1-actions">
                <Button type="button" onClick={() => setView(selectedProvider ? 'detail' : 'list')}>
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
