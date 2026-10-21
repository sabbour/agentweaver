import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
  Field, Input, MessageBar, MessageBarBody, Spinner, Tab, TabList,
} from '@fluentui/react-components';
import { ArrowUpload24Regular, PuzzlePiece20Regular } from '@fluentui/react-icons';
import { MetricRow } from '../components/ui/Metric';
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { GatewayError } from './api';
import type {
  MarketplaceBrowsePage,
  MarketplaceBrowseRequest,
  MarketplaceSkillCandidate,
  MarketplaceSkillContentImportRequest,
  MarketplaceSkillContentPreviewRequest,
  MarketplaceSource,
  MarketplaceSourceInput,
  MarketplaceSourceUpdateInput,
  ProjectSkillAssignment,
  SkillAssignmentRequest,
  SkillContentCandidateRequest,
  SkillContentInventoryPage,
  SkillContentInventoryQuery,
  SkillContentImportReceipt,
  SkillContentImportRequest,
  SkillContentPreview,
  SkillContentResourceRequest,
  VersionedProjectConfiguration,
} from './contracts';

/** Agent choices shown when assigning an imported skill. */
export interface SkillsMarketplaceAgent {
  agentId: string;
  displayName: string;
}

/** Owner operations supplied by the project's configuration route. */
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
  previewMarketplaceSkill(
    projectId: string,
    sourceId: string,
    request: MarketplaceSkillContentPreviewRequest,
  ): Promise<SkillContentPreview>;
  importMarketplaceSkill(
    projectId: string,
    sourceId: string,
    request: MarketplaceSkillContentImportRequest,
  ): Promise<SkillContentImportReceipt>;
  previewSkillContent(candidate: SkillContentCandidateRequest): Promise<SkillContentPreview>;
  importSkillContent(
    projectId: string,
    request: SkillContentImportRequest,
  ): Promise<SkillContentImportReceipt>;
  listSkillInventory(
    projectId: string,
    request: SkillContentInventoryQuery,
  ): Promise<SkillContentInventoryPage>;
  updateSkillAssignment(
    projectId: string,
    skillId: string,
    request: SkillAssignmentRequest,
  ): Promise<VersionedProjectConfiguration>;
}

/** Inputs and callbacks required to edit one project's marketplace and skill inventory. */
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

type MarketplaceSkillSelection = {
  projectId: string;
  sourceId: string;
  sourceRevision: number;
  resolvedCommitSha: string;
  selectedPath: string;
  candidateName: string;
  query: string;
  page: number;
};

type SkillInventoryView = {
  projectId: string;
  actions: SkillsMarketplaceActions;
  page: SkillContentInventoryPage | null;
  cursors: Array<string | null>;
  loading: boolean;
  error: string | null;
};

/** Formats owner and transport errors without hiding Gateway status codes. */
function messageForError(reason: unknown): string {
  if (reason instanceof GatewayError) {
    const code = reason.code ? ` (${reason.code})` : '';
    return `HTTP ${reason.status}${code}: ${reason.message}`;
  }
  return reason instanceof Error ? reason.message : 'The request failed unexpectedly.';
}

/** Creates an editable draft from a source or the default values for a new one. */
function sourceDraft(source?: MarketplaceSource): SourceDraft {
  return {
    name: source?.name ?? '',
    repository: source?.repository ?? '',
    requestedRef: source?.requestedRef ?? 'main',
    subpath: source?.subpath ?? '',
  };
}

/** Trims the editor draft and omits optional blank source fields. */
function sourceInput(draft: SourceDraft): MarketplaceSourceInput {
  const request: MarketplaceSourceInput = { repository: draft.repository.trim() };
  if (draft.name.trim()) request.name = draft.name.trim();
  if (draft.requestedRef.trim()) request.requestedRef = draft.requestedRef.trim();
  if (draft.subpath.trim()) request.subpath = draft.subpath.trim();
  return request;
}

/** Rejects stale or malformed source results before changing the editor state. */
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
    throw new Error('The server returned an invalid marketplace source response.');
  }
}

/** Loads source records and validates the owner response shape. */
async function listMarketplaceSources(
  actions: SkillsMarketplaceActions,
  projectId: string,
): Promise<MarketplaceSource[]> {
  const result = await actions.listSources(projectId);
  if (!Array.isArray(result) || result.some((source) =>
    !source || typeof source.sourceId !== 'string' || !Number.isSafeInteger(source.revision))) {
    throw new Error('The server returned an invalid marketplace source list.');
  }
  return result;
}

/** Validates the summary returned by a non-mutating skill preview. */
function validateSkillPreview(result: SkillContentPreview): void {
  if (!result || typeof result.name !== 'string' || !result.name ||
      typeof result.description !== 'string' ||
      typeof result.contentDigest !== 'string' || !result.contentDigest ||
      !Number.isSafeInteger(result.resourceCount) || result.resourceCount < 0 ||
      !Number.isSafeInteger(result.totalBytes) || result.totalBytes < 0) {
    throw new Error('The Skills owner returned an invalid preview receipt.');
  }
}

