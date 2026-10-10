import { Button, Field, Input, MessageBar, MessageBarBody, Spinner } from '@fluentui/react-components';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { GatewayError } from './api';
import type {
  MarketplaceBrowsePage,
  MarketplaceBrowseRequest,
  MarketplaceSource,
  MarketplaceSourceInput,
  MarketplaceSourceUpdateInput,
  ProjectSkillAssignment,
  SkillAssignmentRequest,
  SkillContentCandidateRequest,
  SkillContentImportReceipt,
  SkillContentImportRequest,
  SkillContentPreview,
  SkillContentResourceRequest,
  VersionedProjectConfiguration,
} from './contracts';

export interface SkillsMarketplaceAgent {
  agentId: string;
  displayName: string;
}

export interface SkillsMarketplaceActions {
  listSources(projectId: string): Promise<MarketplaceSource[]>;
  createSource(projectId: string, request: MarketplaceSourceInput): Promise<MarketplaceSource>;
  updateSource(
    projectId: string,
    sourceId: string,
    request: MarketplaceSourceUpdateInput,
  ): Promise<MarketplaceSource>;
  removeSource(
    projectId: string,
    sourceId: string,
    expectedRevision: number,
  ): Promise<MarketplaceSource>;
  browseSource(
    projectId: string,
    sourceId: string,
    request: MarketplaceBrowseRequest,
  ): Promise<MarketplaceBrowsePage>;
  previewSkillContent(candidate: SkillContentCandidateRequest): Promise<SkillContentPreview>;
  importSkillContent(
    projectId: string,
    request: SkillContentImportRequest,
  ): Promise<SkillContentImportReceipt>;
  updateSkillAssignment(
    projectId: string,
    skillId: string,
    request: SkillAssignmentRequest,
  ): Promise<VersionedProjectConfiguration>;
}

interface SkillsMarketplacePanelProps {
  projectId: string;
  configurationRevision: number | null;
  assignments: readonly ProjectSkillAssignment[];
  agents: readonly SkillsMarketplaceAgent[];
  actions: SkillsMarketplaceActions | null;
  assignmentDisabledReason?: string | null;
  onAssignmentPendingChange?: (pending: boolean) => void;
  onConfigurationChanged?: (configuration: VersionedProjectConfiguration) => void;
}

type SourceDraft = {
  name: string;
  repository: string;
  requestedRef: string;
  subpath: string;
};

type SourceLoadState = 'loading' | 'loaded' | 'unavailable';

function messageForError(reason: unknown): string {
  if (reason instanceof GatewayError) {
    const code = reason.code ? ` (${reason.code})` : '';
    return `HTTP ${reason.status}${code}: ${reason.message}`;
  }
  return reason instanceof Error ? reason.message : 'The request failed unexpectedly.';
}

function sourceDraft(source?: MarketplaceSource): SourceDraft {
  return {
    name: source?.name ?? '',
    repository: source?.repository ?? '',
    requestedRef: source?.requestedRef ?? 'main',
    subpath: source?.subpath ?? '',
  };
}

function sourceInput(draft: SourceDraft): MarketplaceSourceInput {
  const request: MarketplaceSourceInput = { repository: draft.repository.trim() };
  if (draft.name.trim()) request.name = draft.name.trim();
  if (draft.requestedRef.trim()) request.requestedRef = draft.requestedRef.trim();
  if (draft.subpath.trim()) request.subpath = draft.subpath.trim();
  return request;
}

function validateSource(
  source: MarketplaceSource,
  expectedState: MarketplaceSource['state'],
  previousRevision?: number,
): void {
  if (!source ||
      typeof source.sourceId !== 'string' || !source.sourceId ||
      !Number.isSafeInteger(source.revision) || source.revision < 1 ||
      source.state !== expectedState ||
      (previousRevision !== undefined && source.revision <= previousRevision)) {
    throw new Error('Projects & Config returned an invalid marketplace source response.');
  }
}

async function listMarketplaceSources(
  actions: SkillsMarketplaceActions,
  projectId: string,
): Promise<MarketplaceSource[]> {
  const result = await actions.listSources(projectId);
  if (!Array.isArray(result) || result.some((source) =>
    !source || typeof source.sourceId !== 'string' || !Number.isSafeInteger(source.revision))) {
    throw new Error('Projects & Config returned an invalid marketplace source list.');
  }
  return result;
}

