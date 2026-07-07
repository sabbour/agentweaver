import { type ReactElement, type ReactNode, useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Badge,
  Button,
  Card,
  CardHeader,
  Combobox,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  Option,
  Spinner,
  Text,
  Textarea,
  Toast,
  ToastBody,
  ToastTitle,
  Toaster,
  useId,
  useToastController,
  makeStyles,
  mergeClasses,
  tokens,
} from '@fluentui/react-components';
import {
  ChevronDownRegular,
  ChevronRightRegular,
  ChevronUpRegular,
  CheckmarkCircleRegular,
  DismissCircleRegular,
  DismissRegular,
  SparkleRegular,
} from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
import { GITHUB_AUTHORIZE_URL } from '../config';
import { ApiError } from '../api/client';
import type { CreateProjectRequest, GitHubAccount, GitHubRepo, Project } from '../api/types';
import { PageHeader } from '../components/PageHeader';
import {
  BlueprintPanel,
  applyBlueprintToRequest,
  NO_BLUEPRINT,
  useBlueprintGeneration,
  type BlueprintSelection,
} from '../components/BlueprintPicker';
import { useProjectList } from '../hooks/useProjectList';
import { GitHubIcon } from '../components/GitHubIcon';

/** Normalizes an owner/repo string or existing https URL to a full GitHub HTTPS URL. */
function toGitHubUrl(val: string): string {
  const v = val.trim();
  if (v.startsWith('https://')) return v;
  if (/^[\w.-]+\/[\w.-]/.test(v)) return `https://github.com/${v}`;
  return v;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
    gap: tokens.spacingVerticalM,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
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
  cardMeta: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  cardOriginRow: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  cardGitHubMark: {
    display: 'flex',
    alignItems: 'center',
    color: tokens.colorNeutralForeground1,
  },
  cardRepo: {
    color: tokens.colorNeutralForeground3,
    overflow: 'hidden',
    textOverflow: 'ellipsis',
    whiteSpace: 'nowrap',
  },
  cardDir: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    wordBreak: 'break-all',
  },
  cardWarning: {
    color: tokens.colorPaletteMarigoldForeground1,
    fontSize: tokens.fontSizeBase200,
  },
  cardActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalS,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    alignItems: 'flex-start',
    padding: `${tokens.spacingVerticalXXL} 0`,
    maxWidth: '640px',
  },
  emptyBody: {
    color: tokens.colorNeutralForeground3,
  },
  emptyActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    flexWrap: 'wrap',
    marginTop: tokens.spacingVerticalS,
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
  orgRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS, padding: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1, cursor: 'pointer', textAlign: 'left' },
  pasteRow: { display: 'flex', gap: tokens.spacingHorizontalS },
  growInput: { flex: 1 },
  accountOption: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  accountAvatar: {
    width: '28px',
    height: '28px',
    borderRadius: '50%',
    flexShrink: 0,
  },
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
      if (origin === 'github') req.source_repository = toGitHubUrl(sourceRepository.trim());
      applyBlueprintToRequest(req, blueprint);
      const project = await apiClient.createProject(req);
      onCreated(project);
      setOpen(false);
      reset();
    } catch (err) {
      setError(
        err instanceof ApiError
          ? `API error ${err.status}: ${err.body}`
          : err instanceof Error
            ? err.message
            : String(err),
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
    <Dialog open={open} onOpenChange={(_, state) => onOpenChange(state.open)}>
      <DialogTrigger disableButtonEnhancement>{trigger}</DialogTrigger>
      <DialogSurface
        className={styles.dialogSurface}
        backdrop={{ appearance: 'dimmed', className: styles.dialogBackdrop }}
      >
        <DialogTrigger disableButtonEnhancement>
          <Button className={styles.closeButton} appearance="transparent" icon={<DismissRegular />} aria-label="Close" />
        </DialogTrigger>
        <DialogBody>
          <div className={styles.dialogHeader}>
            <div className={styles.titleBlock}>
              <span className={styles.headerIcon}>{icon}</span>
              <div>
                <DialogTitle>{title}</DialogTitle>
                <Text className={styles.subtitle}>{subtitle}</Text>
              </div>
            </div>
          </div>
          <DialogContent className={styles.dialogContent}>
            <div className={styles.dialogTwoCol}>
              <div className={styles.dialogLeftCol}>{left}</div>
              <div className={styles.dialogRightCol}>{right}</div>
            </div>
          </DialogContent>
          <DialogActions className={styles.dialogActions}>
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
                <DialogTrigger disableButtonEnhancement><Button appearance="transparent" disabled={saving}>Cancel</Button></DialogTrigger>
                <Button aria-label="Create" appearance="primary" disabled={!canCreate} onClick={onCreate}>
                  {saving ? 'Creating' : 'Create project'}
                </Button>
                {saving && <Spinner size="extra-tiny" aria-hidden="true" />}
              </div>
            </div>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
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
      trigger={<Button appearance="primary">Create blank project</Button>}
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

function useGitHubData(open: boolean) {
  const [accounts, setAccounts] = useState<GitHubAccount[]>([]);
  const [accountsLoading, setAccountsLoading] = useState(false);
  const [authRequired, setAuthRequired] = useState(false);
  const [accountsError, setAccountsError] = useState<string | null>(null);
  const [accountsKey, setAccountsKey] = useState(0);

  const [selectedAccount, setSelectedAccount] = useState<GitHubAccount | null>(null);
  const [repos, setRepos] = useState<GitHubRepo[]>([]);
  const [reposLoading, setReposLoading] = useState(false);
  const [reposError, setReposError] = useState<string | null>(null);
  const [reposKey, setReposKey] = useState(0);

  // Reset all state when the dialog (re-)opens.
  const [prevOpen, setPrevOpen] = useState(open);
  if (open !== prevOpen) {
    setPrevOpen(open);
    if (open) {
      setAccounts([]);
      setAccountsLoading(false);
      setAuthRequired(false);
      setAccountsError(null);
      setSelectedAccount(null);
      setRepos([]);
      setReposLoading(false);
      setReposError(null);
    }
  }

  // Load accounts when dialog opens.
  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setAccountsLoading(true);
    setAuthRequired(false);
    setAccountsError(null);
    apiClient.listGitHubAccounts()
      .then((data) => {
        if (cancelled) return;
        setAccounts(data);
        setAccountsLoading(false);
        // Auto-select the authenticated user (first entry).
        if (data.length > 0) setSelectedAccount(data[0]);
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setAccountsLoading(false);
        if (err instanceof ApiError && err.status === 401) {
          setAuthRequired(true);
        } else {
          setAccountsError(
            err instanceof ApiError
              ? `Error ${err.status}: ${err.body}`
              : err instanceof Error ? err.message : String(err),
          );
        }
      });
    return () => { cancelled = true; };
  }, [open, accountsKey]);

  // Load repos whenever the selected account changes.
  useEffect(() => {
    if (!selectedAccount) { setRepos([]); return; }
    let cancelled = false;
    setReposLoading(true);
    setReposError(null);
    apiClient.listGitHubRepos(selectedAccount.login)
      .then((data) => {
        if (!cancelled) { setRepos(data); setReposLoading(false); }
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setReposLoading(false);
        setReposError(
          err instanceof ApiError
            ? `Error ${err.status}: ${err.body}`
            : err instanceof Error ? err.message : String(err),
        );
        setRepos([]);
      });
    return () => { cancelled = true; };
  }, [selectedAccount, reposKey]);

  const changeAccount = (acc: GitHubAccount) => {
    setSelectedAccount(acc);
    setRepos([]);
    setReposError(null);
  };

  const reloadAccounts = () => setAccountsKey((k) => k + 1);
  const reloadRepos = () => setReposKey((k) => k + 1);

  return {
    accounts, accountsLoading, authRequired, accountsError,
    selectedAccount, changeAccount,
    repos, reposLoading, reposError,
    reloadAccounts, reloadRepos,
  };
}