/** Confirms the import receipt matches the exact content preview the user approved. */
function validateSkillImportReceipt(
  result: SkillContentImportReceipt,
  preview: SkillContentPreview,
): void {
  if (!result || typeof result.skillId !== 'string' || !result.skillId ||
      !Number.isSafeInteger(result.revision) || result.revision < 1 ||
      typeof result.name !== 'string' || !result.name ||
      typeof result.description !== 'string' ||
      result.name !== preview.name ||
      result.description !== preview.description ||
      result.contentDigest !== preview.contentDigest ||
      result.resourceCount !== preview.resourceCount ||
      result.totalBytes !== preview.totalBytes) {
    throw new Error('The Skills owner returned an import receipt that does not match the preview.');
  }
}

/** Validates inventory revisions, assignments, paging bounds, and revocation metadata. */
function validateSkillInventoryPage(result: SkillContentInventoryPage): void {
  const nullableString = (value: unknown) => value === null || typeof value === 'string';
  const validRevision = (value: unknown) =>
    typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
  if (!result ||
      !Number.isSafeInteger(result.configurationRevision) || result.configurationRevision < 0 ||
      !Array.isArray(result.assignments) ||
      !Array.isArray(result.revisions) ||
      !Number.isSafeInteger(result.pageSize) || result.pageSize < 1 || result.pageSize > 50 ||
      (result.nextCursor !== null &&
        (typeof result.nextCursor !== 'string' || result.nextCursor.length === 0)) ||
      result.assignments.some((assignment) =>
        !assignment || typeof assignment.skillId !== 'string' || !assignment.skillId ||
        typeof assignment.enabled !== 'boolean' ||
        !Number.isSafeInteger(assignment.order) || assignment.order < 0 ||
        (assignment.revision !== null && !validRevision(assignment.revision)) ||
        !nullableString(assignment.contentDigest) ||
        (assignment.agentIds !== null &&
          (!Array.isArray(assignment.agentIds) ||
            assignment.agentIds.some((agentId) => typeof agentId !== 'string' || !agentId)))) ||
      result.revisions.some((revision) =>
        !revision || typeof revision.skillId !== 'string' || !revision.skillId ||
        !validRevision(revision.revision) ||
        typeof revision.name !== 'string' || !revision.name ||
        typeof revision.description !== 'string' ||
        typeof revision.contentDigest !== 'string' || !revision.contentDigest ||
        !Number.isSafeInteger(revision.resourceCount) || revision.resourceCount < 0 ||
        !Number.isSafeInteger(revision.totalBytes) || revision.totalBytes < 0 ||
        typeof revision.createdByActorId !== 'string' || !revision.createdByActorId ||
        typeof revision.createdAt !== 'string' || !Number.isFinite(Date.parse(revision.createdAt)) ||
        !nullableString(revision.sourceId) ||
        !nullableString(revision.sourceRevision) ||
        !nullableString(revision.requestedRef) ||
        !nullableString(revision.resolvedCommitSha) ||
        !nullableString(revision.selectedPath) ||
        typeof revision.isRevoked !== 'boolean' ||
        !nullableString(revision.revokedByActorId) ||
        !nullableString(revision.revokedAt) ||
        (revision.revokedAt !== null && !Number.isFinite(Date.parse(revision.revokedAt))))) {
    throw new Error('The server returned an invalid skill inventory.');
  }
}

/** Gets the folder-relative path when available, otherwise the selected filename. */
function relativeFilePath(file: File): string {
  return file.webkitRelativePath || file.name;
}

/** Returns the final segment of a normalized file path. */
function fileName(path: string): string {
  return path.replaceAll('\\', '/').split('/').at(-1) ?? '';
}

/** Encodes file bytes as base64 in bounded chunks to avoid argument-size overflow. */
async function fileToBase64(file: File): Promise<string> {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  return globalThis.btoa(binary);
}

/** Builds one skill candidate, requiring one manifest and unique same-folder resources. */
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

/** Creates a fresh idempotency key or rejects browsers without a secure UUID source. */
function makeIdempotencyKey(): string {
  if (!globalThis.crypto?.randomUUID)
    throw new Error('This browser cannot create a safe import request ID.');
  return globalThis.crypto.randomUUID();
}

