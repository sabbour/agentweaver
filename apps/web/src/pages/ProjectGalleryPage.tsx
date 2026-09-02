import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { formatApiErrorMessage, isGitHubRepoAppConnectionRequired } from '../api/errors';
import {
  Badge,
  Button,
  Combobox,
  DialogTitle,
  Field,
  Input,
  makeStyles,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  Textarea,
  Toast,
  Toaster,
  ToastBody,
  ToastTitle,
  tokens,
  useId,
  useToastController,
} from '@fluentui/react-components';
import { AddRegular, CheckmarkCircleRegular, DismissCircleRegular, SparkleRegular } from '@fluentui/react-icons';
import { BlueprintPanel } from '../components/BlueprintPicker';
import { applyBlueprintToRequest, NO_BLUEPRINT, useBlueprintGeneration } from '../components/BlueprintPicker.helpers';
import { GitHubIcon } from '../components/GitHubIcon';
import { CopilotAuthorizationResultNotice } from '../components/CopilotAuthorizationResultNotice';
import { AppDialog, EmptyState, LoadingState, PageContainer, PageHeader, Tile, TileGrid } from '../components/ui';
import { Pager } from '../copilot-fluent-system';
import { ENTRA_AUTHORIZE_URL } from '../config';
import { useProjectList } from '../hooks/useProjectList';
import { useEffect, useState } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import type {
  CreateProjectRequest,
  PagedResult,
  Project,
} from '../api/types';
import type { BlueprintSelection } from '../components/BlueprintPicker.helpers';
import type { ReactElement, ReactNode } from 'react';
const useStyles = makeStyles({
  // One-time entrance for a freshly-created project card: a brand ring that
  // fades out with a slight rise. Purely a "this is the new one" cue.
  cardHighlight: {
    animationName: {
      '0%': { boxShadow: `0 0 0 2px ${tokens.colorBrandStroke1}`, transform: 'translateY(6px)' },
      '70%': { boxShadow: `0 0 0 2px ${tokens.colorBrandStroke1}` },
      '100%': { boxShadow: '0 0 0 0 transparent', transform: 'translateY(0)' },
    },
    animationDuration: '1200ms',
    animationTimingFunction: tokens.curveDecelerateMid,
    animationFillMode: 'both',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', transform: 'none', boxShadow: 'none' },
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  // One coherent frame for the whole workflow (form + blueprint), not two
  // separate floating cards — the divider between columns reads as sections
  // of one panel, matching how the rest of the product groups related content.
  dialogTwoCol: {
    display: 'flex',
    alignItems: 'stretch',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 680px)': {
      flexDirection: 'column',
    },
  },
  dialogLeftCol: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    flex: '0 0 40%',
    width: '40%',
    minWidth: '320px',
    padding: '24px',
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    // Each column owns its own scroll within a bounded frame, so a short column
    // never leaves a large empty gap and a tall column scrolls in place instead
    // of forcing the whole modal to grow past the viewport.
    maxHeight: 'calc(100vh - 300px)',
    overflowY: 'auto',
    overflowX: 'hidden',
    '@media (max-width: 680px)': {
      width: '100%',
      minWidth: '0',
      borderRight: 'none',
      borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
      maxHeight: 'none',
      overflowY: 'visible',
    },
  },
  // Bounded, independently-scrolling column (see dialogLeftCol) — replaces the
  // previous single-scroll-owner design at the user's request.
  dialogRightCol: {
    display: 'flex',
    flexDirection: 'column',
    flex: '1 1 auto',
    padding: '24px',
    minWidth: '320px',
    gap: tokens.spacingVerticalM,
    maxHeight: 'calc(100vh - 300px)',
    overflowY: 'auto',
    overflowX: 'hidden',
    '@media (max-width: 680px)': {
      minWidth: '0',
      maxHeight: 'none',
      overflowY: 'visible',
    },
  },
  // No `position: relative` here on purpose: Fluent's default DialogSurface is
  // `position: fixed; inset: 0; margin: auto`, which centers the surface in the
  // viewport. Overriding position breaks that vertical centering and pins the
  // dialog to the top. A bounded max-height keeps comfortable margin above and
  // below; the close button (position: absolute) still anchors to this surface
  // because `position: fixed` is a containing block for absolute descendants.
  dialogSurface: { maxWidth: '1180px', width: 'min(1180px, calc(100vw - 48px))', maxHeight: 'calc(100vh - 48px)', backgroundColor: tokens.colorNeutralBackground2, padding: tokens.spacingVerticalXL },
  // Pin the scrim to the whole viewport. Fluent already sizes its backdrop this
  // way, but forcing it here (together with `appearance: 'dimmed'` on the slot)
  // guarantees the dim covers the full window regardless of surface height,
  // centering, or Fluent's nested-dialog detection (which would otherwise make
  // the backdrop transparent).
  dialogBackdrop: { position: 'fixed', top: 0, right: 0, bottom: 0, left: 0 },
  // The two columns each own their scroll, so DialogContent must not add a
  // second (double) scrollbar of its own — let it size to its children.
  dialogContent: { overflow: 'visible' },
  // Fluent lays DialogActions into a single grid track by default, which is why
  // the footer looked centered/narrow. Force it to span the full body width so
  // the space-between split (No blueprint on the left, actions on the right)
  // reaches both edges.
  dialogActions: { gridColumn: '1 / -1', justifySelf: 'stretch', width: '100%', maxWidth: '100%', margin: 0 },
  closeButton: { position: 'absolute', right: tokens.spacingHorizontalM, top: tokens.spacingVerticalM, minWidth: '32px', border: 0 },
  dialogHeader: {
    display: 'flex',
    alignItems: 'flex-start',
    justifyContent: 'space-between',
    gap: tokens.spacingHorizontalM,
  },
  titleBlock: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalM },
  headerIcon: {
    width: '36px',
    height: '36px',
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    color: tokens.colorBrandForeground1,
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    flexShrink: 0,
  },
  subtitle: { color: tokens.colorNeutralForeground3, marginTop: tokens.spacingVerticalXXS },
  charCounter: { alignSelf: 'flex-end', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  tipLine: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  fieldWithCounter: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  tabToggle: {
    display: 'inline-flex', gap: tokens.spacingHorizontalXS, padding: tokens.spacingVerticalXXS,
    backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusXLarge, alignSelf: 'flex-start',
  },
  // Border + top padding keeps the action bar reading as a distinct, attached
  // footer rather than content that trails off — true whether or not the panel
  // above it is scrolled.
  footerSplit: {
    display: 'flex',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
    alignItems: 'center',
    width: '100%',
    gap: tokens.spacingHorizontalL,
    rowGap: tokens.spacingVerticalS,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalM,
  },
  footerLeft: { display: 'flex', flexDirection: 'row', alignItems: 'center', gap: tokens.spacingHorizontalM, marginRight: 'auto', flexWrap: 'wrap' },
  // Selected state for the "No blueprint" toggle. Mirrors the brand-emphasis of a
  // selected template card (colorBrandStroke1) so "no blueprint" reads as an active
  // choice, without a loud primary fill that would compete with "Create project".
  // The fill is held steady across hover/active/focus so the selected state doesn't
  // flicker back to neutral when the pointer is over the button.
  noBlueprintActive: {
    borderTopColor: tokens.colorBrandStroke1,
    borderRightColor: tokens.colorBrandStroke1,
    borderBottomColor: tokens.colorBrandStroke1,
    borderLeftColor: tokens.colorBrandStroke1,
    color: tokens.colorBrandForeground1,
    backgroundColor: tokens.colorBrandBackground2,
    ':hover': {
      borderTopColor: tokens.colorBrandStroke1,
      borderRightColor: tokens.colorBrandStroke1,
      borderBottomColor: tokens.colorBrandStroke1,
      borderLeftColor: tokens.colorBrandStroke1,
      color: tokens.colorBrandForeground1,
      backgroundColor: tokens.colorBrandBackground2,
    },
    ':hover:active': {
      borderTopColor: tokens.colorBrandStroke1,
      borderRightColor: tokens.colorBrandStroke1,
      borderBottomColor: tokens.colorBrandStroke1,
      borderLeftColor: tokens.colorBrandStroke1,
      color: tokens.colorBrandForeground1,
      backgroundColor: tokens.colorBrandBackground2,
    },
  },
  footerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS, flexWrap: 'wrap' },
  repositoryPanel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  listBlock: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  listHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center' },
  pasteRow: { display: 'flex', gap: tokens.spacingHorizontalS },
  growInput: { flex: 1 },
  repoSelector: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  repoOption: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  githubMark: { width: '28px', height: '28px', borderRadius: tokens.borderRadiusCircular, backgroundColor: tokens.colorNeutralForeground1, color: tokens.colorNeutralBackground1, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontSize: tokens.fontSizeBase100, fontWeight: tokens.fontWeightBold, flexShrink: 0 },
});

