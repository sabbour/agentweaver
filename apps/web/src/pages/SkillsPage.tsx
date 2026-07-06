import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
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
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  OverlayDrawer,
  DrawerHeader,
  DrawerHeaderTitle,
  DrawerBody,
  Spinner,
  Tab,
  TabList,
  Text,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import type { SelectTabData } from '@fluentui/react-components';
import {
  ArrowSync24Regular,
  BranchFork24Regular,
  ArrowUpload24Regular,
  Delete24Regular,
  Dismiss24Regular,
  Eye24Regular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import type {
  SkillDto,
  SkillDetailDto,
  SkillCandidateDto,
  SkillAcquisitionResponse,
  TeamMemberDto,
} from '../api/types';
import { PageHeader } from '../components/PageHeader';

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalL },
  breadcrumb: {
    display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'center',
    fontSize: tokens.fontSizeBase300, color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: { color: tokens.colorBrandForeground1, textDecoration: 'none' },
  tabContent: { marginTop: tokens.spacingVerticalM, display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  toolbar: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  empty: { color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  itemList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  item: {
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground2, display: 'flex', flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  itemHeader: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  itemTitle: { fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase300, flexGrow: 1 },
  itemMeta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase100 },
  itemDesc: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground1, lineHeight: '1.5' },
  agentChips: { display: 'flex', gap: tokens.spacingHorizontalXS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXS },
  actions: { display: 'flex', gap: tokens.spacingHorizontalS, flexWrap: 'wrap', marginTop: tokens.spacingVerticalXS },
  candidateList: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, marginTop: tokens.spacingVerticalM },
  candidate: {
    border: `1px dashed ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`, display: 'flex',
    flexDirection: 'column', gap: tokens.spacingVerticalXXS,
  },
  drawerContent: { fontSize: tokens.fontSizeBase200, whiteSpace: 'pre-wrap', lineHeight: '1.6', fontFamily: tokens.fontFamilyMonospace },
  assignGrid: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS, marginTop: tokens.spacingVerticalXS },
  assignRow: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  hiddenInput: { display: 'none' },
});

function formatApiError(err: unknown): string {
  if (err instanceof ApiError) return `API error ${err.status}: ${err.body || 'Request failed'}`;
  return err instanceof Error ? err.message : String(err);
}

function statusColor(status: string): 'success' | 'warning' | 'danger' | 'subtle' {
  if (status === 'active') return 'success';
  if (status === 'missing') return 'warning';
  if (status === 'malformed') return 'danger';
  return 'subtle';
}