function relativeFilePath(file: File): string {
  return file.webkitRelativePath || file.name;
}

function fileName(path: string): string {
  return path.replaceAll('\\', '/').split('/').at(-1) ?? '';
}

async function fileToBase64(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return globalThis.btoa(binary);
}

async function buildCandidate(files: readonly File[]): Promise<SkillContentCandidateRequest> {
  const manifests = files.filter((file) => fileName(relativeFilePath(file)) === 'SKILL.md');
  if (manifests.length !== 1)
    throw new Error('Select exactly one SKILL.md file and its text resources.');

  const manifest = manifests[0];
  const manifestPath = relativeFilePath(manifest).replaceAll('\\', '/');
  const manifestRoot = manifestPath.slice(0, manifestPath.lastIndexOf('/') + 1);
  const resources: SkillContentResourceRequest[] = [];
  const resourcePaths = new Set<string>();
  for (const file of files) {
    if (file === manifest) continue;
    const selectedPath = relativeFilePath(file).replaceAll('\\', '/');
    if (manifestRoot && !selectedPath.startsWith(manifestRoot))
      throw new Error('Select resource files from the same folder as SKILL.md.');
    const relativePath = manifestRoot ? selectedPath.slice(manifestRoot.length) : selectedPath;
    if (!relativePath || resourcePaths.has(relativePath.toLocaleLowerCase()))
      throw new Error('The selected files contain a duplicate or empty resource path.');
    resourcePaths.add(relativePath.toLocaleLowerCase());
    resources.push({ relativePath, content: await fileToBase64(file) });
  }

  return {
    skillMarkdown: await fileToBase64(manifest),
    resources,
  };
}

function makeIdempotencyKey(): string {
  if (!globalThis.crypto?.randomUUID)
    throw new Error('This browser cannot create a safe import request ID.');
  return globalThis.crypto.randomUUID();
}