function useCreateProjectDialog(origin: 'blank' | 'github', onCreated: (p: Project) => void) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState('');
  const [workingDirectory, setWorkingDirectory] = useState('');
  const [sourceRepository, setSourceRepository] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [blueprint, setBlueprint] = useState<BlueprintSelection>(NO_BLUEPRINT);

  const reset = () => {
    setName('');
    setWorkingDirectory('');
    setSourceRepository('');
    setError(null);
    setSaving(false);
    setBlueprint(NO_BLUEPRINT);
  };

  const handleSubmit = async () => {
    if (!name.trim() || !workingDirectory.trim()) return;
    if (origin === 'github' && !sourceRepository.trim()) return;
    setSaving(true);
    setError(null);
    try {
      const req: CreateProjectRequest = {
        name: name.trim(),
        origin,
        working_directory: workingDirectory.trim(),
      };
      if (origin === 'github') {
        const selections = await apiClient.listGitHubRepositorySelections();
        const selected = selections.repositories.find((repository) =>
          repository.full_name.localeCompare(sourceRepository.trim(), undefined, { sensitivity: 'accent' }) === 0);
        if (!selected) {
          throw new Error('Select a repository available through your Repo App authorization.');
        }
        const issued = await apiClient.issueGitHubRepositorySelection(selected.full_name);
        req.repository_selection_code = issued.selection_code;
      }
      applyBlueprintToRequest(req, blueprint);
      const project = await apiClient.createProject(req);
      onCreated(project);
      setOpen(false);
      reset();
    } catch (err) {
      setError(
        formatApiErrorMessage(err, 'Could not load projects.'),
      );
    } finally {
      setSaving(false);
    }
  };

  return {
    open, setOpen, name, setName, workingDirectory, setWorkingDirectory,
    sourceRepository, setSourceRepository,
    saving, error, handleSubmit, reset,
    blueprint, setBlueprint,
  };
}

