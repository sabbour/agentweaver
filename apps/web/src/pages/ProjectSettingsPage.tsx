import { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import {
  Badge,
  Button,
  Checkbox,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Text,
  Title3,
  mergeClasses,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import {
  ArrowSyncRegular,
  ClipboardTaskListLtr24Regular,
  Delete24Regular,
  Settings24Regular,
  Shield24Regular,
} from '@fluentui/react-icons';
import type { ReactElement } from 'react';
import { apiClient } from '../api/apiClient';
import { ApiError } from '../api/client';
import { PageHeader } from '../components/PageHeader';
import type {
  Project,
  SandboxPolicy,
  UpdateProjectProviderSettingsRequest,
  ReviewPolicyListResponse,
  ReviewPolicyDetailDto,
} from '../api/types';

// Spec settings-subnav — project Settings restructured into a left in-page rail +
// right content pane. Only sections with a real Agentweaver backend are shipped
// (Principle VII): General, Sandbox policy, Review policy, Danger Zone. The rail is
// data-driven so more sections can be appended as their backends land.
type SectionId = 'general' | 'sandbox' | 'review' | 'danger';

interface SectionDef {
  id: SectionId;
  label: string;
  description: string;
  icon: ReactElement;
  danger?: boolean;
}

const SECTIONS: SectionDef[] = [
  {
    id: 'general',
    label: 'General',
    description: 'Project name, repository link, and the default model.',
    icon: <Settings24Regular />,
  },
  {
    id: 'sandbox',
    label: 'Sandbox policy',
    description: 'Control how agent commands execute and what they may reach.',
    icon: <Shield24Regular />,
  },
  {
    id: 'review',
    label: 'Review policy',
    description: 'Choose which review steps gate this project\u2019s work.',
    icon: <ClipboardTaskListLtr24Regular />,
  },
  {
    id: 'danger',
    label: 'Danger Zone',
    description: 'Irreversible actions for this project.',
    icon: <Delete24Regular />,
    danger: true,
  },
];

function isSectionId(value: string | null): value is SectionId {
  return value === 'general' || value === 'sandbox' || value === 'review' || value === 'danger';
}

const REVIEW_STEP_LABELS: Record<string, string> = {
  rubberduck: 'Rubberduck',
  rai: 'RAI',
  'human-review': 'Human review',
};

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
    maxWidth: '1100px',
  },
  breadcrumb: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground2,
  },
  breadcrumbLink: {
    color: tokens.colorBrandForeground1,
    textDecoration: 'none',
  },
  layout: {
    display: 'flex',
    gap: tokens.spacingHorizontalXXL,
    alignItems: 'flex-start',
  },
  rail: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
    width: '220px',
    flexShrink: 0,
    position: 'sticky',
    top: '0',
  },
  railItem: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    border: 'none',
    background: 'transparent',
    cursor: 'pointer',
    textAlign: 'left',
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
    fontFamily: tokens.fontFamilyBase,
    ':hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },
  },
  railItemActive: {
    backgroundColor: tokens.colorNeutralBackground1Selected,
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
  },
  railItemDanger: {
    color: tokens.colorPaletteRedForeground1,
    ':hover': {
      backgroundColor: tokens.colorPaletteRedBackground1,
      color: tokens.colorPaletteRedForeground1,
    },
  },
  railIcon: {
    display: 'flex',
    flexShrink: 0,
  },
  pane: {
    flex: 1,
    minWidth: 0,
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalL,
  },
  paneHeader: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXXS,
  },
  paneDescription: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase300,
  },
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    maxWidth: '640px',
  },
  subBlock: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    paddingBottom: tokens.spacingVerticalL,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  actions: {
    display: 'flex',
    gap: tokens.spacingHorizontalM,
    alignItems: 'center',
  },
  dangerSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingVerticalL,
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    borderRadius: tokens.borderRadiusMedium,
    maxWidth: '640px',
  },
  listBox: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
  },
  listItem: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    padding: `${tokens.spacingVerticalXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': {
      borderBottom: 'none',
    },
  },
  emptyNote: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    fontStyle: 'italic',
  },
  policyCard: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  policyCardActive: {
    border: `1px solid ${tokens.colorBrandStroke1}`,
  },
  policyHeader: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    flexWrap: 'wrap',
  },
  policyName: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase400,
  },
  policyBadges: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    alignItems: 'center',
    marginLeft: 'auto',
  },
  stepChips: {
    display: 'flex',
    gap: tokens.spacingHorizontalXS,
    flexWrap: 'wrap',
    alignItems: 'center',
  },
  policyList: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
});

export function ProjectSettingsPage() {
  const styles = useStyles();
  const { projectId } = useParams<{ projectId: string }>();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  // Selected settings section is deep-linked via ?section=… so it is shareable and
  // survives refresh; fall back to General for missing/unknown values.
  const sectionParam = searchParams.get('section');
  const activeSection: SectionId = isSectionId(sectionParam) ? sectionParam : 'general';

  const selectSection = (id: SectionId) => {
    const next = new URLSearchParams(searchParams);
    next.set('section', id);
    setSearchParams(next, { replace: true });
  };

  const [project, setProject] = useState<Project | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  // Model settings
  const [copilotModel, setCopilotModel] = useState('');
  const [savingModel, setSavingModel] = useState(false);
  const [modelError, setModelError] = useState<string | null>(null);
  const [modelSuccess, setModelSuccess] = useState(false);

  // Rename
  const [newName, setNewName] = useState('');
  const [savingRename, setSavingRename] = useState(false);
  const [renameError, setRenameError] = useState<string | null>(null);
  const [renameSuccess, setRenameSuccess] = useState(false);

  // Relink
  const [newDir, setNewDir] = useState('');
  const [savingRelink, setSavingRelink] = useState(false);
  const [relinkError, setRelinkError] = useState<string | null>(null);
  const [relinkSuccess, setRelinkSuccess] = useState(false);
  const [dataDir, setDataDir] = useState<string | null>(null);

  // Delete
  const [deleteConfirmed, setDeleteConfirmed] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [deleteError, setDeleteError] = useState<string | null>(null);

  // Sandbox policy
  const [sandboxPolicy, setSandboxPolicy] = useState<SandboxPolicy | null>(null);
  const [sandboxFetched, setSandboxFetched] = useState(false);
  const [sandboxError, setSandboxError] = useState<string | null>(null);
  const [savingSandbox, setSavingSandbox] = useState(false);
  const [sandboxSaveError, setSandboxSaveError] = useState<string | null>(null);
  const [sandboxSaveSuccess, setSandboxSaveSuccess] = useState(false);
  const sandboxLoading = project !== null && !sandboxFetched;

  // Review policy
  const [reviewList, setReviewList] = useState<ReviewPolicyListResponse | null>(null);
  const [reviewDetails, setReviewDetails] = useState<Record<string, ReviewPolicyDetailDto>>({});
  const [reviewLoading, setReviewLoading] = useState(true);
  const [reviewError, setReviewError] = useState<string | null>(null);
  const [reviewBusy, setReviewBusy] = useState(false);
  const [reviewSyncing, setReviewSyncing] = useState(false);
  const [reviewMessage, setReviewMessage] = useState<string | null>(null);

  const formatError = (err: unknown): string =>
    err instanceof ApiError
      ? `API error ${err.status}: ${err.body}`
      : err instanceof Error
        ? err.message
        : String(err);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    apiClient.getServerInfo()
      .then((info) => { if (!cancelled) setDataDir(info.data_directory); })
      .catch(() => {});
    apiClient.getProject(projectId)
      .then((p) => {
        if (!cancelled) {
          setProject(p);
          setCopilotModel(p.default_model_github_copilot ?? '');
          setNewName(p.name);
          setNewDir(p.working_directory);
        }
      })
      .catch((err) => {
        if (!cancelled) setLoadError(formatError(err));
      })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [projectId]);

  // Load review policies (list + per-policy step details for valid policies).
  const loadReviewPolicies = useMemo(() => async (signal?: { cancelled: boolean }) => {
    if (!projectId) return;
    const list = await apiClient.listReviewPolicies(projectId);
    const validNames = list.policies
      .map((p) => p.name)
      .filter((n): n is string => Boolean(n));
    const details = await Promise.all(
      validNames.map((name) =>
        apiClient.getReviewPolicy(projectId, name)
          .then((d) => [name, d] as const)
          .catch(() => null),
      ),
    );
    if (signal?.cancelled) return;
    const map: Record<string, ReviewPolicyDetailDto> = {};
    for (const entry of details) {
      if (entry) map[entry[0]] = entry[1];
    }
    setReviewList(list);
    setReviewDetails(map);
  }, [projectId]);

  useEffect(() => {
    if (!projectId) return;
    const signal = { cancelled: false };
    setReviewLoading(true);
    setReviewError(null);
    loadReviewPolicies(signal)
      .catch((err) => { if (!signal.cancelled) setReviewError(formatError(err)); })
      .finally(() => { if (!signal.cancelled) setReviewLoading(false); });
    return () => { signal.cancelled = true; };
  }, [projectId, loadReviewPolicies]);

  const handleSetActivePolicy = async (name: string) => {
    if (!projectId) return;
    setReviewBusy(true);
    setReviewError(null);
    setReviewMessage(null);
    try {
      const updated = await apiClient.setActiveReviewPolicy(projectId, name);
      setReviewList(updated);
      setReviewMessage(`Active review policy set to "${name}".`);
    } catch (err) {
      setReviewError(formatError(err));
    } finally {
      setReviewBusy(false);
    }
  };

  const handleSyncPolicies = async () => {
    if (!projectId) return;
    setReviewSyncing(true);
    setReviewError(null);
    setReviewMessage(null);
    try {
      // POST sync re-reads .agentweaver/review-policies from disk and returns the
      // refreshed list; then reload list + step details against that set.
      const refreshed = await apiClient.syncReviewPolicies(projectId);
      await loadReviewPolicies();
      setReviewMessage(`Synced ${refreshed.policies.length} review polic${refreshed.policies.length === 1 ? 'y' : 'ies'} from .agentweaver/review-policies/.`);
    } catch (err) {
      setReviewError(formatError(err));
    } finally {
      setReviewSyncing(false);
    }
  };

  const handleSaveModel = async () => {
    if (!projectId) return;
    setSavingModel(true);
    setModelError(null);
    setModelSuccess(false);
    try {
      const req: UpdateProjectProviderSettingsRequest = {};
      req.default_provider = 'github-copilot';
      if (copilotModel.trim()) req.default_model_github_copilot = copilotModel.trim();
      await apiClient.updateProjectProviderSettings(projectId, req);
      setModelSuccess(true);
    } catch (err) {
      setModelError(formatError(err));
    } finally {
      setSavingModel(false);
    }
  };

  useEffect(() => {
    if (!project?.working_directory) return;
    let cancelled = false;
    apiClient.getSandboxPolicy(project.working_directory)
      .then((p) => {
        if (!cancelled) {
          setSandboxPolicy(p);
          setSandboxFetched(true);
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setSandboxFetched(true);
          setSandboxError(formatError(err));
        }
      });
    return () => { cancelled = true; };
  }, [project?.working_directory]);

  const handleSaveSandbox = async () => {
    if (!sandboxPolicy) return;
    setSavingSandbox(true);
    setSandboxSaveError(null);
    setSandboxSaveSuccess(false);
    try {
      // Round-trip the FULL policy (including allowed_repository_roots and
      // destructive_command_patterns) so omitted fields are never dropped.
      const updated = await apiClient.updateSandboxPolicy(sandboxPolicy);
      setSandboxPolicy(updated);
      setSandboxSaveSuccess(true);
    } catch (err) {
      setSandboxSaveError(formatError(err));
    } finally {
      setSavingSandbox(false);
    }
  };

  const handleRename = async () => {
    if (!projectId || !newName.trim()) return;
    setSavingRename(true);
    setRenameError(null);
    setRenameSuccess(false);
    try {
      await apiClient.renameProject(projectId, newName.trim());
      setProject((prev) => prev ? { ...prev, name: newName.trim() } : prev);
      setRenameSuccess(true);
    } catch (err) {
      setRenameError(formatError(err));
    } finally {
      setSavingRename(false);
    }
  };

  const handleRelink = async () => {
    if (!projectId || !newDir.trim()) return;
    setSavingRelink(true);
    setRelinkError(null);
    setRelinkSuccess(false);
    try {
      await apiClient.relinkProject(projectId, newDir.trim());
      setProject((prev) => prev ? { ...prev, working_directory: newDir.trim(), available: true } : prev);
      setRelinkSuccess(true);
    } catch (err) {
      setRelinkError(formatError(err));
    } finally {
      setSavingRelink(false);
    }
  };

  const handleDelete = async () => {
    if (!projectId || !deleteConfirmed) return;
    setDeleting(true);
    setDeleteError(null);
    try {
      await apiClient.deleteProject(projectId);
      navigate('/');
    } catch (err) {
      setDeleteError(formatError(err));
    } finally {
      setDeleting(false);
    }
  };

  if (!projectId) return null;

  const activeDef = SECTIONS.find((s) => s.id === activeSection) ?? SECTIONS[0];

  return (
    <div className={styles.root}>
      <PageHeader
        title="Project settings"
        subtitle="Project configuration and pickup behavior."
        breadcrumb={
          <div className={styles.breadcrumb}>
            <Link to="/" className={styles.breadcrumbLink}>Projects</Link>
            <span>/</span>
            <Link to={`/projects/${projectId}`} className={styles.breadcrumbLink}>{project?.name ?? projectId}</Link>
            <span>/</span>
            <span>Settings</span>
          </div>
        }
      />

      {loading && <Spinner label="Loading project" />}

      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{loadError}</MessageBarBody>
        </MessageBar>
      )}

      {project && (
        <div className={styles.layout}>
          <nav className={styles.rail} aria-label="Settings sections">
            {SECTIONS.map((section) => (
              <button
                key={section.id}
                type="button"
                className={mergeClasses(
                  styles.railItem,
                  section.danger && styles.railItemDanger,
                  section.id === activeSection && styles.railItemActive,
                )}
                aria-current={section.id === activeSection ? 'page' : undefined}
                onClick={() => selectSection(section.id)}
              >
                <span className={styles.railIcon}>{section.icon}</span>
                <span>{section.label}</span>
              </button>
            ))}
          </nav>

          <div className={styles.pane}>
            <div className={styles.paneHeader}>
              <Title3>{activeDef.label}</Title3>
              <Text className={styles.paneDescription}>{activeDef.description}</Text>
            </div>

            {activeSection === 'general' && (
              <div className={styles.section}>
                <div className={styles.subBlock}>
                  <Title3>Rename project</Title3>
                  <Field label="Name">
                    <Input value={newName} onChange={(_, v) => setNewName(v.value)} />
                  </Field>
                  <div className={styles.actions}>
                    <Button
                      appearance="primary"
                      disabled={savingRename || !newName.trim() || newName.trim() === project.name}
                      onClick={() => void handleRename()}
                    >
                      {savingRename ? 'Saving' : 'Save'}
                    </Button>
                    {savingRename && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                  {renameError && (
                    <MessageBar intent="error"><MessageBarBody>{renameError}</MessageBarBody></MessageBar>
                  )}
                  {renameSuccess && (
                    <MessageBar intent="success"><MessageBarBody>Project renamed.</MessageBarBody></MessageBar>
                  )}
                </div>

                <div className={styles.subBlock}>
                  <Title3>Relink repository</Title3>
                  <Text>Update the server-side path if the repository has moved on the Agentweaver server.</Text>
                  <Field
                    label="Repository path"
                    hint={dataDir
                      ? `Path to a git repository accessible from the server's data folder: ${dataDir}`
                      : 'Absolute path to a git repository on the machine running the Agentweaver server'}
                  >
                    <Input value={newDir} onChange={(_, v) => setNewDir(v.value)} />
                  </Field>
                  <div className={styles.actions}>
                    <Button
                      appearance="primary"
                      disabled={savingRelink || !newDir.trim() || newDir.trim() === project.working_directory}
                      onClick={() => void handleRelink()}
                    >
                      {savingRelink ? 'Saving' : 'Save'}
                    </Button>
                    {savingRelink && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                  {relinkError && (
                    <MessageBar intent="error"><MessageBarBody>{relinkError}</MessageBarBody></MessageBar>
                  )}
                  {relinkSuccess && (
                    <MessageBar intent="success"><MessageBarBody>Project relinked.</MessageBarBody></MessageBar>
                  )}
                </div>

                <div className={styles.section}>
                  <Title3>Default model</Title3>
                  <Field label="GitHub Copilot model">
                    <Input value={copilotModel} onChange={(_, v) => setCopilotModel(v.value)} placeholder="e.g. gpt-4o" />
                  </Field>
                  <div className={styles.actions}>
                    <Button appearance="primary" disabled={savingModel} onClick={() => void handleSaveModel()}>
                      {savingModel ? 'Saving' : 'Save'}
                    </Button>
                    {savingModel && <Spinner size="extra-tiny" aria-hidden="true" />}
                  </div>
                  {modelError && (
                    <MessageBar intent="error"><MessageBarBody>{modelError}</MessageBarBody></MessageBar>
                  )}
                  {modelSuccess && (
                    <MessageBar intent="success"><MessageBarBody>Model settings saved.</MessageBarBody></MessageBar>
                  )}
                </div>
              </div>
            )}

            {activeSection === 'sandbox' && (
              <div className={styles.section}>
                {sandboxLoading && <Spinner size="extra-tiny" label="Loading policy" />}
                {sandboxError && (
                  <MessageBar intent="error"><MessageBarBody>{sandboxError}</MessageBarBody></MessageBar>
                )}
                {sandboxPolicy && (
                  <>
                    <Field label="Shell execution">
                      <Switch
                        label={sandboxPolicy.shell_enabled ? 'Enabled' : 'Disabled'}
                        checked={sandboxPolicy.shell_enabled}
                        onChange={(_, data) =>
                          setSandboxPolicy((prev) => prev ? { ...prev, shell_enabled: data.checked } : prev)
                        }
                      />
                    </Field>
                    <Field
                      label="Sandbox enabled"
                      hint="When off, commands run directly on the host with no isolation layer."
                    >
                      <Switch
                        label={sandboxPolicy.direct ? 'Off — no isolation layer' : 'On — commands run in the sandbox'}
                        checked={!sandboxPolicy.direct}
                        onChange={(_, data) =>
                          setSandboxPolicy((prev) => prev ? { ...prev, direct: !data.checked } : prev)
                        }
                      />
                    </Field>
                    <Field
                      label="Outbound network"
                      hint={sandboxPolicy.direct ? 'Only applies when the sandbox is enabled.' : undefined}
                    >
                      <Switch
                        label={sandboxPolicy.network_enabled ? 'Enabled' : 'Blocked'}
                        checked={sandboxPolicy.network_enabled}
                        disabled={sandboxPolicy.direct}
                        onChange={(_, data) =>
                          setSandboxPolicy((prev) => prev ? { ...prev, network_enabled: data.checked } : prev)
                        }
                      />
                    </Field>
                    <Field label="Allowed repository roots">
                      <div className={styles.listBox}>
                        {sandboxPolicy.allowed_repository_roots.length === 0 ? (
                          <Text className={styles.emptyNote}>None configured</Text>
                        ) : (
                          sandboxPolicy.allowed_repository_roots.map((root, i) => (
                            <div key={i} className={styles.listItem}>{root}</div>
                          ))
                        )}
                      </div>
                    </Field>
                    <Field label="Blocked command patterns">
                      <div className={styles.listBox}>
                        {sandboxPolicy.destructive_command_patterns.length === 0 ? (
                          <Text className={styles.emptyNote}>None configured</Text>
                        ) : (
                          sandboxPolicy.destructive_command_patterns.map((pat, i) => (
                            <div key={i} className={styles.listItem}>{pat}</div>
                          ))
                        )}
                      </div>
                    </Field>
                    <div className={styles.actions}>
                      <Button appearance="primary" disabled={savingSandbox} onClick={() => void handleSaveSandbox()}>
                        {savingSandbox ? 'Saving' : 'Save'}
                      </Button>
                      {savingSandbox && <Spinner size="extra-tiny" aria-hidden="true" />}
                    </div>
                    {sandboxSaveError && (
                      <MessageBar intent="error"><MessageBarBody>{sandboxSaveError}</MessageBarBody></MessageBar>
                    )}
                    {sandboxSaveSuccess && (
                      <MessageBar intent="success"><MessageBarBody>Sandbox policy saved.</MessageBarBody></MessageBar>
                    )}
                  </>
                )}
              </div>
            )}

            {activeSection === 'review' && (
              <div className={styles.section}>
                <div className={styles.actions}>
                  <Button
                    appearance="secondary"
                    icon={reviewSyncing ? <Spinner size="extra-tiny" aria-hidden="true" /> : <ArrowSyncRegular />}
                    disabled={reviewSyncing}
                    onClick={() => void handleSyncPolicies()}
                  >
                    {reviewSyncing ? 'Syncing' : 'Sync'}
                  </Button>
                  {reviewList && (
                    <Text className={styles.paneDescription}>
                      Active policy: <strong>{reviewList.active_policy_name}</strong>
                    </Text>
                  )}
                </div>

                {reviewMessage && (
                  <MessageBar intent="success"><MessageBarBody>{reviewMessage}</MessageBarBody></MessageBar>
                )}
                {reviewError && (
                  <MessageBar intent="error"><MessageBarBody>{reviewError}</MessageBarBody></MessageBar>
                )}
                {reviewLoading && <Spinner size="extra-tiny" label="Loading review policies" />}

                {!reviewLoading && reviewList && reviewList.policies.length === 0 && (
                  <Text className={styles.emptyNote}>
                    No review policies found. Sync to load from .agentweaver/review-policies/.
                  </Text>
                )}

                {!reviewLoading && reviewList && reviewList.policies.length > 0 && (
                  <div className={styles.policyList}>
                    {reviewList.policies.map((policy, index) => {
                      const detail = policy.name ? reviewDetails[policy.name] : undefined;
                      return (
                        <div
                          key={policy.name ?? `invalid-${index}`}
                          className={mergeClasses(styles.policyCard, policy.is_active && styles.policyCardActive)}
                        >
                          <div className={styles.policyHeader}>
                            <span className={styles.policyName}>{policy.name ?? 'Unnamed policy'}</span>
                            <div className={styles.policyBadges}>
                              {policy.is_active && <Badge appearance="filled" color="brand">Active</Badge>}
                              <Badge appearance="outline" color="informative">
                                {policy.is_built_in ? 'Built-in' : 'Custom'}
                              </Badge>
                              <Badge appearance="tint" color={policy.valid ? 'success' : 'danger'}>
                                {policy.valid ? 'Valid' : 'Invalid'}
                              </Badge>
                            </div>
                          </div>

                          {policy.description && <Text>{policy.description}</Text>}

                          {detail && detail.steps.length > 0 && (
                            <div className={styles.stepChips}>
                              {detail.steps.map((step, i) => (
                                <Badge key={i} appearance="tint" color="subtle">
                                  {step.label || REVIEW_STEP_LABELS[step.kind] || step.kind}
                                </Badge>
                              ))}
                            </div>
                          )}

                          <Text className={styles.emptyNote}>Source: {policy.source}</Text>

                          {!policy.valid && policy.error && (
                            <MessageBar intent="error"><MessageBarBody>{policy.error}</MessageBarBody></MessageBar>
                          )}

                          {policy.name && !policy.is_active && (
                            <div className={styles.actions}>
                              <Button
                                appearance="secondary"
                                size="small"
                                disabled={reviewBusy || !policy.valid}
                                onClick={() => void handleSetActivePolicy(policy.name as string)}
                              >
                                Set as active
                              </Button>
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}
              </div>
            )}

            {activeSection === 'danger' && (
              <div className={styles.dangerSection}>
                <Title3>Delete project</Title3>
                <Text>This action cannot be undone. The project and all its run history will be permanently removed.</Text>
                <Checkbox
                  label="I understand this is permanent"
                  checked={deleteConfirmed}
                  onChange={(_, data) => setDeleteConfirmed(!!data.checked)}
                />
                <div className={styles.actions}>
                  <Button
                    appearance="primary"
                    style={{ backgroundColor: tokens.colorPaletteRedBackground3, borderColor: tokens.colorPaletteRedBorder2 }}
                    disabled={!deleteConfirmed || deleting}
                    onClick={() => void handleDelete()}
                  >
                    {deleting ? 'Deleting' : 'Delete project'}
                  </Button>
                  {deleting && <Spinner size="extra-tiny" aria-hidden="true" />}
                </div>
                {deleteError && (
                  <MessageBar intent="error"><MessageBarBody>{deleteError}</MessageBarBody></MessageBar>
                )}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