export function SkillsMarketplacePanel({
  projectId,
  configurationRevision,
  assignments,
  agents,
  actions,
  assignmentDisabledReason,
  onAssignmentPendingChange,
  onConfigurationChanged,
}: SkillsMarketplacePanelProps) {
  const activeProjectId = useRef(projectId);
  const sourceRequest = useRef(0);
  const browseRequest = useRef(0);
  const skillRequest = useRef(0);
  const folderInputRef = useCallback((input: HTMLInputElement | null) => {
    input?.setAttribute('webkitdirectory', '');
  }, []);
  const [sources, setSources] = useState<MarketplaceSource[] | null>(null);
  const [sourcesProjectId, setSourcesProjectId] = useState<string | null>(null);
  const [sourceError, setSourceError] = useState<string | null>(null);
  const [sourceNotice, setSourceNotice] = useState<string | null>(null);
  const [sourceBusy, setSourceBusy] = useState<string | null>(null);
  const [sourceDrafts, setSourceDrafts] = useState<Record<string, SourceDraft>>({});
  const [newSource, setNewSource] = useState<SourceDraft>(() => sourceDraft());
  const [selectedSourceId, setSelectedSourceId] = useState('');
  const [browseQuery, setBrowseQuery] = useState('');
  const [browsePage, setBrowsePage] = useState<MarketplaceBrowsePage | null>(null);
  const [browsing, setBrowsing] = useState(false);
  const [skillError, setSkillError] = useState<string | null>(null);
  const [skillNotice, setSkillNotice] = useState<string | null>(null);
  const [skillBusy, setSkillBusy] = useState<string | null>(null);
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [candidate, setCandidate] = useState<SkillContentCandidateRequest | null>(null);
  const [preview, setPreview] = useState<SkillContentPreview | null>(null);
  const [importReceipt, setImportReceipt] = useState<SkillContentImportReceipt | null>(null);
  const [pendingImportKey, setPendingImportKey] = useState<string | null>(null);
  const [selectedAgentIds, setSelectedAgentIds] = useState<string[]>([]);
  const [assignmentSnapshot, setAssignmentSnapshot] = useState<{
    projectId: string;
    revision: number | null;
    skills: readonly ProjectSkillAssignment[];
  }>({ projectId, revision: configurationRevision, skills: assignments });

  const refreshSources = useCallback(async () => {
    if (!actions) return;

    const scope = projectId;
    const requestId = ++sourceRequest.current;
    try {
      const result = await listMarketplaceSources(actions, scope);
      if (activeProjectId.current !== scope || sourceRequest.current !== requestId) return;
      setSources(result);
      setSourcesProjectId(scope);
      setSourceError(null);
    } catch (reason) {
      if (activeProjectId.current !== scope || sourceRequest.current !== requestId) return;
      setSources(null);
      setSourcesProjectId(scope);
      setSourceError(messageForError(reason));
    }
  }, [actions, projectId]);

  useLayoutEffect(() => {
    if (activeProjectId.current === projectId) return;
    activeProjectId.current = projectId;
    sourceRequest.current += 1;
    browseRequest.current += 1;
    skillRequest.current += 1;
    setSources(null);
    setSourcesProjectId(null);
    setSourceError(null);
    setSourceNotice(null);
    setSourceDrafts({});
    setNewSource(sourceDraft());
    setSelectedSourceId('');
    setBrowsePage(null);
    setBrowsing(false);
    setSkillError(null);
    setSkillNotice(null);
    setSelectedFiles([]);
    setCandidate(null);
    setPreview(null);
    setImportReceipt(null);
    setPendingImportKey(null);
    setSelectedAgentIds([]);
  }, [actions, assignments, configurationRevision, projectId]);

  useEffect(() => {
    if (!actions) return;
    const scope = projectId;
    const requestId = ++sourceRequest.current;
    void listMarketplaceSources(actions, scope).then((result) => {
      if (activeProjectId.current !== scope || sourceRequest.current !== requestId) return;
      setSources(result);
      setSourcesProjectId(scope);
      setSourceError(null);
    }).catch((reason: unknown) => {
      if (activeProjectId.current !== scope || sourceRequest.current !== requestId) return;
      setSources(null);
      setSourcesProjectId(scope);
      setSourceError(messageForError(reason));
    });
  }, [actions, projectId]);

  if (assignmentSnapshot.projectId !== projectId ||
      (configurationRevision !== null &&
        (assignmentSnapshot.revision === null || configurationRevision > assignmentSnapshot.revision))) {
    setAssignmentSnapshot({ projectId, revision: configurationRevision, skills: assignments });
  }

  const currentSources = actions && sourcesProjectId === projectId ? sources : null;
  const currentSelectedSourceId = currentSources?.some((source) => source.sourceId === selectedSourceId)
    ? selectedSourceId
    : currentSources?.[0]?.sourceId ?? '';
  const sourceLoadState: SourceLoadState = !actions
    ? 'unavailable'
    : sourcesProjectId === projectId ? 'loaded' : 'loading';
  const selectedSource = currentSources?.find((source) => source.sourceId === currentSelectedSourceId) ?? null;
  const currentBrowsePage = browsePage &&
      selectedSource &&
      browsePage.sourceId === selectedSource.sourceId &&
      browsePage.sourceRevision === selectedSource.revision
    ? browsePage
    : null;
  const currentAssignments = assignmentSnapshot.projectId === projectId
    ? assignmentSnapshot.skills
    : assignments;
  const currentConfigurationRevision = assignmentSnapshot.projectId === projectId
    ? assignmentSnapshot.revision
    : configurationRevision;

  const updateSourceDraft = (sourceId: string, field: keyof SourceDraft, value: string) => {
    const source = currentSources?.find((item) => item.sourceId === sourceId);
    if (!source) return;
    setSourceDrafts((current) => ({
      ...current,
      [sourceId]: { ...(current[sourceId] ?? sourceDraft(source)), [field]: value },
    }));
  };

  const addSource = async (event: FormEvent) => {
    event.preventDefault();
    if (!actions || !projectId || !newSource.repository.trim()) return;
    const scope = projectId;
    setSourceBusy('create');
    setSourceError(null);
    setSourceNotice(null);
    try {
      const created = await actions.createSource(scope, sourceInput(newSource));
      if (activeProjectId.current !== scope) return;
      validateSource(created, 'active');
      setNewSource(sourceDraft());
      setSourceNotice(`Source ${created.repository} was saved at revision ${created.revision}.`);
      setBrowsePage(null);
      await refreshSources();
    } catch (reason) {
      if (activeProjectId.current === scope) setSourceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope) setSourceBusy(null);
    }
  };

  const updateSource = async (event: FormEvent, source: MarketplaceSource) => {
    event.preventDefault();
    if (!actions || source.state !== 'active') return;
    const scope = projectId;
    const draft = sourceDrafts[source.sourceId] ?? sourceDraft(source);
    setSourceBusy(`update:${source.sourceId}`);
    setSourceError(null);
    setSourceNotice(null);
    try {
      const updated = await actions.updateSource(scope, source.sourceId, {
        ...sourceInput(draft),
        expectedRevision: source.revision,
      });
      if (activeProjectId.current !== scope) return;
      validateSource(updated, 'active', source.revision);
      setSourceNotice(`Source ${updated.repository} was saved at revision ${updated.revision}.`);
      setBrowsePage(null);
      await refreshSources();
    } catch (reason) {
      if (activeProjectId.current === scope) setSourceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope) setSourceBusy(null);
    }
  };

  const removeSource = async (source: MarketplaceSource) => {
    if (!actions || source.state !== 'active') return;
    const scope = projectId;
    setSourceBusy(`remove:${source.sourceId}`);
    setSourceError(null);
    setSourceNotice(null);
    try {
      const removed = await actions.removeSource(scope, source.sourceId, source.revision);
      if (activeProjectId.current !== scope) return;
      validateSource(removed, 'removed', source.revision);
      setSourceNotice(`Source ${removed.repository} was marked removed at revision ${removed.revision}.`);
      setBrowsePage(null);
      await refreshSources();
    } catch (reason) {
      if (activeProjectId.current === scope) setSourceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope) setSourceBusy(null);
    }
  };

  const browse = async (page: number) => {
    if (!actions || !selectedSource || selectedSource.state !== 'active') return;
    const scope = projectId;
    const source = selectedSource;
    const requestId = ++browseRequest.current;
    setBrowsing(true);
    setSourceError(null);
    setBrowsePage(null);
    try {
      const result = await actions.browseSource(scope, source.sourceId, {
        expectedSourceRevision: source.revision,
        query: browseQuery.trim() || undefined,
        page,
        pageSize: 25,
      });
      if (activeProjectId.current !== scope || browseRequest.current !== requestId) return;
      if (result.sourceId !== source.sourceId ||
          result.sourceRevision !== source.revision ||
          result.page !== page ||
          !Number.isSafeInteger(result.total) ||
          !Array.isArray(result.candidates)) {
        throw new Error('Projects & Config returned browse results for a different source revision or page.');
      }
      setBrowsePage(result);
    } catch (reason) {
      if (activeProjectId.current === scope && browseRequest.current === requestId)
        setSourceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && browseRequest.current === requestId)
        setBrowsing(false);
    }
  };

  const previewLocalSkill = async () => {
    if (!actions || selectedFiles.length === 0) return;
    const scope = projectId;
    const requestId = ++skillRequest.current;
    setSkillBusy('preview');
    setSkillError(null);
    setSkillNotice(null);
    setCandidate(null);
    setPreview(null);
    setImportReceipt(null);
    setPendingImportKey(null);
    try {
      const nextCandidate = await buildCandidate(selectedFiles);
      if (activeProjectId.current !== scope || skillRequest.current !== requestId) return;
      const result = await actions.previewSkillContent(nextCandidate);
      if (activeProjectId.current !== scope || skillRequest.current !== requestId) return;
      if (!result.contentDigest || !result.name ||
          !Number.isSafeInteger(result.resourceCount) || result.resourceCount < 0 ||
          !Number.isSafeInteger(result.totalBytes) || result.totalBytes < 0) {
        throw new Error('The Skills owner returned an invalid preview receipt.');
      }
      setCandidate(nextCandidate);
      setPreview(result);
      setSkillNotice('The Skills owner validated these files without importing them.');
    } catch (reason) {
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillBusy(null);
    }
  };

  const importLocalSkill = async () => {
    if (!actions || !candidate || !preview || importReceipt) return;
    const scope = projectId;
    const requestId = ++skillRequest.current;
    setSkillBusy('import');
    setSkillError(null);
    setSkillNotice(null);
    let idempotencyKey = pendingImportKey;
    try {
      if (!idempotencyKey) {
        idempotencyKey = makeIdempotencyKey();
        setPendingImportKey(idempotencyKey);
      }
      const result = await actions.importSkillContent(scope, {
        idempotencyKey,
        expectedContentDigest: preview.contentDigest,
        candidate,
      });
      if (activeProjectId.current !== scope || skillRequest.current !== requestId) return;
      if (!result.skillId || result.revision < 1 ||
          result.contentDigest !== preview.contentDigest ||
          result.resourceCount !== preview.resourceCount) {
        throw new Error('The Skills owner returned an import receipt that does not match the preview.');
      }
      setImportReceipt(result);
      setSkillNotice(`The Skills owner imported ${result.name} at revision ${result.revision}.`);
    } catch (reason) {
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillBusy(null);
    }
  };

  const toggleAgent = (agentId: string) => {
    setSelectedAgentIds((current) => current.includes(agentId)
      ? current.filter((selected) => selected !== agentId)
      : [...current, agentId]);
  };

  const assignImportedSkill = async (event: FormEvent) => {
    event.preventDefault();
    if (!actions || !importReceipt || currentConfigurationRevision === null ||
        selectedAgentIds.length === 0) return;
    if (assignmentDisabledReason) {
      setSkillError(assignmentDisabledReason);
      return;
    }
    const scope = projectId;
    const requestId = ++skillRequest.current;
    const existing = currentAssignments.find((item) => item.skillId === importReceipt.skillId);
    const order = existing?.order ?? Math.max(-1, ...currentAssignments.map((item) => item.order)) + 1;
    setSkillBusy('assignment');
    onAssignmentPendingChange?.(true);
    setSkillError(null);
    setSkillNotice(null);
    try {
      const result = await actions.updateSkillAssignment(scope, importReceipt.skillId, {
        expectedProjectConfigurationRevision: currentConfigurationRevision,
        revision: importReceipt.revision,
        contentDigest: importReceipt.contentDigest,
        enabled: true,
        order,
        agentIds: selectedAgentIds,
      });
      if (activeProjectId.current !== scope) return;
      if (result.projectId !== scope ||
          !Number.isSafeInteger(result.revision) ||
          result.revision <= currentConfigurationRevision) {
        throw new Error('Projects & Config returned an assignment for a different project or configuration revision.');
      }
      const savedAssignment = result.configuration.skills.find((item) =>
        item.skillId === importReceipt.skillId);
      if (!savedAssignment ||
          savedAssignment.enabled !== true ||
          savedAssignment.order !== order ||
          savedAssignment.revision !== importReceipt.revision ||
          savedAssignment.contentDigest !== importReceipt.contentDigest ||
          !savedAssignment.agentIds ||
          savedAssignment.agentIds.length !== selectedAgentIds.length ||
          selectedAgentIds.some((agentId) => !savedAssignment.agentIds?.includes(agentId))) {
        throw new Error('Projects & Config returned a revision without the requested skill assignment.');
      }
      setAssignmentSnapshot({
        projectId: scope,
        revision: result.revision,
        skills: result.configuration.skills,
      });
      onConfigurationChanged?.(result);
      setSkillNotice(
        `The owner saved this assignment in configuration revision ${result.revision}. This does not prove a runtime loaded it.`,
      );
    } catch (reason) {
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillError(messageForError(reason));
    } finally {
      onAssignmentPendingChange?.(false);
      if (activeProjectId.current === scope && skillRequest.current === requestId)
        setSkillBusy(null);
    }
  };

  return (
    <section className="v1-panel v1-stack" aria-labelledby="skills-marketplace-heading">
      <div className="v1-panel-heading">
        <h2 id="skills-marketplace-heading">Skills and marketplace</h2>
      </div>
      <p className="v1-muted">
        The signed-in actor and current project are checked by the owner on every request. A source marked active is configured; it does not confirm that GitHub is available.
      </p>
      {!actions && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Skills and marketplace actions are unavailable because no authorized Gateway actions were supplied. No source, import, assignment, or runtime state is assumed.
          </MessageBarBody>
        </MessageBar>
      )}

      <section className="v1-stack" aria-labelledby="marketplace-sources-heading">
        <h3 id="marketplace-sources-heading">Marketplace sources</h3>
        {sourceError && <MessageBar intent="error" role="alert"><MessageBarBody>{sourceError}</MessageBarBody></MessageBar>}
        {sourceNotice && <MessageBar intent="success"><MessageBarBody>{sourceNotice}</MessageBarBody></MessageBar>}
        <p className="v1-muted">
          Sources must be public GitHub repositories. Browse results pin a source revision and resolved commit. Browsing reads current-page manifests only; it does not import resource files.
        </p>
        {sourceLoadState === 'loading' && <Spinner label="Loading marketplace sources…" />}
        {sourceLoadState === 'unavailable' && (
          <p className="v1-warning">Marketplace source operations are unavailable through the current Gateway contract.</p>
        )}
        {sourceLoadState === 'loaded' && currentSources === null && !sourceError && (
          <p className="v1-warning">The source list is not available for this project.</p>
        )}
        {currentSources && currentSources.length === 0 && !sourceError && (
          <p className="v1-muted">No active marketplace sources were returned for this project.</p>
        )}
        {currentSources && currentSources.length > 0 && (
          <div className="v1-record-list">
            {currentSources.map((source) => {
              const draft = sourceDrafts[source.sourceId] ?? sourceDraft(source);
              const busy = sourceBusy !== null;
              return (
                <article className="v1-record" key={source.sourceId}>
                  <form className="v1-form" onSubmit={(event) => void updateSource(event, source)}>
                    <div className="v1-inline-form">
                      <Field label="Source name">
                        <Input value={draft.name} onChange={(_, data) => updateSourceDraft(source.sourceId, 'name', data.value)} />
                      </Field>
                      <Field label="Public GitHub repository" hint="Use owner/repository.">
                        <Input value={draft.repository} onChange={(_, data) => updateSourceDraft(source.sourceId, 'repository', data.value)} />
                      </Field>
                      <Field label="Requested ref">
                        <Input value={draft.requestedRef} onChange={(_, data) => updateSourceDraft(source.sourceId, 'requestedRef', data.value)} />
                      </Field>
                      <Field label="Repository subpath" hint="Leave blank to browse from the repository root.">
                        <Input value={draft.subpath} onChange={(_, data) => updateSourceDraft(source.sourceId, 'subpath', data.value)} />
                      </Field>
                    </div>
                    <p className="v1-muted">
                      {source.repository} · {source.requestedRef} · revision {source.revision} · {source.state}
                    </p>
                    <div className="v1-actions">
                      <Button appearance="secondary" type="submit" disabled={!actions || busy || source.state !== 'active' || !draft.repository.trim()}>
                        {sourceBusy === `update:${source.sourceId}` ? 'Saving…' : 'Save source'}
                      </Button>
                      <Button
                        appearance="secondary"
                        type="button"
                        disabled={!actions || busy || source.state !== 'active'}
                        onClick={() => void removeSource(source)}
                      >
                        {sourceBusy === `remove:${source.sourceId}` ? 'Removing…' : 'Remove source'}
                      </Button>
                    </div>
                  </form>
                </article>
              );
            })}
          </div>
        )}
        <form className="v1-form" onSubmit={(event) => void addSource(event)}>
          <h4>Add a public source</h4>
          <div className="v1-inline-form">
            <Field label="Source name">
              <Input value={newSource.name} onChange={(_, data) => setNewSource((current) => ({ ...current, name: data.value }))} />
            </Field>
            <Field label="Public GitHub repository" hint="Use owner/repository.">
              <Input value={newSource.repository} onChange={(_, data) => setNewSource((current) => ({ ...current, repository: data.value }))} />
            </Field>
            <Field label="Requested ref">
              <Input value={newSource.requestedRef} onChange={(_, data) => setNewSource((current) => ({ ...current, requestedRef: data.value }))} />
            </Field>
            <Field label="Repository subpath" hint="Leave blank to browse from the repository root.">
              <Input value={newSource.subpath} onChange={(_, data) => setNewSource((current) => ({ ...current, subpath: data.value }))} />
            </Field>
          </div>
          <div className="v1-actions">
            <Button appearance="primary" type="submit" disabled={!actions || sourceBusy !== null || !newSource.repository.trim()}>
              {sourceBusy === 'create' ? 'Adding…' : 'Add source'}
            </Button>
            <Button appearance="secondary" type="button" disabled={!actions || sourceBusy !== null} onClick={() => {
              setSourceError(null);
              void refreshSources();
            }}>
              Refresh sources
            </Button>
          </div>
        </form>
        <form className="v1-form" onSubmit={(event) => {
          event.preventDefault();
          void browse(1);
        }}>
          <h4>Browse a source</h4>
          <Field label="Marketplace source">
            <select
              className="v1-select"
              aria-label="Marketplace source"
              value={currentSelectedSourceId}
              onChange={(event) => {
                setSelectedSourceId(event.currentTarget.value);
                setBrowsePage(null);
              }}
              disabled={!currentSources?.length}
            >
              <option value="">Select a source</option>
              {currentSources?.map((source) => (
                <option key={source.sourceId} value={source.sourceId}>
                  {source.name} · {source.repository}
                </option>
              ))}
            </select>
          </Field>
          <div className="v1-inline-form">
            <Field label="Search skills" hint="Matches skill name or repository location.">
              <Input
                value={browseQuery}
                maxLength={128}
                onChange={(_, data) => setBrowseQuery(data.value)}
              />
            </Field>
            <div className="v1-actions">
              <Button appearance="primary" type="submit" disabled={!actions || !selectedSource || browsing}>
                {browsing ? 'Browsing…' : 'Browse skills'}
              </Button>
            </div>
          </div>
        </form>
        {browsing && <Spinner label="Browsing the selected source revision…" />}
        {currentBrowsePage && (
          <div className="v1-stack" aria-label="Marketplace browse results">
            <p className="v1-muted">
              Ref {currentBrowsePage.requestedRef} · source revision {currentBrowsePage.sourceRevision} · commit <code>{currentBrowsePage.resolvedCommitSha}</code> · {currentBrowsePage.total} result(s)
            </p>
            {currentBrowsePage.candidates.length === 0 ? (
              <p className="v1-muted">No skills matched this search on the selected source revision.</p>
            ) : (
              <div className="v1-record-list">
                {currentBrowsePage.candidates.map((item) => (
                  <article className="v1-record" key={`${item.location}/${item.name}`}>
                    <h4>{item.name}</h4>
                    <p className="v1-muted">{item.location || 'Repository root'}</p>
                    {item.description && <p>{item.description}</p>}
                    <Button appearance="secondary" disabled aria-describedby="marketplace-import-unavailable">
                      Import source skill
                    </Button>
                  </article>
                ))}
              </div>
            )}
            <p id="marketplace-import-unavailable" className="v1-warning">
              Source import is unavailable. The owner does not yet expose a selected-content preview that returns the pinned manifest and resources for Skills validation.
            </p>
            <div className="v1-actions">
              <Button
                appearance="secondary"
                disabled={!actions || browsing || currentBrowsePage.page <= 1}
                onClick={() => void browse(currentBrowsePage.page - 1)}
              >
                Previous page
              </Button>
              <span className="v1-muted">Page {currentBrowsePage.page} · {currentBrowsePage.pageSize} per page</span>
              <Button
                appearance="secondary"
                disabled={!actions || browsing || !currentBrowsePage.hasMore}
                onClick={() => void browse(currentBrowsePage.page + 1)}
              >
                Next page
              </Button>
            </div>
          </div>
        )}
      </section>

      <section className="v1-stack" aria-labelledby="local-skill-heading">
        <h3 id="local-skill-heading">Upload a local skill</h3>
        <p className="v1-muted">
          Select a folder that contains one SKILL.md and its text resources, or select those files together. The Skills owner checks file content and paths before import.
        </p>
        {skillError && <MessageBar intent="error" role="alert"><MessageBarBody>{skillError}</MessageBarBody></MessageBar>}
        {skillNotice && <MessageBar intent="success"><MessageBarBody>{skillNotice}</MessageBarBody></MessageBar>}
        <Field label="Skill folder or files" hint="Include exactly one file named SKILL.md.">
          <input
            ref={folderInputRef}
            className="v1-select"
            type="file"
            multiple
            aria-label="Skill folder or files"
            disabled={!actions || skillBusy !== null}
            onChange={(event) => {
              if (skillBusy !== null) return;
              setSelectedFiles(Array.from(event.currentTarget.files ?? []));
              setCandidate(null);
              setPreview(null);
              setImportReceipt(null);
              setPendingImportKey(null);
              setSkillError(null);
              setSkillNotice(null);
            }}
          />
        </Field>
        <p className="v1-muted">{selectedFiles.length} file(s) selected.</p>
        <div className="v1-actions">
          <Button
            appearance="primary"
            type="button"
            disabled={!actions || selectedFiles.length === 0 || skillBusy !== null}
            onClick={() => void previewLocalSkill()}
          >
            {skillBusy === 'preview' ? 'Checking files…' : 'Validate skill files'}
          </Button>
          <Button
            appearance="secondary"
            type="button"
            disabled={!actions || !preview || !candidate || skillBusy !== null || importReceipt !== null}
            onClick={() => void importLocalSkill()}
          >
            {skillBusy === 'import' ? 'Importing…' : importReceipt ? 'Imported' : 'Import skill to project'}
          </Button>
        </div>
        {preview && (
          <div className="v1-detail-list" aria-label="Skill preview">
            <div><dt>Validated skill</dt><dd>{preview.name}</dd></div>
            <div><dt>Resources</dt><dd>{preview.resourceCount}</dd></div>
            <div><dt>Content digest</dt><dd><code>{preview.contentDigest}</code></dd></div>
            <div><dt>Total bytes</dt><dd>{preview.totalBytes}</dd></div>
          </div>
        )}
        {importReceipt && (
          <p>
            Imported <strong>{importReceipt.name}</strong> · revision {importReceipt.revision} · digest <code>{importReceipt.contentDigest}</code>
          </p>
        )}
      </section>

      <section className="v1-stack" aria-labelledby="skill-assignment-heading">
        <h3 id="skill-assignment-heading">Project assignments</h3>
        <p className="v1-muted">
          An assignment changes the next project configuration revision. Accepted runs keep their own immutable configuration.
        </p>
        {assignmentDisabledReason && <p className="v1-warning">{assignmentDisabledReason}</p>}
        {currentConfigurationRevision === null && (
          <p className="v1-warning">Project configuration is not available, so assignment is disabled.</p>
        )}
        {currentAssignments.length === 0 ? (
          <p className="v1-muted">No skill assignments were returned by the current project configuration.</p>
        ) : (
          <div className="v1-record-list">
            {currentAssignments.map((assignment) => (
              <article className="v1-record" key={assignment.skillId}>
                <strong>{assignment.skillId}</strong>
                <p className="v1-muted">
                  {assignment.enabled ? 'Enabled' : 'Disabled'} · order {assignment.order}
                  {assignment.revision ? ` · revision ${assignment.revision}` : ' · no imported content revision'}
                  {assignment.agentIds?.length ? ` · ${assignment.agentIds.length} agent(s)` : ''}
                </p>
                {!assignment.revision || !assignment.contentDigest || !assignment.agentIds?.length ? (
                  <p className="v1-warning">This entry has no complete content pin and is not returned as accepted runtime content.</p>
                ) : null}
              </article>
            ))}
          </div>
        )}
        {importReceipt && (
          <form className="v1-form" onSubmit={(event) => void assignImportedSkill(event)}>
            <h4>Assign {importReceipt.name}</h4>
            {agents.length === 0 ? (
              <p className="v1-warning">No active project agents were returned. This skill cannot be assigned.</p>
            ) : (
              <fieldset className="v1-form">
                <legend>Active project agents</legend>
                {agents.map((agent) => (
                  <label className="v1-checkbox" key={agent.agentId}>
                    <input
                      type="checkbox"
                      checked={selectedAgentIds.includes(agent.agentId)}
                      onChange={() => toggleAgent(agent.agentId)}
                    />
                    <span>{agent.displayName}</span>
                  </label>
                ))}
              </fieldset>
            )}
            <div className="v1-actions">
              <Button
                appearance="primary"
                type="submit"
                disabled={!actions || currentConfigurationRevision === null ||
                  selectedAgentIds.length === 0 || agents.length === 0 || skillBusy !== null ||
                  assignmentDisabledReason !== undefined && assignmentDisabledReason !== null}
              >
                {skillBusy === 'assignment' ? 'Saving assignment…' : 'Assign to selected agents'}
              </Button>
            </div>
          </form>
        )}
        <MessageBar intent="info">
          <MessageBarBody>
            Runtime load status is not available from this API. A saved assignment or accepted configuration does not prove that the AgentHost loaded a skill.
          </MessageBarBody>
        </MessageBar>
      </section>
    </section>
  );
}