function slugify(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

function Counter({ value, max }: { value: string; max: number }) {
  const styles = useStyles();
  return <Text className={styles.charCounter}>{value.length} / {max}</Text>;
}

function workspacePath(dataDir: string | null, slug: string) {
  return dataDir ? `${dataDir}/${slug}` : slug;
}

function CreateProjectDialogShell({
  open,
  onOpenChange,
  trigger,
  icon,
  title,
  subtitle,
  left,
  right,
  saving,
  canCreate,
  onCreate,
  onNoBlueprint,
  noBlueprintSelected,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  trigger: ReactElement;
  icon: ReactNode;
  title: string;
  subtitle: string;
  left: ReactNode;
  right: ReactNode;
  saving: boolean;
  canCreate: boolean;
  onCreate: () => void;
  onNoBlueprint: () => void;
  noBlueprintSelected: boolean;
}) {
  const styles = useStyles();
  return (
    <AppDialog
      open={open}
      onOpenChange={onOpenChange}
      trigger={trigger}
      maxWidth="1180px"
      // Keep open across in-dialog account/repo filter clicks. Default Fluent
      // `modal` closes when focus briefly leaves the surface during re-render
      // (reproduced: re-clicking the selected GitHub account dismissed the dialog).
      modalType="alert"
    >
      <div className={styles.dialogHeader}>
        <div className={styles.titleBlock}>
          <span className={styles.headerIcon}>{icon}</span>
          <div>
            <DialogTitle>{title}</DialogTitle>
            <Text className={styles.subtitle}>{subtitle}</Text>
          </div>
        </div>
      </div>
      <div className={styles.dialogContent}>
        <div className={styles.dialogTwoCol}>
          <div className={styles.dialogLeftCol}>{left}</div>
          <div className={styles.dialogRightCol}>{right}</div>
        </div>
      </div>
      <div className={styles.dialogActions}>
        <div className={styles.footerSplit}>
          <div className={styles.footerLeft}>
            <Button
              appearance="outline"
              className={noBlueprintSelected ? styles.noBlueprintActive : undefined}
              aria-label="No blueprint"
              aria-pressed={noBlueprintSelected}
              icon={noBlueprintSelected ? <CheckmarkCircleRegular /> : <DismissCircleRegular />}
              onClick={onNoBlueprint}
            >
              No blueprint
            </Button>
            <Text className={styles.tipLine}>
              {noBlueprintSelected
                ? 'Selected. Your project starts empty; add agents later.'
                : 'Start with an empty project and add agents later.'}
            </Text>
          </div>
          <div className={styles.footerActions}>
            <Button appearance="transparent" disabled={saving} onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button aria-label="Create" appearance="primary" disabled={!canCreate} onClick={onCreate}>
              {saving ? 'Creating' : 'Create project'}
            </Button>
            {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
          </div>
        </div>
      </div>
    </AppDialog>
  );
}

function CreateBlankDialog({ onCreated, dataDir, workspaceAutoAssigned }: { onCreated: (p: Project) => void; dataDir: string | null; workspaceAutoAssigned: boolean }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('blank', onCreated);
  const [description, setDescription] = useState('');
  const [goal, setGoal] = useState('');
  const [folderName, setFolderName] = useState('');
  const [folderEdited, setFolderEdited] = useState(false);
  const generation = useBlueprintGeneration(d.setBlueprint);
  const canCreate = Boolean(d.name.trim() && d.workingDirectory.trim() && !d.saving);

  const resetLocal = () => { d.reset(); setDescription(''); setGoal(''); setFolderName(''); setFolderEdited(false); generation.setGenerated(null); };
  const setWorkspaceSlug = (slug: string) => {
    setFolderName(slug);
    d.setWorkingDirectory(workspaceAutoAssigned ? slug : workspacePath(dataDir, slug));
  };

  const left = (
    <>
      <Field label="Project name *">
        <Input
          value={d.name}
          onChange={(_, v) => { const slug = slugify(v.value); d.setName(v.value); if (!folderEdited) setWorkspaceSlug(slug); }}
          placeholder="My project"
        />
      </Field>
      {!workspaceAutoAssigned && (
        <Field label="Repository folder" required hint={dataDir ? `Folder name inside ${dataDir}` : 'Workspace folder'}>
          <Input
            contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
            value={folderName}
            onChange={(_, v) => { setFolderEdited(v.value !== ''); setWorkspaceSlug(v.value); }}
            placeholder="my-repo"
          />
        </Field>
      )}
      <div className={styles.fieldWithCounter}>
        <Field label="Description (optional)">
          <Textarea value={description} maxLength={500} onChange={(_, v) => setDescription(v.value)} placeholder="What is this project about?" resize="vertical" />
        </Field>
        <Counter value={description} max={500} />
      </div>
      {d.error && <MessageBar intent="error"><MessageBarBody>{d.error}</MessageBarBody></MessageBar>}
    </>
  );

  const right = (
    <BlueprintPanel
      active={d.open}
      tabs={['templates', 'generate']}
      value={d.blueprint}
      onChange={d.setBlueprint}
      generated={generation.generated}
      onGenerate={() => void generation.generate(goal)}
      generating={generation.generating}
      generationError={generation.error}
      generateDescription={goal}
      onGenerateDescriptionChange={setGoal}
    />
  );

  return (
    <CreateProjectDialogShell
      open={d.open}
      onOpenChange={(open) => { d.setOpen(open); if (!open) resetLocal(); }}
      trigger={<Button appearance="primary" icon={<AddRegular />}>Create blank project</Button>}
      icon={<SparkleRegular />}
      title="Create blank project"
      subtitle="Start from scratch and let Agentweaver design the right squad and workflow for you."
      left={left}
      right={right}
      saving={d.saving}
      canCreate={canCreate}
      onCreate={() => void d.handleSubmit()}
      onNoBlueprint={() => d.setBlueprint(NO_BLUEPRINT)}
      noBlueprintSelected={d.blueprint.kind === 'none'}
    />
  );
}

type RepoBrowserRepo = {
  fullName: string;
  private: boolean;
  defaultBranch: string;
  pushedAt: string | null;
};

function useGitHubData(open: boolean) {
  const [repos, setRepos] = useState<RepoBrowserRepo[]>([]);
  const [reposLoading, setReposLoading] = useState(false);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposConnectionRequired, setReposConnectionRequired] = useState(false);
  const [reposKey, setReposKey] = useState(0);

  useEffect(() => {
    if (!open) {
      return;
    }
    let cancelled = false;
    const loadRepositories = async () => {
      setReposLoading(true);
      setReposError(null);
      setReposConnectionRequired(false);
      try {
        const selections = await apiClient.listGitHubRepositorySelections();
        if (cancelled) return;
        setRepos(selections.repositories.map((repository) => ({
          fullName: repository.full_name,
          private: repository.private,
          defaultBranch: repository.default_branch,
          pushedAt: repository.pushed_at,
        })));
      } catch (err: unknown) {
        if (cancelled) return;
        setReposConnectionRequired(isGitHubRepoAppConnectionRequired(err));
        setReposError(formatApiErrorMessage(err, 'Could not load repositories.'));
      } finally {
        if (!cancelled) setReposLoading(false);
      }
    };
    void loadRepositories();
    return () => { cancelled = true; };
  }, [open, reposKey]);

  const reloadRepos = () => setReposKey((k) => k + 1);

  return {
    repos, reposLoading, reposError, reposConnectionRequired, reloadRepos,
  };
}


function CreateFromGitHubDialog({
  onCreated,
  dataDir,
  workspaceAutoAssigned,
  resumeSignal = 0,
}: {
  onCreated: (p: Project) => void;
  dataDir: string | null;
  workspaceAutoAssigned: boolean;
  resumeSignal?: number;
}) {
  const styles = useStyles();
  const location = useLocation();
  const d = useCreateProjectDialog('github', onCreated);
  const { repos, reposLoading, reposError, reposConnectionRequired, reloadRepos } = useGitHubData(d.open);
  const [repoFilter, setRepoFilter] = useState('');
  const [pasteRepo, setPasteRepo] = useState('');
  const [folderName, setFolderName] = useState('');
  const [folderEdited, setFolderEdited] = useState(false);
  const [generateDescription, setGenerateDescription] = useState('');
  const [connectingRepoApp, setConnectingRepoApp] = useState(false);
  const generation = useBlueprintGeneration(d.setBlueprint, d.sourceRepository);

  useEffect(() => {
    if (resumeSignal > 0) d.setOpen(true);
  }, [resumeSignal, d.setOpen]);

  const connectRepoApp = async () => {
    setConnectingRepoApp(true);
    try {
      const handoff = await apiClient.beginRepoAppAuthorization(`${location.pathname}${location.search}`);
      window.location.assign(handoff.authorization_url);
    } catch {
      setConnectingRepoApp(false);
    }
  };

  const hasChosenRepository = /^(https:\/\/github\.com\/)?[\w.-]+\/[\w.-]+/.test(d.sourceRepository.trim());
  const canCreate = Boolean(d.name.trim() && d.workingDirectory.trim() && hasChosenRepository && !d.saving);
  const setWorkspaceSlug = (slug: string) => {
    setFolderName(slug);
    d.setWorkingDirectory(workspaceAutoAssigned ? slug : workspacePath(dataDir, slug));
  };

  const applyRepo = (ownerRepoOrUrl: string) => {
    const normalized = ownerRepoOrUrl.startsWith('https://github.com/')
      ? ownerRepoOrUrl.slice('https://github.com/'.length)
      : ownerRepoOrUrl;
    const clean = normalized.replace(/\.git$/i, '').replace(/^\/+|\/+$/g, '');
    d.setSourceRepository(clean);
    setRepoFilter(clean);
    const slug = clean.split('/')[1] ?? clean;
    if (slug) {
      if (!folderEdited) setWorkspaceSlug(slugify(slug));
      if (!d.name.trim()) d.setName(slug.replace(/-/g, ' ').replace(/\b\w/g, (c) => c.toUpperCase()));
    }
  };
  const repoDisplayName = (fullName: string | null | undefined) => {
    if (!fullName) return '(unnamed)';
    const [owner, repo] = fullName.split('/');
    return repo ? `${owner} / ${repo}` : fullName;
  };

  const filteredRepos = repos
    .filter((r) => {
      const byName = r.fullName?.toLowerCase().includes(repoFilter.toLowerCase()) ?? false;
      return byName;
    })
    .sort((a, b) => {
      const nameA = (a.fullName?.split('/').pop() ?? '').toLowerCase();
      const nameB = (b.fullName?.split('/').pop() ?? '').toLowerCase();
      return nameA.localeCompare(nameB);
    });
  const resetLocal = () => {
    d.reset(); setRepoFilter(''); setPasteRepo(''); setFolderName(''); setFolderEdited(false); setGenerateDescription(''); generation.setGenerated(null);
  };

  const left = (
    <div className={styles.repositoryPanel}>
      <div className={styles.repoSelector}>
        <Text weight="semibold">Repository *</Text>
        <Combobox
          aria-label="Repository"
          freeform
          placeholder={reposLoading ? 'Loading repositories...' : 'Search or select a repository'}
          value={d.sourceRepository}
          onInput={(e) => { const val = (e.target as HTMLInputElement).value; setRepoFilter(val); d.setSourceRepository(val); if (val.includes('/')) applyRepo(val); }}
          onOptionSelect={(_, data) => { if (data.optionValue) applyRepo(data.optionValue); }}
          disabled={reposLoading}
        >
          {filteredRepos.map((repo) => {
            const fullName = repo.fullName ?? '';
            return (
              <Option key={fullName} value={fullName} text={fullName}>
                <span className={styles.repoOption}>
                  <span className={styles.githubMark}>GH</span>
                  <Text weight="semibold">{repoDisplayName(fullName)}</Text>
                </span>
              </Option>
            );
          })}
        </Combobox>
        <Text className={styles.tipLine}>
          Start typing to narrow repositories available through the Repo App. Import succeeds only after its authorization verifies the selection.
        </Text>
      </div>

      <Field label="Project name">
        <Input
          value={d.name}
          onChange={(_, v) => { d.setName(v.value); if (!d.sourceRepository.trim() && !folderEdited) setWorkspaceSlug(slugify(v.value)); }}
          placeholder="My project"
        />
      </Field>

      {reposError && (
        <MessageBar
          intent={reposConnectionRequired ? 'warning' : 'error'}
          data-testid="create-from-github-repositories-error"
          data-intent={reposConnectionRequired ? 'warning' : 'error'}
        >
          <MessageBarBody>{reposError}</MessageBarBody>
          <MessageBarActions>
            {reposConnectionRequired
              ? (
                <Button size="small" appearance="primary" disabled={connectingRepoApp} onClick={() => void connectRepoApp()}>
                  {connectingRepoApp ? 'Opening GitHub…' : 'Connect GitHub'}
                </Button>
              )
              : <Button size="small" onClick={reloadRepos}>Retry</Button>}
          </MessageBarActions>
        </MessageBar>
      )}

      <Field label="Paste a repository from your Repo App authorization" hint="owner/repo e.g. kubernetes/client-go">
        <div className={styles.pasteRow}>
          <Input className={styles.growInput} value={pasteRepo} onChange={(_, v) => setPasteRepo(v.value)} placeholder="owner/repo" />
          <Button appearance="secondary" disabled={!pasteRepo.trim()} onClick={() => applyRepo(pasteRepo)}>Go →</Button>
        </div>
      </Field>
      {!workspaceAutoAssigned && (
        <Field label="Repository folder" required hint={dataDir ? `Folder name inside ${dataDir}` : 'Workspace folder'}>
          <Input
            contentBefore={dataDir ? <Text size={200} style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>{dataDir}/</Text> : undefined}
            value={folderName}
            onChange={(_, v) => { setFolderEdited(v.value !== ''); setWorkspaceSlug(v.value); }}
            placeholder="my-repo"
          />
        </Field>
      )}

      {d.error && <MessageBar intent="error"><MessageBarBody>{d.error}</MessageBarBody></MessageBar>}
    </div>
  );

  const right = (
    <BlueprintPanel
      active={d.open}
      tabs={['suggested', 'templates', 'generate']}
      value={d.blueprint}
      onChange={d.setBlueprint}
      targetRepository={d.sourceRepository}
      generated={generation.generated}
      onGenerate={() => void generation.generate(generateDescription)}
      generating={generation.generating}
      generationError={generation.error}
      generateDescription={generateDescription}
      onGenerateDescriptionChange={setGenerateDescription}
    />
  );

  return (
    <CreateProjectDialogShell
      open={d.open}
      onOpenChange={(open) => { d.setOpen(open); if (!open) resetLocal(); }}
      trigger={<Button appearance="subtle" icon={<GitHubIcon size={16} />}>Create from GitHub</Button>}
      icon="GH"
      title="Create project from GitHub"
      subtitle="Import an existing repository and configure a project with Agentweaver."
      left={left}
      right={right}
      saving={d.saving}
      canCreate={canCreate}
      onCreate={() => void d.handleSubmit()}
      onNoBlueprint={() => d.setBlueprint(NO_BLUEPRINT)}
      noBlueprintSelected={d.blueprint.kind === 'none'}
    />
  );
}

function formatSourceRepository(url: string): string {
  return url.replace(/^https:\/\/github\.com\//, '');
}

function ProjectCard({ project, onOpen, highlight }: { project: Project; onOpen: () => void; highlight?: boolean }) {
  const styles = useStyles();
  const isGitHub = project.origin === 'github';

  const secondaryParts: string[] = [];
  if (isGitHub && project.source_repository) secondaryParts.push(formatSourceRepository(project.source_repository));
  secondaryParts.push(project.working_directory);
  if (!project.available) secondaryParts.push('Working directory may have moved or become inaccessible.');

  return (
    <Tile
      className={highlight ? styles.cardHighlight : undefined}
      media={isGitHub ? (
        <GitHubIcon
          size={20}
          title={project.source_repository
            ? `Connected to GitHub: ${formatSourceRepository(project.source_repository)}`
            : 'Connected to GitHub'}
        />
      ) : undefined}
      badges={
        <Badge appearance="tint" size="small" color={project.available ? 'success' : 'warning'}>
          {project.available ? 'Available' : 'Unavailable'}
        </Badge>
      }
      primary={project.name}
      secondary={secondaryParts.join(' · ')}
      meta={<Badge appearance="outline" size="small">{isGitHub ? 'GitHub' : 'Blank'}</Badge>}
      actions={<Button appearance="primary" size="small" onClick={onOpen}>Open</Button>}
      actionsAlwaysVisible
      onClick={onOpen}
    />
  );
}



export function ProjectGalleryPage() {
  const location = useLocation();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const { appendProject, refetch } = useProjectList();
  const [projectPage, setProjectPage] = useState<PagedResult<Project> | null>(null);
  const [loading, setLoading] = useState(true);
  const [authError, setAuthError] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const [errorMessage, setErrorMessage] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(12);
  const [reloadKey, setReloadKey] = useState(0);
  const [dataDir, setDataDir] = useState<string | null>(null);
  const [workspaceAutoAssigned, setWorkspaceAutoAssigned] = useState(false);
  const [highlightId, setHighlightId] = useState<string | null>(null);
  const [resumeCreateFromGitHubSignal, setResumeCreateFromGitHubSignal] = useState(0);
  const toasterId = useId('project-gallery-toaster');
  const { dispatchToast } = useToastController(toasterId);

  useEffect(() => {
    const controller = new AbortController();
    const loadProjects = async () => {
      setLoading(true);
      setAuthError(false);
      setLoadError(false);
      setErrorMessage(null);
      try {
        const result = await apiClient.listProjects({ page, pageSize, signal: controller.signal });
        if (!controller.signal.aborted) setProjectPage(result);
      } catch (err) {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) {
          setAuthError(true);
          return;
        }
        setLoadError(true);
        setErrorMessage(
          formatApiErrorMessage(err, 'Could not create the project.'),
        );
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    };
    void loadProjects();

    return () => controller.abort();
  }, [page, pageSize, reloadKey]);

  useEffect(() => {
    let cancelled = false;
    const loadServerInfo = async () => {
      try {
        const info = await apiClient.getServerInfo();
        if (!cancelled) {
          setDataDir(info.data_directory);
          setWorkspaceAutoAssigned(info.workspace_auto_assigned ?? false);
        }
      } catch {
        return undefined;
      }
    };
    void loadServerInfo();
    return () => { cancelled = true; };
  }, []);

  const handleCreated = (project: Project) => {
    const isFirstProject = (projectPage?.total_count ?? 0) === 0;
    appendProject(project);
    refetch();
    setProjectPage((current) => {
      const currentPageSize = current?.page_size ?? pageSize;
      const nextTotalCount = (current?.total_count ?? 0) + 1;
      if (!current) {
        return {
          items: [project],
          page: 1,
          page_size: currentPageSize,
          total_count: 1,
          total_pages: 1,
        };
      }
      if (current.page !== 1) {
        return {
          ...current,
          total_count: nextTotalCount,
          total_pages: Math.max(1, Math.ceil(nextTotalCount / currentPageSize)),
        };
      }
      return {
        ...current,
        items: [project, ...current.items.filter((p) => p.project_id !== project.project_id)].slice(0, currentPageSize),
        total_count: nextTotalCount,
        total_pages: Math.max(1, Math.ceil(nextTotalCount / currentPageSize)),
      };
    });
    setPage(1);
    setReloadKey((key) => key + 1);
    setHighlightId(project.project_id);
    dispatchToast(
      <Toast>
        <ToastTitle>{isFirstProject ? "You're set up" : 'Project created'}</ToastTitle>
        <ToastBody>
          {isFirstProject
            ? `'${project.name}' is your first project — open it to start.`
            : `'${project.name}' is ready to open.`}
        </ToastBody>
      </Toast>,
      { intent: 'success' },
    );
    // Clear the highlight once the one-time entrance animation has finished so
    // it never replays on a later re-render.
    window.setTimeout(() => {
      setHighlightId((current) => (current === project.project_id ? null : current));
    }, 1400);
  };

  const projects = projectPage?.items ?? [];
  const totalProjects = projectPage?.total_count ?? 0;
  const totalProjectPages = projectPage?.total_pages ?? 1;
  const showGalleryActions = !loading && !authError && totalProjects > 0;
  const copilotAuthorizationResult = searchParams.get('copilot_app_auth');

  const dismissCopilotAuthorizationResult = () => {
    const next = new URLSearchParams(searchParams);
    next.delete('copilot_app_auth');
    setSearchParams(next, { replace: true });
  };

  useEffect(() => {
    const repoAppAuth = searchParams.get('repo_app_auth');
    if (!repoAppAuth) return;

    if (repoAppAuth === 'success' && location.pathname === '/projects') {
      setResumeCreateFromGitHubSignal((current) => current + 1);
    }

    const next = new URLSearchParams(searchParams);
    next.delete('repo_app_auth');
    setSearchParams(next, { replace: true });
  }, [location.pathname, searchParams, setSearchParams]);

  return (
    <PageContainer>
      <Toaster toasterId={toasterId} position="bottom-end" />
      <PageHeader
        title="Projects"
        description="Open an existing project, or create one from GitHub or a blueprint."
        actions={showGalleryActions ? (
          <>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog
              onCreated={handleCreated}
              dataDir={dataDir}
              workspaceAutoAssigned={workspaceAutoAssigned}
              resumeSignal={resumeCreateFromGitHubSignal}
            />
          </>
        ) : undefined}
      />

      <CopilotAuthorizationResultNotice
        code={copilotAuthorizationResult}
        onDismiss={dismissCopilotAuthorizationResult}
      />

      {loading && <LoadingState label="Loading projects" rows={4} />}

      {!loading && authError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Sign in with Microsoft Entra ID to see your projects.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              size="small"
              onClick={() => { window.location.href = ENTRA_AUTHORIZE_URL; }}
            >
              Sign in with Microsoft Entra ID
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage ?? 'Failed to load projects.'}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={() => setReloadKey((key) => key + 1)}>Retry</Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {!loading && !loadError && !authError && totalProjects === 0 && (
        <EmptyState
          title="No projects yet"
          description="A project pairs a working directory with a squad and workflow so agents can start real work right away. Import an existing GitHub repository, or start blank and describe a goal for a tailored blueprint."
          action={
            <div style={{ display: 'flex', gap: tokens.spacingHorizontalM, flexWrap: 'wrap', justifyContent: 'center' }}>
              <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
              <CreateFromGitHubDialog
                onCreated={handleCreated}
                dataDir={dataDir}
                workspaceAutoAssigned={workspaceAutoAssigned}
                resumeSignal={resumeCreateFromGitHubSignal}
              />
            </div>
          }
        />
      )}

      {!loading && !loadError && !authError && totalProjects > 0 && (
        <>
          <TileGrid aria-label="Projects">
            {projects.map((p) => (
              <ProjectCard
                key={p.project_id}
                project={p}
                onOpen={() => navigate(`/projects/${p.project_id}`)}
                highlight={p.project_id === highlightId}
              />
            ))}
          </TileGrid>
          {totalProjectPages > 1 && (
            <Pager
              page={page}
              pageSize={pageSize}
              totalItems={totalProjects}
              pageSizeOptions={[12, 24, 48]}
              onPageChange={setPage}
              onPageSizeChange={(nextPageSize) => {
                setPageSize(nextPageSize);
                setPage(1);
              }}
            />
          )}
        </>
      )}
    </PageContainer>
  );
}