function CreateFromGitHubDialog({ onCreated, dataDir, workspaceAutoAssigned }: { onCreated: (p: Project) => void; dataDir: string | null; workspaceAutoAssigned: boolean }) {
  const styles = useStyles();
  const d = useCreateProjectDialog('github', onCreated);
  const {
    accounts, accountsLoading, authRequired, accountsError,
    selectedAccount, changeAccount,
    repos, reposLoading, reposError,
    reloadAccounts, reloadRepos,
  } = useGitHubData(d.open);
  const [repoFilter, setRepoFilter] = useState('');
  const [pasteRepo, setPasteRepo] = useState('');
  const [folderName, setFolderName] = useState('');
  const [folderEdited, setFolderEdited] = useState(false);
  const [showMoreSources, setShowMoreSources] = useState(false);
  const [generateDescription, setGenerateDescription] = useState('');
  const generation = useBlueprintGeneration(d.setBlueprint, d.sourceRepository);

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
    .filter(r => r.fullName?.toLowerCase().includes(repoFilter.toLowerCase()) ?? false)
    .sort((a, b) => {
      const nameA = (a.fullName?.split('/').pop() ?? '').toLowerCase();
      const nameB = (b.fullName?.split('/').pop() ?? '').toLowerCase();
      return nameA.localeCompare(nameB);
    });
  const visibleSources = showMoreSources ? accounts : accounts.slice(0, 5);

  const resetLocal = () => {
    d.reset(); setRepoFilter(''); setPasteRepo(''); setFolderName(''); setFolderEdited(false); setShowMoreSources(false); setGenerateDescription(''); generation.setGenerated(null);
  };

  const left = (
    <div className={styles.repositoryPanel}>
      <div className={styles.repoSelector}>
        <Text weight="semibold">Repository *</Text>
        <Combobox
          aria-label="Repository"
          freeform
          placeholder={accountsLoading ? 'Loading...' : reposLoading ? 'Loading repositories...' : 'Search or select a repository'}
          value={d.sourceRepository}
          onInput={(e) => { const val = (e.target as HTMLInputElement).value; setRepoFilter(val); d.setSourceRepository(val); if (val.includes('/')) applyRepo(val); }}
          onOptionSelect={(_, data) => { if (data.optionValue) applyRepo(data.optionValue); }}
          disabled={accountsLoading}
        >
          {filteredRepos.map((repo) => {
            const fullName = repo.fullName ?? '';
            return (
              <Option key={fullName} value={fullName} text={fullName}>
                <span className={styles.repoOption}><span className={styles.githubMark}>GH</span><Text weight="semibold">{repoDisplayName(fullName)}</Text></span>
              </Option>
            );
          })}
        </Combobox>
        <Text className={styles.tipLine}>Start typing to search any owner/repository on GitHub.</Text>
      </div>

      <Field label="Project name">
        <Input
          value={d.name}
          onChange={(_, v) => { d.setName(v.value); if (!d.sourceRepository.trim() && !folderEdited) setWorkspaceSlug(slugify(v.value)); }}
          placeholder="My project"
        />
      </Field>

      {authRequired && (
        <MessageBar intent="warning">
          <MessageBarBody>Connect your GitHub account to list repositories, including private ones you can access. Public repos can still be pasted below without connecting.</MessageBarBody>
          <MessageBarActions><Button size="small" onClick={() => { window.location.href = GITHUB_AUTHORIZE_URL; }}>Connect GitHub</Button></MessageBarActions>
        </MessageBar>
      )}
      {accountsError && <MessageBar intent="error"><MessageBarBody>Could not load accounts: {accountsError}</MessageBarBody><MessageBarActions><Button size="small" onClick={reloadAccounts}>Retry</Button></MessageBarActions></MessageBar>}
      {reposError && <MessageBar intent="error"><MessageBarBody>Could not load repositories: {reposError}</MessageBarBody><MessageBarActions><Button size="small" onClick={reloadRepos}>Retry</Button></MessageBarActions></MessageBar>}

      {!authRequired && (
        <div className={styles.listBlock}>
          <div className={styles.listHeader}>
            <Text weight="semibold">My organizations</Text>
            {accounts.length > 5 && (
              <Button
                appearance="transparent"
                size="small"
                icon={showMoreSources ? <ChevronUpRegular /> : <ChevronDownRegular />}
                iconPosition="after"
                onClick={() => setShowMoreSources(!showMoreSources)}
              >
                {showMoreSources ? 'Show less' : 'Show more'}
              </Button>
            )}
          </div>
          {visibleSources.length === 0 ? <Text className={styles.tipLine}>{accountsLoading ? 'Loading sources…' : 'No GitHub sources found.'}</Text> : visibleSources.map((acc) => (
            <button key={acc.login} className={styles.orgRow} type="button" onClick={() => { changeAccount(acc); setRepoFilter(''); d.setSourceRepository(''); }}>
              <span className={styles.accountOption}>
                <img src={acc.avatar_url} alt="" className={styles.accountAvatar} />
                <span><Text weight="semibold">{acc.name ?? acc.login}</Text><br /><Text className={styles.tipLine}>@{acc.login}</Text></span>
                {acc.type === 'user' && <Badge size="small" appearance="outline">You</Badge>}
              </span>
              <ChevronRightRegular />
            </button>
          ))}
          {selectedAccount && <Text className={styles.tipLine}>Browsing @{selectedAccount.login} repositories</Text>}
        </div>
      )}

      <Field label="Or paste any repository" hint="owner/repo e.g. kubernetes/client-go">
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
      trigger={<Button appearance="secondary">Create from GitHub</Button>}
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
  return (
    <Card className={highlight ? mergeClasses(styles.card, styles.cardHighlight) : styles.card}>
      <CardHeader
        image={isGitHub ? (
          <span className={styles.cardGitHubMark}>
            <GitHubIcon
              size={20}
              title={project.source_repository
                ? `Connected to GitHub: ${formatSourceRepository(project.source_repository)}`
                : 'Connected to GitHub'}
            />
          </span>
        ) : undefined}
        header={<Text weight="semibold" size={400}>{project.name}</Text>}
        action={
          <Badge appearance="tint" size="small" color={project.available ? 'success' : 'warning'}>
            {project.available ? 'Available' : 'Unavailable'}
          </Badge>
        }
      />
      <div className={styles.cardMeta}>
        <div className={styles.cardOriginRow}>
          <Badge appearance="outline" size="small">{isGitHub ? 'GitHub' : 'Blank'}</Badge>
          {project.source_repository && (
            <Text size={200} className={styles.cardRepo}>{formatSourceRepository(project.source_repository)}</Text>
          )}
        </div>
        <Text className={styles.cardDir}>{project.working_directory}</Text>
        {!project.available && (
          <Text className={styles.cardWarning}>Working directory may have moved or become inaccessible.</Text>
        )}
      </div>
      <div className={styles.cardActions}>
        <Button appearance="primary" size="small" onClick={onOpen}>Open</Button>
      </div>
    </Card>
  );
}

export function ProjectGalleryPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { projects, loading, authError, loadError, errorMessage, appendProject, refetch } = useProjectList();
  const [dataDir, setDataDir] = useState<string | null>(null);
  const [workspaceAutoAssigned, setWorkspaceAutoAssigned] = useState(false);
  const [highlightId, setHighlightId] = useState<string | null>(null);
  const toasterId = useId('project-gallery-toaster');
  const { dispatchToast } = useToastController(toasterId);

  useEffect(() => {
    let cancelled = false;
    apiClient.getServerInfo()
      .then((info) => {
        if (!cancelled) {
          setDataDir(info.data_directory);
          setWorkspaceAutoAssigned(info.workspace_auto_assigned ?? false);
        }
      })
      .catch(() => {});
    return () => { cancelled = true; };
  }, []);

  const handleCreated = (project: Project) => {
    const isFirstProject = projects.length === 0;
    appendProject(project);
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

  const showGalleryActions = !loading && !authError && projects.length > 0;

  return (
    <div className={styles.root}>
      <Toaster toasterId={toasterId} position="bottom-end" />
      <PageHeader
        title="Projects"
        subtitle="Open an existing project, or create one from GitHub or a blueprint."
        actions={showGalleryActions ? (
          <>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </>
        ) : undefined}
      />

      {loading && <Spinner label="Loading projects…" />}

      {!loading && authError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Sign in with GitHub to see your projects.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              size="small"
              onClick={() => { window.location.href = GITHUB_AUTHORIZE_URL; }}
            >
              Sign in with GitHub
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage ?? 'Failed to load projects.'}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={refetch}>Retry</Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {!loading && !loadError && !authError && projects.length === 0 && (
        <div className={styles.emptyState}>
          <Text weight="semibold" size={500}>No projects yet. Create one to get started.</Text>
          <Text className={styles.emptyBody}>
            A project pairs a working directory with a squad and workflow so agents can start real work right away.
            Import an existing GitHub repository, or start blank and describe a goal for a tailored blueprint.
          </Text>
          <div className={styles.emptyActions}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </div>
        </div>
      )}

      {!loading && projects.length > 0 && (
        <div className={styles.grid}>
          {projects.map((p) => (
            <ProjectCard
              key={p.project_id}
              project={p}
              onOpen={() => navigate(`/projects/${p.project_id}`)}
              highlight={p.project_id === highlightId}
            />
          ))}
        </div>
      )}
    </div>
  );
}
