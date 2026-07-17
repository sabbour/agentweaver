import { apiClient } from '../api/apiClient';
import {
  ApiError,
  isApplyBlueprintSkillDefaultsResponse,
  isBlueprintSkillDefaultsPreviewResponse,
} from '../api/client';
import {
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  Tab,
  TabList,
  Text,
  Textarea,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import {
  ArrowSync24Regular,
  ArrowUpload24Regular,
  BranchFork24Regular,
  Delete24Regular,
  Dismiss24Regular,
  Eye24Regular,
  PuzzlePiece20Regular,
} from '@fluentui/react-icons';
import {
  EmptyState,
  ErrorState,
  LoadingState,
  MetricRow,
  PageContainer,
  PageHeader,
} from '../components/ui';
import { collectFilesFromDataTransfer, supportsEntryApi } from '../utils/skillDrop';
import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import type {
  BlueprintSkillDefaultsPreviewResponse,
  Project,
  SkillAcquisitionResponse,
  SkillCandidateDto,
  SkillDetailDto,
  SkillDto,
  TeamMemberDto,
} from '../api/types';
import type { DroppedSkillFile } from '../utils/skillDrop';

const useStyles = makeStyles({
  breadcrumbLink: {
    color: tokens.colorNeutralForeground2,
    textDecoration: 'none',
    ':hover': { textDecorationLine: 'underline' },
  },
  breadcrumbSep: {
    color: tokens.colorNeutralForeground4,
  },
  tabContent: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  toolbar: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  syncHint: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
    lineHeight: '1.5',
    maxWidth: '640px',
  },
  empty: { color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  itemList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  item: {
    display: 'flex', flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalS,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  itemHeader: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  itemTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase300, flexGrow: 1 },
  itemMeta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  itemDesc: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground1, lineHeight: '1.5' },
  agentChips: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXS },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXS },
  formGrid: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  dropzone: {
    border: `1px dashed ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalL} ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorNeutralBackground2,
    cursor: 'pointer',
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  candidateList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, marginTop: tokens.spacingVerticalM },
  candidate: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`, display: 'flex',
    flexDirection: 'column', gap: tokens.spacingVerticalXXS,
  },
  drawerContent: { fontSize: tokens.fontSizeBase200, whiteSpace: 'pre-wrap', lineHeight: '1.6', fontFamily: tokens.fontFamilyMonospace },
  assignGrid: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, marginTop: tokens.spacingVerticalXS },
  assignRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  defaultsSections: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  defaultsRows: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXS },
  defaultsRow: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  defaultsMeta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  hiddenInput: { display: 'none' },
});