/**
 * Manages project marketplace sources, skill previews/imports, inventory paging,
 * and revision-fenced assignments while rejecting stale responses from prior scopes.
 */
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
  const activeActions = useRef(actions);
  const sourceRequest = useRef(0);
  const browseRequest = useRef(0);
  const inventoryRequest = useRef(0);
  const skillRequest = useRef(0);
  const marketplaceRequest = useRef(0);
  const marketplaceBusyRef = useRef<'preview' | 'import' | null>(null);
  const marketplaceImportKey = useRef<string | null>(null);
  const folderInputRef = useCallback((input: HTMLInputElement | null) => {
    input?.setAttribute('webkitdirectory', '');
  }, []);
  // Which tab is shown, and whether the marketplace or import dialog is open.
  // The dialogs hold the browse, preview, and upload flows so the page stays short.
  const [tab, setTab] = useState<'catalog' | 'assignments'>('catalog');
  const [marketplaceOpen, setMarketplaceOpen] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
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
  const [browseResultQuery, setBrowseResultQuery] = useState<string | null>(null);
  const [browsing, setBrowsing] = useState(false);
  const [skillInventory, setSkillInventory] = useState<SkillInventoryView | null>(null);
  const [marketplaceSelection, setMarketplaceSelection] = useState<MarketplaceSkillSelection | null>(null);
  const [marketplacePreview, setMarketplacePreview] = useState<SkillContentPreview | null>(null);
  const [marketplaceBusy, setMarketplaceBusy] = useState<'preview' | 'import' | null>(null);
  const [marketplaceError, setMarketplaceError] = useState<string | null>(null);
  const [marketplaceNotice, setMarketplaceNotice] = useState<string | null>(null);
  const [marketplaceImportComplete, setMarketplaceImportComplete] = useState(false);
  const [skillError, setSkillError] = useState<string | null>(null);
  const [skillNotice, setSkillNotice] = useState<string | null>(null);
  const [skillBusy, setSkillBusy] = useState<string | null>(null);
  const [selectedFiles, setSelectedFiles] = useState<File[]>([]);
  const [candidate, setCandidate] = useState<SkillContentCandidateRequest | null>(null);
  const [preview, setPreview] = useState<SkillContentPreview | null>(null);
  const [importReceipt, setImportReceipt] = useState<SkillContentImportReceipt | null>(null);
  const [importReceiptKind, setImportReceiptKind] = useState<'local' | 'marketplace' | null>(null);
  const [pendingImportKey, setPendingImportKey] = useState<string | null>(null);
  const [selectedAgentIds, setSelectedAgentIds] = useState<string[]>([]);
  const [assignmentSnapshot, setAssignmentSnapshot] = useState<{
    projectId: string;
    revision: number | null;
    skills: readonly ProjectSkillAssignment[];
  }>({ projectId, revision: configurationRevision, skills: assignments });

  const clearMarketplaceSelection = useCallback(() => {
    marketplaceRequest.current += 1;
    marketplaceBusyRef.current = null;
    marketplaceImportKey.current = null;
    setMarketplaceSelection(null);
    setMarketplacePreview(null);
    setMarketplaceBusy(null);
    setMarketplaceError(null);
    setMarketplaceNotice(null);
    setMarketplaceImportComplete(false);
  }, []);
  const clearMarketplaceBrowse = useCallback(() => {
    browseRequest.current += 1;
    setBrowsePage(null);
    setBrowseResultQuery(null);
    setBrowsing(false);
    clearMarketplaceSelection();
  }, [clearMarketplaceSelection]);

  const refreshSources = useCallback(async () => {
    if (!actions) return;

    clearMarketplaceBrowse();
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
  }, [actions, clearMarketplaceBrowse, projectId]);

  useLayoutEffect(() => {
    if (activeProjectId.current === projectId) {
      if (activeActions.current === actions) return;
      activeActions.current = actions;
      sourceRequest.current += 1;
      inventoryRequest.current += 1;
      setSources(null);
      setSourcesProjectId(null);
      setSourceError(null);
      setSourceNotice(null);
      setSkillInventory(null);
      clearMarketplaceBrowse();
      return;
    }

    activeProjectId.current = projectId;
    activeActions.current = actions;
    sourceRequest.current += 1;
    inventoryRequest.current += 1;
    skillRequest.current += 1;
    setSources(null);
    setSourcesProjectId(null);
    setSourceError(null);
    setSourceNotice(null);
    setSkillInventory(null);
    setSourceDrafts({});
    setNewSource(sourceDraft());
    setSelectedSourceId('');
    clearMarketplaceBrowse();
    setSkillError(null);
    setSkillNotice(null);
    setMarketplaceError(null);
    setMarketplaceNotice(null);
    setImportReceiptKind(null);
    setSelectedFiles([]);
    setCandidate(null);
    setPreview(null);
    setImportReceipt(null);
    setPendingImportKey(null);
    setSelectedAgentIds([]);
  }, [actions, assignments, clearMarketplaceBrowse, configurationRevision, projectId]);

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
      browsePage.sourceRevision === selectedSource.revision &&
      browseResultQuery === browseQuery.trim()
    ? browsePage
    : null;
  const currentMarketplaceSelection = marketplaceSelection &&
      currentBrowsePage &&
      marketplaceSelection.projectId === projectId &&
      marketplaceSelection.sourceId === currentBrowsePage.sourceId &&
      marketplaceSelection.sourceRevision === currentBrowsePage.sourceRevision &&
      marketplaceSelection.resolvedCommitSha === currentBrowsePage.resolvedCommitSha &&
      marketplaceSelection.query === browseQuery.trim() &&
      marketplaceSelection.page === currentBrowsePage.page &&
      currentBrowsePage.candidates.some((item) =>
        item.location === marketplaceSelection.selectedPath &&
        item.name === marketplaceSelection.candidateName)
    ? marketplaceSelection
    : null;
  const currentMarketplacePreview = currentMarketplaceSelection ? marketplacePreview : null;
  const currentAssignments = assignmentSnapshot.projectId === projectId
    ? assignmentSnapshot.skills
    : assignments;
  const currentConfigurationRevision = assignmentSnapshot.projectId === projectId
    ? assignmentSnapshot.revision
    : configurationRevision;
  const currentSkillInventory = skillInventory?.projectId === projectId &&
      skillInventory.actions === actions
    ? skillInventory
    : null;
  const skillInventoryPage = currentSkillInventory?.page ?? null;

  const loadSkillInventory = async (
    cursor: string | null,
    cursors: Array<string | null>,
  ) => {
    if (!actions) return;
    const scope = projectId;
    const requestId = ++inventoryRequest.current;
    setSkillInventory((current) => current?.projectId === scope && current.actions === actions
      ? { ...current, loading: true, error: null }
      : { projectId: scope, actions, page: null, cursors: [null], loading: true, error: null });
    try {
      const result = await actions.listSkillInventory(scope, {
        cursor: cursor ?? undefined,
        pageSize: 25,
      });
      if (activeProjectId.current !== scope || inventoryRequest.current !== requestId) return;
      validateSkillInventoryPage(result);
      setSkillInventory({
        projectId: scope,
        actions,
        page: result,
        cursors,
        loading: false,
        error: null,
      });
    } catch (reason) {
      if (activeProjectId.current !== scope || inventoryRequest.current !== requestId) return;
      setSkillInventory((current) => ({
        projectId: scope,
        actions,
        page: current?.projectId === scope && current.actions === actions ? current.page : null,
        cursors: current?.projectId === scope && current.actions === actions ? current.cursors : [null],
        loading: false,
        error: messageForError(reason),
      }));
    }
  };

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
    if (!actions || !projectId || !newSource.repository.trim() ||
        marketplaceBusyRef.current === 'import') return;
    const scope = projectId;
    clearMarketplaceBrowse();
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
    if (!actions || source.state !== 'active' || marketplaceBusyRef.current === 'import') return;
    const scope = projectId;
    const draft = sourceDrafts[source.sourceId] ?? sourceDraft(source);
    clearMarketplaceBrowse();
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
    if (!actions || source.state !== 'active' || marketplaceBusyRef.current === 'import') return;
    const scope = projectId;
    clearMarketplaceBrowse();
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
    if (!actions || !selectedSource || selectedSource.state !== 'active' ||
        marketplaceBusyRef.current === 'import') return;
    const scope = projectId;
    const source = selectedSource;
    clearMarketplaceBrowse();
    const requestId = ++browseRequest.current;
    const query = browseQuery.trim();
    setBrowsing(true);
    setSourceError(null);
    setBrowsePage(null);
    try {
      const result = await actions.browseSource(scope, source.sourceId, {
        expectedSourceRevision: source.revision,
        query: query || undefined,
        page,
        pageSize: 25,
      });
      if (activeProjectId.current !== scope || browseRequest.current !== requestId) return;
      if (result.sourceId !== source.sourceId ||
          result.sourceRevision !== source.revision ||
          result.page !== page ||
          !Number.isSafeInteger(result.total) ||
          !Array.isArray(result.candidates)) {
        throw new Error('The server returned browse results for a different source revision or page.');
      }
      setBrowsePage(result);
      setBrowseResultQuery(query);
    } catch (reason) {
      if (activeProjectId.current === scope && browseRequest.current === requestId)
        setSourceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && browseRequest.current === requestId)
        setBrowsing(false);
    }
  };

  const previewMarketplaceSkill = async (item: MarketplaceSkillCandidate) => {
    if (!actions || !selectedSource || !currentBrowsePage ||
        skillBusy !== null || marketplaceBusyRef.current !== null) return;
    const scope = projectId;
    const source = selectedSource;
    const page = currentBrowsePage;
    if (page.candidates.every((candidate) =>
      candidate.location !== item.location || candidate.name !== item.name)) return;

    clearMarketplaceSelection();
    const requestId = marketplaceRequest.current;
    const selection: MarketplaceSkillSelection = {
      projectId: scope,
      sourceId: source.sourceId,
      sourceRevision: page.sourceRevision,
      resolvedCommitSha: page.resolvedCommitSha,
      selectedPath: item.location,
      candidateName: item.name,
      query: browseQuery.trim(),
      page: page.page,
    };
    marketplaceBusyRef.current = 'preview';
    setMarketplaceBusy('preview');
    setMarketplaceSelection(selection);
    setMarketplaceError(null);
    setMarketplaceNotice(null);
    try {
      const result = await actions.previewMarketplaceSkill(scope, source.sourceId, {
        expectedSourceRevision: page.sourceRevision,
        resolvedCommitSha: page.resolvedCommitSha,
        selectedPath: item.location,
      });
      if (activeProjectId.current !== scope || marketplaceRequest.current !== requestId) return;
      validateSkillPreview(result);
      setMarketplacePreview(result);
      setMarketplaceNotice('The Skills owner validated this source skill at the selected commit. No files were sent by the browser.');
    } catch (reason) {
      if (activeProjectId.current === scope && marketplaceRequest.current === requestId)
        setMarketplaceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && marketplaceRequest.current === requestId) {
        marketplaceBusyRef.current = null;
        setMarketplaceBusy(null);
      }
    }
  };

  const importMarketplaceSkill = async () => {
    if (!actions || !selectedSource || !currentMarketplaceSelection ||
        !currentMarketplacePreview || skillBusy !== null ||
        marketplaceBusyRef.current !== null || marketplaceImportComplete) return;
    const scope = projectId;
    const selection = currentMarketplaceSelection;
    const selectedPreview = currentMarketplacePreview;
    const requestId = marketplaceRequest.current;
    marketplaceBusyRef.current = 'import';
    setMarketplaceBusy('import');
    setMarketplaceError(null);
    setMarketplaceNotice(null);
    try {
      let idempotencyKey = marketplaceImportKey.current;
      if (!idempotencyKey) {
        idempotencyKey = makeIdempotencyKey();
        marketplaceImportKey.current = idempotencyKey;
      }
      const request: MarketplaceSkillContentImportRequest = {
        expectedSourceRevision: selection.sourceRevision,
        resolvedCommitSha: selection.resolvedCommitSha,
        selectedPath: selection.selectedPath,
        idempotencyKey,
        expectedContentDigest: selectedPreview.contentDigest,
      };
      const result = await actions.importMarketplaceSkill(scope, selection.sourceId, request);
      if (activeProjectId.current !== scope || marketplaceRequest.current !== requestId) return;
      validateSkillImportReceipt(result, selectedPreview);
      setImportReceipt(result);
      setImportReceiptKind('marketplace');
      // The next step after an import is assigning the skill, so switch to that tab.
      // It is visible once the user closes the dialog.
      setTab('assignments');
      setMarketplaceImportComplete(true);
      setMarketplaceNotice(`The Skills owner imported ${result.name} at revision ${result.revision}.`);
    } catch (reason) {
      if (activeProjectId.current === scope && marketplaceRequest.current === requestId)
        setMarketplaceError(messageForError(reason));
    } finally {
      if (activeProjectId.current === scope && marketplaceRequest.current === requestId) {
        marketplaceBusyRef.current = null;
        setMarketplaceBusy(null);
      }
    }
  };

  const previewLocalSkill = async () => {
    if (!actions || selectedFiles.length === 0 || marketplaceBusyRef.current !== null) return;
    const scope = projectId;
    const requestId = ++skillRequest.current;
    setSkillBusy('preview');
    setSkillError(null);
    setSkillNotice(null);
    setCandidate(null);
    setPreview(null);
    setImportReceipt(null);
    setImportReceiptKind(null);
    setPendingImportKey(null);
    try {
      const nextCandidate = await buildCandidate(selectedFiles);
      if (activeProjectId.current !== scope || skillRequest.current !== requestId) return;
      const result = await actions.previewSkillContent(nextCandidate);
      if (activeProjectId.current !== scope || skillRequest.current !== requestId) return;
      validateSkillPreview(result);
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
    if (!actions || !candidate || !preview || importReceipt || marketplaceBusyRef.current !== null) return;
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
      validateSkillImportReceipt(result, preview);
      setImportReceipt(result);
      setImportReceiptKind('local');
      // Same as the marketplace import: the next step is assignment.
      setTab('assignments');
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
        selectedAgentIds.length === 0 || marketplaceBusyRef.current !== null) return;
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
        throw new Error('The server returned an assignment for a different project or configuration revision.');
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
        throw new Error('The server returned a revision without the requested skill assignment.');
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

  // Counts for the stat strip come from the props already loaded, so no extra request is made.
  const enabledAssignmentCount = currentAssignments.filter((assignment) => assignment.enabled).length;
  // Local skill errors and notices. They render inside the import dialog while it is open
  // and on the page otherwise, so each message appears in exactly one place.
  const skillMessages = (
    <>
      {skillError && <MessageBar intent="error" role="alert"><MessageBarBody>{skillError}</MessageBarBody></MessageBar>}
      {skillNotice && <MessageBar intent="success"><MessageBarBody>{skillNotice}</MessageBarBody></MessageBar>}
    </>
  );

  // Layout: stat strip, a toolbar that opens the import and marketplace dialogs,
  // then Catalog and Assignments tabs. Buttons stay disabled when no actions are given.
  return (
    <div className="v1-stack">
      {!actions && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Skills and marketplace actions are unavailable because no authorized Gateway actions were supplied. No source, import, assignment, or runtime state is assumed.
          </MessageBarBody>
        </MessageBar>
      )}

      <MetricRow items={[
        { label: 'Assigned', value: currentAssignments.length },
        { label: 'Enabled', value: enabledAssignmentCount },
        { label: 'Marketplace sources', value: currentSources ? currentSources.length : '—' },
        { label: 'Agents', value: agents.length },
      ]} />
      <div className="v1-actions">
        <Button icon={<ArrowUpload24Regular />} disabled={!actions} onClick={() => setImportOpen(true)}>Import skill</Button>
        <Button icon={<PuzzlePiece20Regular />} disabled={!actions} onClick={() => setMarketplaceOpen(true)}>Browse marketplaces</Button>
      </div>
      {!importOpen && skillMessages}
      <TabList
        selectedValue={tab}
        onTabSelect={(_, data) => setTab(data.value === 'assignments' ? 'assignments' : 'catalog')}
        aria-label="Skills view"
      >
        <Tab value="catalog">Catalog</Tab>
        <Tab value="assignments">Assignments</Tab>
      </TabList>
      {tab === 'catalog' && (
        <section className="v1-stack" aria-labelledby="skill-inventory-heading">
          <h3 id="skill-inventory-heading">Imported skill inventory</h3>
          <p className="v1-muted">
            This owner-provided metadata shows stored revisions and project assignments. It does not show whether a runtime loaded a skill. Reading the inventory requires current project Owner or tenant administrator authority.
          </p>
          {currentSkillInventory?.error && (
            <MessageBar intent="error" role="alert">
              <MessageBarBody>{currentSkillInventory.error}</MessageBarBody>
            </MessageBar>
          )}
          <div className="v1-actions">
            <Button
              appearance="secondary"
              type="button"
              disabled={!actions || currentSkillInventory?.loading}
              onClick={() => void loadSkillInventory(null, [null])}
            >
              {currentSkillInventory?.loading ? 'Loading inventory…' :
                skillInventoryPage ? 'Refresh skill inventory' : 'Load skill inventory'}
            </Button>
            {skillInventoryPage?.nextCursor && (
              <Button
                appearance="secondary"
                type="button"
                disabled={currentSkillInventory?.loading}
                onClick={() => void loadSkillInventory(
                  skillInventoryPage.nextCursor,
                  [...(currentSkillInventory?.cursors ?? [null]), skillInventoryPage.nextCursor],
                )}
              >
                Next inventory page
              </Button>
            )}
            {(currentSkillInventory?.cursors.length ?? 0) > 1 && (
              <Button
                appearance="secondary"
                type="button"
                disabled={currentSkillInventory?.loading}
                onClick={() => {
                  const cursors = currentSkillInventory?.cursors.slice(0, -1) ?? [null];
                  void loadSkillInventory(cursors.at(-1) ?? null, cursors);
                }}
              >
                Previous inventory page
              </Button>
            )}
          </div>
          {currentSkillInventory?.loading && (
            <Spinner label="Loading project skill inventory…" />
          )}
          {skillInventoryPage && (
            <div className="v1-stack" aria-label="Project skill inventory">
              <p className="v1-muted">
                Page {currentSkillInventory?.cursors.length ?? 1}
              </p>
              <div className="v1-stack">
                <h4>Current project assignments</h4>
                {skillInventoryPage.assignments.length === 0 ? (
                  <p className="v1-muted">No saved skill assignments were returned.</p>
                ) : skillInventoryPage.assignments.map((assignment) => (
                  <article className="v1-record" key={assignment.skillId}>
                    <h5>{assignment.skillId}</h5>
                    <p className="v1-muted">
                      {assignment.enabled ? 'Enabled' : 'Disabled'} · order {assignment.order} · revision {assignment.revision ?? 'not set'}
                    </p>
                    <p className="v1-muted">
                      Agents: {assignment.agentIds?.length ? assignment.agentIds.join(', ') : 'none recorded'}
                    </p>
                    {assignment.contentDigest && <p className="v1-muted">Digest: <code>{assignment.contentDigest}</code></p>}
                  </article>
                ))}
              </div>
              <div className="v1-stack">
                <h4>Stored revisions</h4>
                {skillInventoryPage.revisions.length === 0 ? (
                  <p className="v1-muted">No stored skill revisions were returned for this page.</p>
                ) : skillInventoryPage.revisions.map((revision) => {
                  const assignment = skillInventoryPage.assignments.find((item) =>
                    item.skillId === revision.skillId);
                  const assignedExactRevision = assignment?.enabled === true &&
                    assignment.revision === revision.revision &&
                    assignment.contentDigest === revision.contentDigest;
                  const assignmentSummary = !assignment
                    ? 'No project assignment'
                    : assignedExactRevision
                      ? `Enabled for agents: ${assignment.agentIds?.join(', ') || 'none recorded'}`
                      : `Project assignment references revision ${assignment.revision ?? 'not set'}`;
                  return (
                    <article className="v1-record" key={`${revision.skillId}/${revision.revision}`}>
                      <h5>{revision.name}</h5>
                      <p className="v1-muted">
                        <code>{revision.skillId}</code> · revision {revision.revision} · {revision.isRevoked ? 'revoked' : 'not revoked'}
                      </p>
                      {revision.description && <p>{revision.description}</p>}
                      <p className="v1-muted">{assignmentSummary}</p>
                      <p className="v1-muted">
                        Digest <code>{revision.contentDigest}</code> · {revision.resourceCount} resource(s) · {revision.totalBytes} byte(s)
                      </p>
                      <p className="v1-muted">
                        {revision.sourceId
                          ? `Marketplace source ${revision.sourceId} · source revision ${revision.sourceRevision ?? 'unknown'} · commit ${revision.resolvedCommitSha ?? 'unknown'} · ${revision.selectedPath ?? 'repository root'}`
                          : 'Local upload'}
                      </p>
                      <p className="v1-muted">
                        Created <time dateTime={revision.createdAt}>{revision.createdAt}</time>
                      </p>
                    </article>
                  );
                })}
              </div>
            </div>
          )}
        </section>
      )}
      {tab === 'assignments' && (
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
                    marketplaceBusy !== null ||
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
      )}

      <Dialog open={marketplaceOpen} onOpenChange={(_, data) => setMarketplaceOpen(data.open)}>
        <DialogSurface className="v1-dialog-surface--wide">
          <DialogBody>
            <DialogTitle>Browse marketplaces</DialogTitle>
            <DialogContent>
              <section className="v1-stack" aria-labelledby="marketplace-sources-heading">
                <h3 id="marketplace-sources-heading">Marketplace sources</h3>
                <p className="v1-muted">
                  The service checks your project access for each action. An active source is saved; it does not confirm that GitHub is available.
                </p>
                {sourceError && <MessageBar intent="error" role="alert"><MessageBarBody>{sourceError}</MessageBarBody></MessageBar>}
                {sourceNotice && <MessageBar intent="success"><MessageBarBody>{sourceNotice}</MessageBarBody></MessageBar>}
                {marketplaceError && <MessageBar intent="error" role="alert"><MessageBarBody>{marketplaceError}</MessageBarBody></MessageBar>}
                {marketplaceNotice && <MessageBar intent="success"><MessageBarBody>{marketplaceNotice}</MessageBarBody></MessageBar>}
                <p className="v1-muted">
                  Use public GitHub repositories. Preview and import use the exact saved source version shown in the results.
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
                              <Button appearance="secondary" type="submit" disabled={!actions || busy ||
                                marketplaceBusy === 'import' || source.state !== 'active' || !draft.repository.trim()}>
                                {sourceBusy === `update:${source.sourceId}` ? 'Saving…' : 'Save source'}
                              </Button>
                              <Button
                                appearance="secondary"
                                type="button"
                                disabled={!actions || busy || marketplaceBusy === 'import' || source.state !== 'active'}
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
                    <Button appearance="primary" type="submit" disabled={!actions || sourceBusy !== null ||
                      marketplaceBusy === 'import' || !newSource.repository.trim()}>
                      {sourceBusy === 'create' ? 'Adding…' : 'Add source'}
                    </Button>
                    <Button appearance="secondary" type="button" disabled={!actions || sourceBusy !== null ||
                      marketplaceBusy === 'import'} onClick={() => {
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
                        clearMarketplaceBrowse();
                      }}
                      disabled={!currentSources?.length || marketplaceBusy === 'import'}
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
                        disabled={marketplaceBusy === 'import'}
                        onChange={(_, data) => {
                          setBrowseQuery(data.value);
                          clearMarketplaceBrowse();
                        }}
                      />
                    </Field>
                    <div className="v1-actions">
                      <Button appearance="primary" type="submit" disabled={!actions || !selectedSource ||
                        browsing || marketplaceBusy === 'import'}>
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
                            <Button
                              appearance="secondary"
                              aria-label={`Preview ${item.name}`}
                              disabled={!actions || marketplaceBusy !== null || skillBusy !== null}
                              onClick={() => void previewMarketplaceSkill(item)}
                            >
                              {marketplaceBusy === 'preview' &&
                                currentMarketplaceSelection?.selectedPath === item.location
                                ? 'Previewing…'
                                : 'Preview source skill'}
                            </Button>
                          </article>
                        ))}
                      </div>
                    )}
                    <div className="v1-actions">
                      <Button
                        appearance="secondary"
                        disabled={!actions || browsing || marketplaceBusy === 'import' ||
                          currentBrowsePage.page <= 1}
                        onClick={() => void browse(currentBrowsePage.page - 1)}
                      >
                        Previous page
                      </Button>
                      <span className="v1-muted">Page {currentBrowsePage.page} · {currentBrowsePage.pageSize} per page</span>
                      <Button
                        appearance="secondary"
                        disabled={!actions || browsing || marketplaceBusy === 'import' ||
                          !currentBrowsePage.hasMore}
                        onClick={() => void browse(currentBrowsePage.page + 1)}
                      >
                        Next page
                      </Button>
                    </div>
                  </div>
                )}
                {marketplaceBusy === 'preview' && (
                  <Spinner label="Previewing the selected skill at its pinned commit…" />
                )}
                {currentMarketplaceSelection && currentMarketplacePreview && (
                  <div className="v1-stack" aria-label="Marketplace skill preview">
                    <h4>Preview {currentMarketplaceSelection.candidateName}</h4>
                    <p className="v1-muted">
                      {currentMarketplaceSelection.selectedPath || 'Repository root'} · source revision {currentMarketplaceSelection.sourceRevision} · commit <code>{currentMarketplaceSelection.resolvedCommitSha}</code>
                    </p>
                    <div className="v1-detail-list">
                      <div><dt>Validated skill</dt><dd>{currentMarketplacePreview.name}</dd></div>
                      <div><dt>Resources</dt><dd>{currentMarketplacePreview.resourceCount}</dd></div>
                      <div><dt>Content digest</dt><dd><code>{currentMarketplacePreview.contentDigest}</code></dd></div>
                      <div><dt>Total bytes</dt><dd>{currentMarketplacePreview.totalBytes}</dd></div>
                    </div>
                    <Button
                      appearance="primary"
                      type="button"
                      disabled={!actions || skillBusy !== null || marketplaceBusy !== null ||
                        marketplaceImportComplete}
                      onClick={() => void importMarketplaceSkill()}
                    >
                      {marketplaceBusy === 'import' ? 'Importing…' :
                        marketplaceImportComplete ? 'Source skill imported' : 'Import selected source skill'}
                    </Button>
                  </div>
                )}
                {marketplaceBusy === 'import' && (
                  <Spinner label="Importing the selected source skill…" />
                )}
                {importReceiptKind === 'marketplace' && importReceipt && (
                  <p>
                    Imported <strong>{importReceipt.name}</strong> · revision {importReceipt.revision} · digest <code>{importReceipt.contentDigest}</code>. Close this dialog to assign it on the Assignments tab.
                  </p>
                )}
              </section>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setMarketplaceOpen(false)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      <Dialog open={importOpen} onOpenChange={(_, data) => setImportOpen(data.open)}>
        <DialogSurface className="v1-dialog-surface--wide">
          <DialogBody>
            <DialogTitle>Import skill</DialogTitle>
            <DialogContent>
              <div className="v1-stack">
                {skillMessages}
                <section className="v1-stack" aria-label="Upload a local skill">
                  <p className="v1-muted">
                    Select a folder that contains one SKILL.md and its text resources, or select those files together. The Skills owner checks file content and paths before import.
                  </p>
                  <Field label="Skill folder or files" hint="Include exactly one file named SKILL.md.">
                    <input
                      ref={folderInputRef}
                      className="v1-select"
                      type="file"
                      multiple
                      aria-label="Skill folder or files"
                      disabled={!actions || skillBusy !== null || marketplaceBusy !== null}
                      onChange={(event) => {
                        if (skillBusy !== null || marketplaceBusy !== null) return;
                        setSelectedFiles(Array.from(event.currentTarget.files ?? []));
                        setCandidate(null);
                        setPreview(null);
                        setImportReceipt(null);
                        setImportReceiptKind(null);
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
                      disabled={!actions || selectedFiles.length === 0 || skillBusy !== null ||
                        marketplaceBusy !== null}
                      onClick={() => void previewLocalSkill()}
                    >
                      {skillBusy === 'preview' ? 'Checking files…' : 'Validate skill files'}
                    </Button>
                    <Button
                      appearance="secondary"
                      type="button"
                      disabled={!actions || !preview || !candidate || skillBusy !== null ||
                        marketplaceBusy !== null || importReceipt !== null}
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
                  {importReceipt && importReceiptKind === 'local' && (
                    <p>
                      Imported <strong>{importReceipt.name}</strong> · revision {importReceipt.revision} · digest <code>{importReceipt.contentDigest}</code>. Close this dialog to assign it on the Assignments tab.
                    </p>
                  )}
                </section>
              </div>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => setImportOpen(false)}>Close</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
