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
  Title3,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { ChevronRightRegular, DismissRegular, DocumentRegular, InfoRegular, SparkleRegular } from '@fluentui/react-icons';
import { apiClient } from '../api/apiClient';
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
  toolbar: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
  },
  grid: {
    display: 'grid',
    gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))',
    gap: tokens.spacingVerticalM,
  },
  card: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  cardMeta: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  cardDir: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    wordBreak: 'break-all',
  },
  cardActions: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalS,
  },
  emptyState: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    alignItems: 'flex-start',
    padding: `${tokens.spacingVerticalXXL} 0`,
  },
  dialogFields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  dialogTwoCol: {
    display: 'flex',
    gap: '24px',
    alignItems: 'stretch',
    minHeight: '560px',
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
    minWidth: '360px',
    padding: '24px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 680px)': {
      width: '100%',
    },
  },
  dialogRightCol: {
    display: 'flex',
    flexDirection: 'column',
    flex: '1 1 auto',
    padding: '24px',
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1,
    minWidth: '360px',
    height: '640px',
    maxHeight: 'min(640px, calc(100vh - 220px))',
    overflow: 'hidden',
    paddingRight: tokens.spacingHorizontalXS,
    gap: tokens.spacingVerticalM,
  },
  dialogSurface: { maxWidth: '1180px', width: 'min(1180px, calc(100vw - 48px))', backgroundColor: tokens.colorNeutralBackground2, position: 'relative', padding: tokens.spacingVerticalXL },
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
  sectionHeading: { display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalM },
  sectionIcon: { width: '36px', height: '36px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground1, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 },
  sectionTitle: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  charCounter: { alignSelf: 'flex-end', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  tipLine: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  fieldWithCounter: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  subsectionHeader: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  infoBox: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke2}`,
  },
  stepRow: { display: 'flex', gap: tokens.spacingHorizontalS, alignItems: 'flex-start' },
  stepBadge: {
    width: '22px', height: '22px', borderRadius: tokens.borderRadiusCircular,
    backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand,
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0,
    fontSize: tokens.fontSizeBase200,
  },
  stepCopy: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalXXS },
  tabToggle: {
    display: 'inline-flex', gap: tokens.spacingHorizontalXS, padding: tokens.spacingVerticalXXS,
    backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusXLarge, alignSelf: 'flex-start',
  },
  footerSplit: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', width: '100%', gap: tokens.spacingHorizontalL },
  footerLeft: { display: 'flex', flexDirection: 'row', alignItems: 'center', gap: tokens.spacingHorizontalM, marginRight: 'auto' },
  footerActions: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalS },
  repositoryPanel: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalM },
  listBlock: { display: 'flex', flexDirection: 'column', gap: tokens.spacingVerticalS },
  listHeader: { display: 'flex', justifyContent: 'space-between', alignItems: 'center' },
  recentRow: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: tokens.spacingHorizontalS, padding: tokens.spacingVerticalS, borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1 },
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
  repoSelectorLabel: { display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS },
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
}) {
  const styles = useStyles();
  return (
    <Dialog open={open} onOpenChange={(_, state) => onOpenChange(state.open)}>
      <DialogTrigger disableButtonEnhancement>{trigger}</DialogTrigger>
      <DialogSurface className={styles.dialogSurface}>
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
          <DialogContent>
            <div className={styles.dialogTwoCol}>
              <div className={styles.dialogLeftCol}>{left}</div>
              <div className={styles.dialogRightCol}>{right}</div>
            </div>
          </DialogContent>
          <DialogActions>
            <div className={styles.footerSplit}>
              <div className={styles.footerLeft}>
                <Button appearance="outline" aria-label="No blueprint" onClick={onNoBlueprint}>⊘ No blueprint</Button>
                <Text className={styles.tipLine}>Start with an empty project and add agents later.</Text>
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
      <div className={styles.sectionHeading}>
        <span className={styles.sectionIcon}><DocumentRegular /></span>
        <div className={styles.sectionTitle}>
          <Text weight="semibold" size={400}>Project basics</Text>
        </div>
      </div>
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
      <div className={styles.fieldWithCounter}>
        <div className={styles.subsectionHeader}>
          <span aria-hidden="true">⚙</span>
          <Text weight="semibold">What do you want Agentweaver to help you accomplish?</Text>
        </div>
        <Text className={styles.tipLine}>Be specific about the problems you're trying to solve or the outcomes you want.</Text>
        <Textarea
          aria-label="Describe your project"
          value={goal}
          maxLength={1000}
          onChange={(_, v) => setGoal(v.value)}
          placeholder="e.g. Automate customer support tickets, build internal tools, create documentation, manage product roadmap…"
          resize="vertical"
          style={{ minHeight: 130 }}
        />
        <Counter value={goal} max={1000} />
        <Text className={styles.tipLine}>💡 Tip: The more context you provide, the better the blueprint.</Text>
      </div>
      <Button appearance="primary" icon={<SparkleRegular />} aria-label="Generate blueprint" disabled={!goal.trim() || generation.generating} onClick={() => void generation.generate(goal)}>
        {generation.generating ? 'Generating' : 'Generate Blueprint'}
      </Button>
      {generation.error && <MessageBar intent="error"><MessageBarBody>{generation.error}</MessageBarBody></MessageBar>}
      <Text className={styles.tipLine}>Our AI will generate a tailored squad, workflow, and review policy.</Text>
      <div className={styles.infoBox}>
        <Text weight="semibold">What happens next?</Text>
        {[
          ['We generate a custom blueprint', 'Agents, workflows, and review policies tailored to your goal.'],
          ['Customize to your needs', 'Adjust agents, tools, and workflows before creating.'],
          ['Create your project', 'Your project will be ready to go in seconds.'],
        ].map(([label, copy], index) => (
          <div key={label} className={styles.stepRow}>
            <span className={styles.stepBadge}>{index + 1}</span>
            <span className={styles.stepCopy}><Text weight="semibold">{label}</Text><Text className={styles.tipLine}>{copy}</Text></span>
          </div>
        ))}
      </div>
      {d.error && <MessageBar intent="error"><MessageBarBody>{d.error}</MessageBarBody></MessageBar>}
    </>
  );

  const right = (
    <BlueprintPanel
      active={d.open}
      tabs={['generated', 'templates']}
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
  const [recentCleared, setRecentCleared] = useState(false);
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
  const recentTime = (index: number) => ['3 days ago', '1 week ago', '2 weeks ago'][index] ?? 'recently';

  const filteredRepos = repos
    .filter(r => r.fullName?.toLowerCase().includes(repoFilter.toLowerCase()) ?? false)
    .sort((a, b) => {
      const nameA = (a.fullName?.split('/').pop() ?? '').toLowerCase();
      const nameB = (b.fullName?.split('/').pop() ?? '').toLowerCase();
      return nameA.localeCompare(nameB);
    });
  const recentRepos = recentCleared ? [] : repos.slice(0, 3);
  const visibleSources = showMoreSources ? accounts : accounts.slice(0, 5);

  const resetLocal = () => {
    d.reset(); setRepoFilter(''); setPasteRepo(''); setFolderName(''); setFolderEdited(false); setRecentCleared(false); setShowMoreSources(false); setGenerateDescription(''); generation.setGenerated(null);
  };

  const left = (
    <div className={styles.repositoryPanel}>
      <div className={styles.sectionHeading}>
        <span className={styles.sectionIcon}><span className={styles.githubMark}>GH</span></span>
        <div className={styles.sectionTitle}>
          <Text weight="semibold" size={400}>Repository</Text>
        </div>
      </div>

      <div className={styles.repoSelector}>
        <span className={styles.repoSelectorLabel}>
          <Text weight="semibold">Repository *</Text>
          <InfoRegular aria-label="Repository selector information" />
        </span>
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
          <MessageBarBody>Connect your GitHub account to list repositories, or paste any public owner/repo.</MessageBarBody>
          <MessageBarActions><Button size="small" onClick={() => { window.location.href = '/auth/github/authorize'; }}>Connect GitHub</Button></MessageBarActions>
        </MessageBar>
      )}
      {accountsError && <MessageBar intent="error"><MessageBarBody>Could not load accounts: {accountsError}</MessageBarBody><MessageBarActions><Button size="small" onClick={reloadAccounts}>Retry</Button></MessageBarActions></MessageBar>}
      {reposError && <MessageBar intent="error"><MessageBarBody>Could not load repositories: {reposError}</MessageBarBody><MessageBarActions><Button size="small" onClick={reloadRepos}>Retry</Button></MessageBarActions></MessageBar>}

      {recentRepos.length > 0 && (
        <div className={styles.listBlock}>
          <div className={styles.listHeader}><Text weight="semibold">Recent</Text><Button appearance="transparent" size="small" onClick={() => setRecentCleared(true)}>Clear</Button></div>
          {recentRepos.map((repo, index) => (
            <button key={repo.fullName} className={styles.recentRow} type="button" onClick={() => repo.fullName && applyRepo(repo.fullName)}>
              <span className={styles.accountOption}><span className={styles.githubMark}>GH</span><span><Text weight="semibold">{repoDisplayName(repo.fullName)}</Text><br /><Text className={styles.tipLine}>{recentTime(index)}</Text></span></span>
              <ChevronRightRegular />
            </button>
          ))}
        </div>
      )}

      {!authRequired && (
        <div className={styles.listBlock}>
          <div className={styles.listHeader}>
            <Text weight="semibold">My organizations</Text>
            {accounts.length > 5 && <Button appearance="transparent" size="small" onClick={() => setShowMoreSources(!showMoreSources)}>{showMoreSources ? 'Show less ⌃' : 'Show more ⌄'}</Button>}
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

      <div className={styles.infoBox}>
        <Text><InfoRegular /> You can import any public repository on GitHub. Private repositories require connection.</Text>
      </div>
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
    />
  );
}

function ProjectCard({ project, onOpen }: { project: Project; onOpen: () => void }) {
  const styles = useStyles();
  return (
    <Card className={styles.card}>
      <CardHeader
        header={<Title3>{project.name}</Title3>}
        action={
          <Badge
            appearance="filled"
            color={project.available ? 'success' : 'warning'}
          >
            {project.available ? 'Available' : 'Unavailable'}
          </Badge>
        }
      />
      <div className={styles.cardMeta}>
        {project.source_repository && (
          <Text size={200}>{project.source_repository}</Text>
        )}
        <Text className={styles.cardDir}>{project.working_directory}</Text>
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
  const { projects, loading, authError, loadError, errorMessage, appendProject } = useProjectList();
  const [dataDir, setDataDir] = useState<string | null>(null);
  const [workspaceAutoAssigned, setWorkspaceAutoAssigned] = useState(false);

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
    appendProject(project);
  };

  return (
    <div className={styles.root}>
      <PageHeader title="Projects" subtitle="Your Agentweaver projects." />

      {loading && <Spinner label="Loading projects" />}

      {!loading && authError && (
        <MessageBar intent="warning">
          <MessageBarBody>
            Sign in with GitHub to see your projects.
          </MessageBarBody>
          <MessageBarActions>
            <Button
              size="small"
              onClick={() => { window.location.href = '/auth/github/authorize'; }}
            >
              Sign in with GitHub
            </Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{errorMessage ?? 'Failed to load projects.'}</MessageBarBody>
        </MessageBar>
      )}

      {!loading && !loadError && !authError && projects.length === 0 && (
        <div className={styles.emptyState}>
          <Text>No projects yet. Create one to get started.</Text>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </div>
        </div>
      )}

      {!loading && projects.length > 0 && (
        <>
          <div className={styles.toolbar}>
            <CreateBlankDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
            <CreateFromGitHubDialog onCreated={handleCreated} dataDir={dataDir} workspaceAutoAssigned={workspaceAutoAssigned} />
          </div>
          <div className={styles.grid}>
            {projects.map((p) => (
              <ProjectCard
                key={p.project_id}
                project={p}
                onOpen={() => navigate(`/projects/${p.project_id}`)}
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