function formatApiError(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body || 'Request failed'}`;
  return err instanceof Error ? err.message : String(err);
}

const FALLBACK_DEFAULTS_BLUEPRINT_ID = 'blueprint-software-development';

function structuredBlockedPreview(err: unknown): BlueprintSkillDefaultsPreviewResponse | null {
  if (err instanceof ApiError
    && err.status === 422
    && isBlueprintSkillDefaultsPreviewResponse(err.payload)
    && !err.payload.can_apply) {
    return err.payload;
  }
  return null;
}

function statusColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'active') return 'success';
  if (status === 'missing') return 'warning';
  if (status === 'malformed') return 'danger';
  return 'subtle';
}

function summarizeAcquisition(res: SkillAcquisitionResponse): string {
  const counts = { Added: 0, Updated: 0, Unchanged: 0, Rejected: 0 } as Record<string, number>;
  const normalize = (kind: string) => kind.charAt(0).toUpperCase() + kind.slice(1).toLowerCase();
  for (const r of res.results) counts[normalize(r.kind)] = (counts[normalize(r.kind)] ?? 0) + 1;
  const parts: string[] = [];
  if (counts.Added) parts.push(`${counts.Added} added`);
  if (counts.Updated) parts.push(`${counts.Updated} updated`);
  if (counts.Unchanged) parts.push(`${counts.Unchanged} unchanged`);
  if (counts.Rejected) parts.push(`${counts.Rejected} rejected`);
  if (res.marked_missing.length) parts.push(`${res.marked_missing.length} marked missing`);
  return parts.length ? parts.join(', ') : 'No changes.';
}

function defaultApplyError(err: unknown): {
  message: string;
  requiresRepreview: boolean;
  preview: BlueprintSkillDefaultsPreviewResponse | null;
} {
  if (err instanceof ApiError) {
    if (err.status === 409) {
      const staleResponse = isApplyBlueprintSkillDefaultsResponse(err.payload)
        ? err.payload
        : null;
      return {
        message: staleResponse?.errors.length
          ? `This preview is stale because the project changed. ${staleResponse.errors.join(' ')}`
          : 'This preview is stale because the project changed. Preview the latest defaults before applying.',
        requiresRepreview: true,
        preview: staleResponse?.preview ?? null,
      };
    }
    if (err.status === 422 && isApplyBlueprintSkillDefaultsResponse(err.payload)) {
      const errors = err.payload.errors;
      return {
        message: errors.length
          ? `Defaults could not be applied: ${errors.join(' ')}`
          : 'Defaults could not be applied. Resolve the validation errors before trying again.',
        requiresRepreview: false,
        preview: err.payload.preview,
      };
    }
    if (err.status === 422) {
      return {
        message: 'Defaults could not be applied. Resolve the validation errors before trying again.',
        requiresRepreview: false,
        preview: null,
      };
    }
  }
  return { message: formatApiError(err), requiresRepreview: false, preview: null };
}

function defaultsBlueprintId(project: Project): string | null {
  if (project.source_blueprint_type === 'inline'
    || project.source_blueprint_type === 'custom'
    || project.source_blueprint_id === 'inline') {
    return null;
  }
  return project.source_blueprint_id ?? FALLBACK_DEFAULTS_BLUEPRINT_ID;
}

function defaultsUnsupportedReason(project: Project | null): string | null {
  if (project?.source_blueprint_type === 'inline' || project?.source_blueprint_id === 'inline') {
    return 'Blueprint skill defaults are unavailable because this project uses an inline blueprint.';
  }
  if (project?.source_blueprint_type === 'custom') {
    return 'Blueprint skill defaults are unavailable because this project uses a custom blueprint.';
  }
  return null;
}

export function SkillsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();

  const [selectedTab, setSelectedTab] = useState<'catalog' | 'assignments'>('catalog');
  const [skills, setSkills] = useState<SkillDto[] | null>(null);
  const [members, setMembers] = useState<TeamMemberDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  const [detail, setDetail] = useState<SkillDetailDto | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  const [importOpen, setImportOpen] = useState(false);
  const [sourceUrl, setSourceUrl] = useState('');
  const [candidates, setCandidates] = useState<SkillCandidateDto[] | null>(null);
  const [selectedLocations, setSelectedLocations] = useState<Set<string>>(new Set());

  const [addOpen, setAddOpen] = useState(false);
  const [generateOpen, setGenerateOpen] = useState(false);
  const [skillName, setSkillName] = useState('');
  const [skillDisplayName, setSkillDisplayName] = useState('');
  const [skillDescription, setSkillDescription] = useState('');
  const [skillInstructions, setSkillInstructions] = useState('');
  const [generatePrompt, setGeneratePrompt] = useState('');
  const [defaultsOpen, setDefaultsOpen] = useState(false);
  const [defaultsDialogKey, setDefaultsDialogKey] = useState(0);
  const [defaultsPreview, setDefaultsPreview] = useState<BlueprintSkillDefaultsPreviewResponse | null>(null);
  const [defaultsError, setDefaultsError] = useState<string | null>(null);
  const [defaultsRequiresRepreview, setDefaultsRequiresRepreview] = useState(false);
  const [defaultsProject, setDefaultsProject] = useState<Project | null>(null);
  const [defaultsProjectResolved, setDefaultsProjectResolved] = useState(false);
  const defaultsDialogGeneration = useRef(0);
  const currentProjectId = useRef(projectId);
  const defaultsPreviewTransports = useRef(new Map<string, number>());
  const defaultsApplyTransports = useRef(new Map<string, number>());
  const defaultsApplyPreviews = useRef(new Map<string, BlueprintSkillDefaultsPreviewResponse>());
  const defaultsTransportSequence = useRef(0);
  const defaultsBusyProject = useRef<string | null>(null);
  const defaultsTriggerRef = useRef<HTMLButtonElement>(null);

  currentProjectId.current = projectId;

  const mdFileInputRef = useRef<HTMLInputElement>(null);
  const folderInputRef = useRef<HTMLInputElement>(null);

  const reload = useCallback(() => {
    setSkills(null);
    setReloadKey((k) => k + 1);
  }, []);

  useEffect(() => {
    if (!projectId) return;
    setLoading(true);
    setLoadError(null);
    Promise.all([
      apiClient.listSkills(projectId),
      apiClient.getTeam(projectId).then((t) => t.members).catch(() => [] as TeamMemberDto[]),
    ])
      .then(([s, m]) => { setSkills(s); setMembers(m); })
      .catch((err: unknown) => { setSkills([]); setLoadError(formatApiError(err)); })
      .finally(() => setLoading(false));
  }, [projectId, reloadKey]);

  const runAcquisition = async (label: string, action: () => Promise<SkillAcquisitionResponse>) => {
    if (!projectId || busy) return;
    setBusy(label);
    setMutationError(null);
    setNotice(null);
    try {
      const res = await action();
      setNotice(`${label}: ${summarizeAcquisition(res)}`);
      reload();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onSync = () => void runAcquisition('Sync', () => apiClient.syncSkills(projectId!));

  const closeDefaults = useCallback((restoreFocus = true) => {
    defaultsDialogGeneration.current += 1;
    setDefaultsDialogKey((key) => key + 1);
    const activeProjectId = currentProjectId.current;
    if (activeProjectId) {
      const cancellingPreview = defaultsPreviewTransports.current.has(activeProjectId);
      defaultsPreviewTransports.current.delete(activeProjectId);
      if (cancellingPreview && defaultsBusyProject.current === activeProjectId) {
        defaultsBusyProject.current = null;
        setBusy((current) => current === 'defaults-preview' ? null : current);
      }
    }
    setDefaultsOpen(false);
    setDefaultsPreview(null);
    setDefaultsError(null);
    setDefaultsRequiresRepreview(false);
    if (restoreFocus) queueMicrotask(() => defaultsTriggerRef.current?.focus());
  }, []);

  useEffect(() => {
    closeDefaults(false);
    if (defaultsBusyProject.current !== null && defaultsBusyProject.current !== projectId) {
      defaultsBusyProject.current = null;
      setBusy((current) => current === 'defaults-preview' || current === 'defaults-apply' ? null : current);
    }
  }, [closeDefaults, projectId]);

  useEffect(() => {
    if (!projectId) return;
    let current = true;
    setDefaultsProject(null);
    setDefaultsProjectResolved(false);
    void apiClient.getProject(projectId)
      .then((project) => {
        if (current) setDefaultsProject(project);
      })
      .catch(() => {
        // A preview refreshes metadata before it calls the defaults endpoint.
      })
      .finally(() => {
        if (current) setDefaultsProjectResolved(true);
      });
    return () => { current = false; };
  }, [projectId]);

  const previewDefaults = useCallback(async () => {
    if (!projectId
      || busy
      || defaultsPreviewTransports.current.has(projectId)
      || defaultsApplyTransports.current.has(projectId)) return;
    const dialogGeneration = defaultsDialogGeneration.current;
    const transportId = defaultsTransportSequence.current + 1;
    defaultsTransportSequence.current = transportId;
    defaultsPreviewTransports.current.set(projectId, transportId);
    defaultsBusyProject.current = projectId;
    setBusy('defaults-preview');
    setDefaultsPreview(null);
    setDefaultsError(null);
    setDefaultsRequiresRepreview(false);
    try {
      const project = await apiClient.getProject(projectId);
      if (currentProjectId.current !== projectId
        || defaultsPreviewTransports.current.get(projectId) !== transportId
        || defaultsDialogGeneration.current !== dialogGeneration) return;
      setDefaultsProject(project);
      const blueprintId = defaultsBlueprintId(project);
      if (blueprintId === null) {
        setDefaultsError(defaultsUnsupportedReason(project));
        return;
      }
      const preview = await apiClient.previewBlueprintSkillDefaults(projectId, blueprintId);
      if (currentProjectId.current === projectId
        && defaultsPreviewTransports.current.get(projectId) === transportId
        && defaultsDialogGeneration.current === dialogGeneration) setDefaultsPreview(preview);
    } catch (err) {
      if (currentProjectId.current === projectId
        && defaultsPreviewTransports.current.get(projectId) === transportId
        && defaultsDialogGeneration.current === dialogGeneration) {
        const blockedPreview = structuredBlockedPreview(err);
        if (blockedPreview) {
          setDefaultsPreview(blockedPreview);
          setDefaultsError(null);
        } else {
          setDefaultsError(formatApiError(err));
        }
      }
    } finally {
      if (defaultsPreviewTransports.current.get(projectId) === transportId) {
        defaultsPreviewTransports.current.delete(projectId);
      }
      if (currentProjectId.current === projectId
        && defaultsBusyProject.current === projectId
        && defaultsDialogGeneration.current === dialogGeneration) {
        defaultsBusyProject.current = null;
        setBusy(null);
      }
    }
  }, [busy, projectId]);

  const openDefaults = () => {
    if (!projectId || defaultsUnsupportedReason(defaultsProject)) return;
    if (defaultsApplyTransports.current.has(projectId)) {
      queueMicrotask(() => {
        if (defaultsApplyTransports.current.has(projectId) && currentProjectId.current === projectId) {
          setDefaultsPreview(defaultsApplyPreviews.current.get(projectId) ?? null);
          setDefaultsOpen(true);
        }
      });
      return;
    }
    if (busy || defaultsPreviewTransports.current.has(projectId)) return;
    setDefaultsOpen(true);
    void previewDefaults();
  };

  const applyDefaults = async () => {
    if (!projectId
      || !defaultsPreview
      || busy
      || defaultsRequiresRepreview
      || defaultsApplyTransports.current.has(projectId)) return;
    const requestProjectId = projectId;
    const transportId = defaultsTransportSequence.current + 1;
    defaultsTransportSequence.current = transportId;
    defaultsApplyTransports.current.set(requestProjectId, transportId);
    defaultsApplyPreviews.current.set(requestProjectId, defaultsPreview);
    defaultsBusyProject.current = requestProjectId;
    setBusy('defaults-apply');
    setDefaultsError(null);
    try {
      const result = await apiClient.applyBlueprintSkillDefaults(
        requestProjectId,
        defaultsPreview.blueprint_id,
        defaultsPreview.digest,
      );
      if (defaultsApplyTransports.current.get(requestProjectId) !== transportId) return;
      if (result.outcome !== 'applied') {
        if (currentProjectId.current === requestProjectId) {
          const stale = result.outcome === 'stale';
          setDefaultsError(result.errors.join(' ') || (stale
            ? 'This preview is stale because the project changed. Preview the latest defaults before applying.'
            : 'Defaults could not be applied. Resolve the validation errors before trying again.'));
          if (result.preview !== null) setDefaultsPreview(result.preview);
          setDefaultsRequiresRepreview(stale);
        }
        return;
      }
      if (currentProjectId.current === requestProjectId) {
        setNotice('Blueprint defaults applied.');
        reload();
        closeDefaults();
      }
    } catch (err) {
      if (defaultsApplyTransports.current.get(requestProjectId) === transportId
        && currentProjectId.current === requestProjectId) {
        const error = defaultApplyError(err);
        setDefaultsError(error.message);
        if (error.preview !== null) setDefaultsPreview(error.preview);
        setDefaultsRequiresRepreview(error.requiresRepreview);
      }
    } finally {
      if (defaultsApplyTransports.current.get(requestProjectId) === transportId) {
        defaultsApplyTransports.current.delete(requestProjectId);
        defaultsApplyPreviews.current.delete(requestProjectId);
      }
      if (currentProjectId.current === requestProjectId
        && defaultsBusyProject.current === requestProjectId) {
        defaultsBusyProject.current = null;
        setBusy(null);
      }
    }
  };

  const onPreview = async () => {
    if (!projectId || !sourceUrl.trim()) return;
    setBusy('preview');
    setMutationError(null);
    setCandidates(null);
    try {
      const res = await apiClient.previewSkillImport(projectId, sourceUrl.trim());
      setCandidates(res.candidates);
      setSelectedLocations(new Set(res.candidates.filter((c) => c.valid).map((c) => c.location)));
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onImport = async () => {
    if (!projectId || !sourceUrl.trim()) return;
    setBusy('import');
    setMutationError(null);
    try {
      const locs = candidates ? Array.from(selectedLocations) : undefined;
      const res = await apiClient.importSkills(projectId, sourceUrl.trim(), locs && locs.length ? locs : undefined);
      setNotice(`Import: ${summarizeAcquisition(res)}`);
      setImportOpen(false);
      setSourceUrl('');
      setCandidates(null);
      setSelectedLocations(new Set());
      reload();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onUploadFiles = async (files: FileList | null) => {
    if (!projectId || !files || files.length === 0) return;
    await runAcquisition('Upload', () => apiClient.uploadSkills(projectId, Array.from(files)));
    if (mdFileInputRef.current) mdFileInputRef.current.value = '';
    if (folderInputRef.current) folderInputRef.current.value = '';
  };

  const onDropUpload = async (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    if (!projectId || busy) return;
    const dt = e.dataTransfer;
    if (supportsEntryApi(dt.items)) {
      let collected: DroppedSkillFile[];
      try {
        collected = await collectFilesFromDataTransfer(dt.items);
      } catch (err) {
        setMutationError(formatApiError(err));
        return;
      }
      if (collected.length > 0) {
        await runAcquisition('Upload', () =>
          apiClient.uploadSkills(projectId, collected.map((c) => ({ file: c.file, relativePath: c.relativePath }))),
        );
        return;
      }
    }
    await onUploadFiles(dt.files);
  };

  const resetSkillForm = () => {
    setSkillName('');
    setSkillDisplayName('');
    setSkillDescription('');
    setSkillInstructions('');
  };

  const onCreateSkill = async () => {
    if (!projectId) return;
    setBusy('Create skill');
    setMutationError(null);
    setNotice(null);
    try {
      const res = await apiClient.createSkill(projectId, {
        name: skillName.trim(),
        displayName: skillDisplayName.trim() || undefined,
        description: skillDescription.trim(),
        instructions: skillInstructions.trim(),
      });
      setNotice(`Create skill: ${summarizeAcquisition(res)}`);
      setAddOpen(false);
      setGenerateOpen(false);
      resetSkillForm();
      reload();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onGenerateSkill = async () => {
    if (!projectId || !generatePrompt.trim()) return;
    setBusy('Generate skill');
    setMutationError(null);
    try {
      const draft = await apiClient.generateSkill(projectId, generatePrompt.trim());
      setSkillName(draft.name);
      setSkillDisplayName(draft.display_name ?? '');
      setSkillDescription(draft.description);
      setSkillInstructions(draft.instructions);
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onDelete = async (skill: SkillDto) => {
    if (!projectId || busy) return;
    setBusy(`delete:${skill.id}`);
    setMutationError(null);
    try {
      await apiClient.deleteSkill(projectId, skill.id);
      reload();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const openDetail = async (skill: SkillDto) => {
    if (!projectId) return;
    setDetailOpen(true);
    setDetail(null);
    try {
      setDetail(await apiClient.getSkill(projectId, skill.id));
    } catch (err) {
      setMutationError(formatApiError(err));
      setDetailOpen(false);
    }
  };

  const toggleAssignment = async (skill: SkillDto, agentName: string, assign: boolean) => {
    if (!projectId || busy) return;
    setBusy(`assign:${skill.id}:${agentName}`);
    setMutationError(null);
    try {
      if (assign) await apiClient.assignSkill(projectId, skill.id, agentName);
      else await apiClient.unassignSkill(projectId, skill.id, agentName);
      reload();
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const isBusy = busy !== null;
  const skillRows = skills ?? [];
  const activeSkillCount = skillRows.filter((s) => s.status === 'active').length;
  const assignedSkillCount = skillRows.filter((s) => s.assigned_agents.length > 0).length;
  const repositorySkillCount = skillRows.filter((s) => s.provenance === 'connected-repo-sync' || s.provenance === 'repo-import').length;
  const defaultsUnavailableReason = defaultsUnsupportedReason(defaultsProject);

  const roleByName = new Map(members.map((m) => [m.name, m.role_title]));
  const labelForAgent = (name: string): string => {
    const role = roleByName.get(name);
    return role ? `${name} — ${role}` : name;
  };

  return (
    <PageContainer>
      <PageHeader
        title="Skills"
        description="Import, sync, and assign reusable agent skills for this project."
        breadcrumbs={
          <>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
            <span className={styles.breadcrumbSep}>/</span>
            <span>Skills</span>
          </>
        }
      />

      <MetricRow items={[
        { label: 'Total', value: skillRows.length },
        { label: 'Active', value: activeSkillCount },
        { label: 'Assigned', value: assignedSkillCount },
        { label: 'From repo', value: repositorySkillCount },
      ]} />

      <div className={styles.toolbar}>
        <Button icon={<BranchFork24Regular />} disabled={isBusy} onClick={() => { resetSkillForm(); setAddOpen(true); }}>
          Add skill
        </Button>
        <Button icon={<Eye24Regular />} disabled={isBusy} onClick={() => { resetSkillForm(); setGeneratePrompt(''); setGenerateOpen(true); }}>
          Generate skill
        </Button>
        <Button icon={<ArrowUpload24Regular />} disabled={isBusy} onClick={() => setImportOpen(true)}>
          Import skill
        </Button>
        <Button icon={<ArrowSync24Regular />} disabled={isBusy} onClick={onSync}>
          {busy === 'Sync' ? 'Syncing…' : 'Sync connected repo'}
        </Button>
        <Button
          ref={defaultsTriggerRef}
          icon={<Eye24Regular />}
          disabled={(isBusy && busy !== 'defaults-apply') || defaultsUnavailableReason !== null}
          aria-describedby="blueprint-defaults-availability"
          onClick={openDefaults}
        >
          Preview blueprint defaults
        </Button>
      </div>
      <Text id="blueprint-defaults-availability" className={styles.syncHint}>
        {!defaultsProjectResolved
          ? 'Checking whether blueprint skill defaults are available for this project.'
          : defaultsUnavailableReason ?? 'Preview bundled defaults for a predefined blueprint, or use the supported fallback for projects without source metadata.'}
      </Text>
      <Text as="p" className={styles.syncHint}>
        Sync scans the project&apos;s already-connected repo working directory (no separate fetch) for <code>&lt;skill-name&gt;/SKILL.md</code>,
        one level deep, at the repo root or in <code>.github/skills</code>, <code>.copilot/skills</code>, <code>.claude/skills</code>, or <code>.agents/skills</code>.
        Any other files next to SKILL.md are picked up as bundled resources. Re-syncing is safe to repeat — unchanged skills are skipped, changed ones are updated, and skills whose folder disappears are flagged as Missing instead of deleted.
      </Text>

      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_, data) => setSelectedTab(data.value as 'catalog' | 'assignments')}
      >
        <Tab value="catalog">Catalog</Tab>
        <Tab value="assignments">Assignments</Tab>
      </TabList>

      <div className={styles.tabContent}>
        {loading && <LoadingState rows={3} />}
        {loadError && <ErrorState message={loadError} onRetry={reload} />}
        {notice && (
          <MessageBar intent="success"><MessageBarBody>{notice}</MessageBarBody></MessageBar>
        )}
        {mutationError && (
          <MessageBar intent="error"><MessageBarBody>{mutationError}</MessageBarBody></MessageBar>
        )}

        {!loading && !loadError && selectedTab === 'catalog' && (
          skills === null || skills.length === 0
            ? <EmptyState
                title="No skills in the catalog yet"
                description="Sync the connected repo, import from a Git repo, or upload a skill."
                icon={<PuzzlePiece20Regular />}
              />
            : (
              <div className={styles.itemList}>
                {skills.map((s) => (
                  <div key={s.id} className={styles.item}>
                    <div className={styles.itemHeader}>
                      <span className={styles.itemTitle}>{s.name}</span>
                      <Badge appearance="tint" color={statusColor(s.status)}>{s.status}</Badge>
                      <Badge appearance="outline">{s.provenance}</Badge>
                      <span className={styles.itemMeta}>{new Date(s.updated_at).toLocaleString()}</span>
                    </div>
                    <span className={styles.itemDesc}>{s.description}</span>
                    {s.source_location && (
                      <span className={styles.itemMeta}>{s.source_repository ? `${s.source_repository} · ` : ''}{s.source_location}</span>
                    )}
                    {s.assigned_agents.length > 0 && (
                      <div className={styles.agentChips}>
                        {s.assigned_agents.map((a) => <Badge key={a} appearance="tint" color="subtle">{labelForAgent(a)}</Badge>)}
                      </div>
                    )}
                    <div className={styles.actions}>
                      <Button size="small" icon={<Eye24Regular />} disabled={isBusy} onClick={() => void openDetail(s)}>View</Button>
                      <Button size="small" appearance="outline" icon={<Delete24Regular />} disabled={isBusy} onClick={() => void onDelete(s)}>Delete</Button>
                    </div>
                  </div>
                ))}
              </div>
            )
        )}

        {!loading && !loadError && selectedTab === 'assignments' && (
          skills === null || skills.length === 0
            ? <EmptyState title="No skills to assign" description="Add skills in the Catalog tab first." />
            : members.length === 0
              ? <EmptyState title="No agents in this project's team yet" description="Cast a team to assign skills." />
              : (
                <div className={styles.itemList}>
                  {skills.map((s) => (
                    <div key={s.id} className={styles.item}>
                      <div className={styles.itemHeader}>
                        <span className={styles.itemTitle}>{s.name}</span>
                        <Badge appearance="tint" color={statusColor(s.status)}>{s.status}</Badge>
                      </div>
                      <span className={styles.itemDesc}>{s.description}</span>
                      <div className={styles.assignGrid}>
                        <div className={styles.assignRow}>
                          {members.map((m) => {
                            const assigned = s.assigned_agents.includes(m.name);
                            return (
                              <Checkbox
                                key={m.name}
                                label={labelForAgent(m.name)}
                                checked={assigned}
                                disabled={isBusy}
                                onChange={(_, data) => void toggleAssignment(s, m.name, data.checked === true)}
                              />
                            );
                          })}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )
        )}
      </div>

      {/* Detail drawer */}
      <OverlayDrawer position="end" open={detailOpen} onOpenChange={(_, d) => setDetailOpen(d.open)} size="medium">
        <DrawerHeader>
          <DrawerHeaderTitle
            action={
              <Tooltip content="Close" relationship="label">
                <Button appearance="subtle" icon={<Dismiss24Regular />} onClick={() => setDetailOpen(false)} />
              </Tooltip>
            }
          >
            {detail?.name ?? 'Skill'}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          {detail === null
            ? <LoadingState rows={2} />
            : (
              <>
                <Text as="p">{detail.description}</Text>
                {detail.resources.length > 0 && (
                  <Text as="p" style={{ color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 }}>{detail.resources.length} bundled resource(s)</Text>
                )}
                <div className={styles.drawerContent}>{detail.instructions}</div>
              </>
            )}
        </DrawerBody>
      </OverlayDrawer>

      <Dialog
        key={defaultsDialogKey}
        open={defaultsOpen}
        modalType="modal"
        onOpenChange={(_, data) => {
          if (!data.open) closeDefaults();
        }}
      >
        <DialogSurface aria-describedby="blueprint-defaults-description">
          <DialogBody>
            <DialogTitle>Preview blueprint skill defaults</DialogTitle>
            <DialogContent className={styles.defaultsSections}>
              <Text id="blueprint-defaults-description">Review the proposed built-in skill changes before they are applied to this existing project.</Text>
              {busy === 'defaults-preview' && <LoadingState rows={3} />}
              {busy === 'defaults-apply' && <Text>Applying blueprint defaults. This request will continue if you close this dialog.</Text>}
              {defaultsError && <MessageBar intent="error"><MessageBarBody>{defaultsError}</MessageBarBody></MessageBar>}
              {defaultsPreview && (
                <>
                  <section aria-label="Blueprint identity">
                    <Text weight="semibold">{defaultsPreview.blueprint_id}</Text>
                    <Text className={styles.defaultsMeta}>Version {defaultsPreview.blueprint_version}</Text>
                    <Text className={styles.defaultsMeta}>Source: predefined blueprint · preview {defaultsPreview.digest}</Text>
                  </section>
                  {!defaultsPreview.can_apply && (
                    <MessageBar intent="error">
                      <MessageBarBody>Defaults are blocked. Resolve the listed blockers before applying.</MessageBarBody>
                    </MessageBar>
                  )}
                  {defaultsPreview.errors.length > 0 && (
                    <section aria-labelledby="defaults-errors">
                      <Text id="defaults-errors" weight="semibold">Blockers</Text>
                      <div className={styles.defaultsRows} role="list">
                        {defaultsPreview.errors.map((error, index) => (
                          <MessageBar key={`${index}:${error}`} intent="error" role="listitem">
                            <MessageBarBody>{error}</MessageBarBody>
                          </MessageBar>
                        ))}
                      </div>
                    </section>
                  )}
                  <section aria-labelledby="defaults-proposed-actions">
                    <Text id="defaults-proposed-actions" weight="semibold">Proposed built-in skill actions</Text>
                    <div className={styles.defaultsRows} role="list">
                      {defaultsPreview.assignments.length === 0
                        ? <Text className={styles.defaultsMeta}>No bundled skill defaults are available for this blueprint.</Text>
                        : defaultsPreview.assignments.map((action, index) => (
                          <div className={styles.defaultsRow} role="listitem" key={`${action.role_id}:${action.skill_name}:${action.action}:${index}`}>
                            <Badge appearance="outline">{action.role_id}</Badge>
                            <Text>{action.skill_name}</Text>
                            <Badge appearance="tint" color={action.action === 'blocked' ? 'danger' : action.action === 'reactivate' ? 'warning' : 'success'}>{action.action}</Badge>
                            <Text className={styles.defaultsMeta}>→ {action.agent_name}</Text>
                            {action.action === 'blocked' && <Text className={styles.defaultsMeta}>A manually managed skill has the same name and will not be changed.</Text>}
                          </div>
                        ))}
                    </div>
                  </section>
                </>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" onClick={() => closeDefaults()}>Close</Button>
              {(defaultsRequiresRepreview || (!defaultsPreview && busy !== 'defaults-preview')) && (
                <Button appearance="secondary" disabled={isBusy} onClick={() => void previewDefaults()}>Preview latest defaults</Button>
              )}
              <Button
                appearance="primary"
                disabled={!defaultsPreview || busy === 'defaults-preview' || !defaultsPreview.can_apply || defaultsRequiresRepreview}
                onClick={() => void applyDefaults()}
              >
                {busy === 'defaults-apply' ? 'Applying…' : 'Apply defaults'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* Add skill dialog */}
      <Dialog open={addOpen} onOpenChange={(_, d) => setAddOpen(d.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Add skill</DialogTitle>
            <DialogContent className={styles.formGrid}>
              <Field label="Name" required hint="Command slug: lowercase letters, numbers, and hyphens.">
                <Input value={skillName} onChange={(_, data) => setSkillName(data.value)} disabled={isBusy} placeholder="code-review" />
              </Field>
              <Field label="Display name" hint="Optional label for review before saving.">
                <Input value={skillDisplayName} onChange={(_, data) => setSkillDisplayName(data.value)} disabled={isBusy} placeholder="Code Review" />
              </Field>
              <Field label="Description">
                <Input value={skillDescription} onChange={(_, data) => setSkillDescription(data.value)} disabled={isBusy} />
              </Field>
              <Field label="Instructions" required>
                <Textarea value={skillInstructions} onChange={(_, data) => setSkillInstructions(data.value)} disabled={isBusy} rows={8} resize="vertical" />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={isBusy} onClick={() => setAddOpen(false)}>Cancel</Button>
              <Button appearance="primary" disabled={isBusy || !skillName.trim() || !skillInstructions.trim()} onClick={() => void onCreateSkill()}>
                {busy === 'Create skill' ? 'Creating…' : 'Create skill'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* Generate skill dialog */}
      <Dialog open={generateOpen} onOpenChange={(_, d) => setGenerateOpen(d.open)}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Generate skill</DialogTitle>
            <DialogContent className={styles.formGrid}>
              <Field label="Describe the skill to generate" required>
                <Textarea value={generatePrompt} onChange={(_, data) => setGeneratePrompt(data.value)} disabled={isBusy} rows={4} resize="vertical" />
              </Field>
              <Button appearance="secondary" disabled={isBusy || !generatePrompt.trim()} onClick={() => void onGenerateSkill()}>
                {busy === 'Generate skill' ? 'Generating…' : 'Generate'}
              </Button>
              {(skillName || skillInstructions) && (
                <>
                  <Field label="Name" required hint="Review and edit before creating.">
                    <Input value={skillName} onChange={(_, data) => setSkillName(data.value)} disabled={isBusy} />
                  </Field>
                  <Field label="Display name">
                    <Input value={skillDisplayName} onChange={(_, data) => setSkillDisplayName(data.value)} disabled={isBusy} />
                  </Field>
                  <Field label="Description">
                    <Input value={skillDescription} onChange={(_, data) => setSkillDescription(data.value)} disabled={isBusy} />
                  </Field>
                  <Field label="Instructions" required>
                    <Textarea value={skillInstructions} onChange={(_, data) => setSkillInstructions(data.value)} disabled={isBusy} rows={8} resize="vertical" />
                  </Field>
                </>
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={isBusy} onClick={() => setGenerateOpen(false)}>Cancel</Button>
              <Button appearance="primary" disabled={isBusy || !skillName.trim() || !skillInstructions.trim()} onClick={() => void onCreateSkill()}>
                Create
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>

      {/* Import dialog */}
      <Dialog open={importOpen} onOpenChange={(_, d) => { setImportOpen(d.open); if (!d.open) { setCandidates(null); setSourceUrl(''); } }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Import skill</DialogTitle>
            <DialogContent className={styles.formGrid}>
              <MessageBar intent="warning"><MessageBarBody>Only import skills from sources you trust. Imported skills can change how the agent behaves.</MessageBarBody></MessageBar>
              <div
                className={styles.dropzone}
                role="button"
                tabIndex={0}
                onClick={() => mdFileInputRef.current?.click()}
                onDrop={(e) => { void onDropUpload(e); }}
                onDragOver={(e) => e.preventDefault()}
              >
                <Text weight="semibold">Drop .md skill files here</Text>
                <Text style={{ color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 }}>or click to browse</Text>
              </div>
              <div
                className={styles.dropzone}
                role="button"
                tabIndex={0}
                onClick={() => folderInputRef.current?.click()}
                onDrop={(e) => { void onDropUpload(e); }}
                onDragOver={(e) => e.preventDefault()}
              >
                <Text weight="semibold">Drop a skill folder here</Text>
                <Text style={{ color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 }}>Directory with SKILL.md and supporting files</Text>
              </div>
              <input
                ref={mdFileInputRef}
                type="file"
                multiple
                accept=".md,text/markdown"
                className={styles.hiddenInput}
                onChange={(e) => void onUploadFiles(e.target.files)}
                data-testid="skill-upload-input"
              />
              <input
                ref={folderInputRef}
                type="file"
                multiple
                className={styles.hiddenInput}
                onChange={(e) => void onUploadFiles(e.target.files)}
                {...{ webkitdirectory: '', directory: '' }}
              />
              <Field label="Paste raw SKILL.md URL or GitHub repo/folder URL" required>
                <Input
                  value={sourceUrl}
                  placeholder="https://github.com/org/repo/tree/main/skills"
                  onChange={(_, data) => setSourceUrl(data.value)}
                  disabled={isBusy}
                />
              </Field>
              <Text style={{ color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 }}>On GitHub, open a SKILL.md, click Raw, copy the URL.</Text>
              {candidates !== null && (
                candidates.length === 0
                  ? <Text style={{ color: tokens.colorNeutralForeground3, fontStyle: 'italic' }}>No candidate skills found. Try a SKILL.md, a folder of skill directories, or a recognized skills folder.</Text>
                  : (
                    <div className={styles.candidateList}>
                      {candidates.map((c) => (
                        <div key={c.location} className={styles.candidate}>
                          <Checkbox
                            label={`${c.name ?? c.location}${c.valid ? '' : ' (invalid)'}`}
                            checked={selectedLocations.has(c.location)}
                            disabled={!c.valid || isBusy}
                            onChange={(_, data) => {
                              setSelectedLocations((prev) => {
                                const next = new Set(prev);
                                if (data.checked === true) next.add(c.location); else next.delete(c.location);
                                return next;
                              });
                            }}
                          />
                          {c.description && <span className={styles.itemMeta}>{c.description}</span>}
                          {c.errors.length > 0 && <span className={styles.itemMeta}>{c.errors.join('; ')}</span>}
                        </div>
                      ))}
                    </div>
                  )
              )}
            </DialogContent>
            <DialogActions>
              <Button appearance="secondary" disabled={isBusy || !sourceUrl.trim()} onClick={() => void onPreview()}>
                {busy === 'preview' ? 'Loading…' : 'Preview candidates'}
              </Button>
              <Button appearance="primary" disabled={isBusy || !sourceUrl.trim() || (candidates !== null && selectedLocations.size === 0)} onClick={() => void onImport()}>
                {busy === 'import' ? 'Importing…' : 'Import'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </PageContainer>
  );
}