function summarizeAcquisition(res: SkillAcquisitionResponse): string {
  const counts = { Added: 0, Updated: 0, Unchanged: 0, Rejected: 0 } as Record<string, number>;
  for (const r of res.results) counts[r.kind] = (counts[r.kind] ?? 0) + 1;
  const parts: string[] = [];
  if (counts.Added) parts.push(`${counts.Added} added`);
  if (counts.Updated) parts.push(`${counts.Updated} updated`);
  if (counts.Unchanged) parts.push(`${counts.Unchanged} unchanged`);
  if (counts.Rejected) parts.push(`${counts.Rejected} rejected`);
  if (res.marked_missing.length) parts.push(`${res.marked_missing.length} marked missing`);
  return parts.length ? parts.join(', ') : 'No changes.';
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

  // Detail drawer
  const [detail, setDetail] = useState<SkillDetailDto | null>(null);
  const [detailOpen, setDetailOpen] = useState(false);

  // Import dialog
  const [importOpen, setImportOpen] = useState(false);
  const [repoUrl, setRepoUrl] = useState('');
  const [candidates, setCandidates] = useState<SkillCandidateDto[] | null>(null);
  const [selectedLocations, setSelectedLocations] = useState<Set<string>>(new Set());

  const fileInputRef = useRef<HTMLInputElement>(null);

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

  const onPreview = async () => {
    if (!projectId || !repoUrl.trim()) return;
    setBusy('preview');
    setMutationError(null);
    setCandidates(null);
    try {
      const res = await apiClient.previewSkillImport(projectId, repoUrl.trim());
      setCandidates(res.candidates);
      setSelectedLocations(new Set(res.candidates.filter((c) => c.valid).map((c) => c.location)));
    } catch (err) {
      setMutationError(formatApiError(err));
    } finally {
      setBusy(null);
    }
  };

  const onImport = async () => {
    if (!projectId || !repoUrl.trim()) return;
    setBusy('import');
    setMutationError(null);
    try {
      const locs = candidates ? Array.from(selectedLocations) : undefined;
      const res = await apiClient.importSkills(projectId, repoUrl.trim(), locs && locs.length ? locs : undefined);
      setNotice(`Import: ${summarizeAcquisition(res)}`);
      setImportOpen(false);
      setRepoUrl('');
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
    if (fileInputRef.current) fileInputRef.current.value = '';
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

  return (
    <div className={styles.root}>
      <PageHeader
        title="Skills"
        subtitle="Import, sync, and assign reusable agent skills for this project."
        breadcrumb={
          <nav className={styles.breadcrumb} aria-label="Breadcrumb">
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>Project</Link>
            <span>/</span>
            <span>Skills</span>
          </nav>
        }
      />

      <TabList
        selectedValue={selectedTab}
        onTabSelect={(_e, d: SelectTabData) => setSelectedTab(d.value as 'catalog' | 'assignments')}
      >
        <Tab value="catalog">Catalog</Tab>
        <Tab value="assignments">Assignments</Tab>
      </TabList>

      <div className={styles.tabContent}>
        <div className={styles.toolbar}>
          <Button icon={<ArrowSync24Regular />} disabled={isBusy} onClick={onSync}>
            {busy === 'Sync' ? 'Syncing…' : 'Sync connected repo'}
          </Button>
          <Button icon={<BranchFork24Regular />} disabled={isBusy} onClick={() => setImportOpen(true)}>
            Import from repo
          </Button>
          <Button icon={<ArrowUpload24Regular />} disabled={isBusy} onClick={() => fileInputRef.current?.click()}>
            {busy === 'Upload' ? 'Uploading…' : 'Upload'}
          </Button>
          <input
            ref={fileInputRef}
            type="file"
            multiple
            className={styles.hiddenInput}
            onChange={(e) => void onUploadFiles(e.target.files)}
            data-testid="skill-upload-input"
          />
        </div>

        {loading && <Spinner size="small" label="Loading…" />}
        {loadError && (
          <MessageBar intent="error">
            <MessageBarBody>{loadError}</MessageBarBody>
            <Button size="small" onClick={reload}>Retry</Button>
          </MessageBar>
        )}
        {notice && (
          <MessageBar intent="success"><MessageBarBody>{notice}</MessageBarBody></MessageBar>
        )}
        {mutationError && (
          <MessageBar intent="error"><MessageBarBody>{mutationError}</MessageBarBody></MessageBar>
        )}

        {!loading && !loadError && selectedTab === 'catalog' && (
          skills === null || skills.length === 0
            ? <Text className={styles.empty}>No skills in the catalog yet. Sync the connected repo, import from a Git repo, or upload a skill.</Text>
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
                        {s.assigned_agents.map((a) => <Badge key={a} appearance="tint" color="brand">{a}</Badge>)}
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
            ? <Text className={styles.empty}>No skills to assign. Add skills in the Catalog tab first.</Text>
            : members.length === 0
              ? <Text className={styles.empty}>No agents in this project's team yet. Cast a team to assign skills.</Text>
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
                                label={m.name}
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
            ? <Spinner size="small" label="Loading skill…" />
            : (
              <>
                <Text as="p">{detail.description}</Text>
                {detail.resources.length > 0 && (
                  <Text as="p" className={styles.itemMeta}>{detail.resources.length} bundled resource(s)</Text>
                )}
                <div className={styles.drawerContent}>{detail.instructions}</div>
              </>
            )}
        </DrawerBody>
      </OverlayDrawer>

      {/* Import dialog */}
      <Dialog open={importOpen} onOpenChange={(_, d) => { setImportOpen(d.open); if (!d.open) { setCandidates(null); setRepoUrl(''); } }}>
        <DialogSurface>
          <DialogBody>
            <DialogTitle>Import skills from a Git repo</DialogTitle>
            <DialogContent>
              <Field label="Repository URL" required>
                <Input
                  value={repoUrl}
                  placeholder="https://github.com/org/repo"
                  onChange={(_, data) => setRepoUrl(data.value)}
                  disabled={isBusy}
                />
              </Field>
              {candidates !== null && (
                candidates.length === 0
                  ? <Text className={styles.empty}>No candidate skills found in recognized locations.</Text>
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
              <Button appearance="secondary" disabled={isBusy || !repoUrl.trim()} onClick={() => void onPreview()}>
                {busy === 'preview' ? 'Loading…' : 'Preview candidates'}
              </Button>
              <Button appearance="primary" disabled={isBusy || !repoUrl.trim() || (candidates !== null && selectedLocations.size === 0)} onClick={() => void onImport()}>
                {busy === 'import' ? 'Importing…' : 'Import'}
              </Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
    </div>
  );
}
